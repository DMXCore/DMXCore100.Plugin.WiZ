using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace DMXCore100.WiZ;

internal delegate Task<IReadOnlyList<WizLight>> WizDiscoverFunc(bool refresh, CancellationToken cancellationToken);

internal delegate ValueTask WizDatagramSender(
    IPEndPoint endpoint,
    ReadOnlyMemory<byte> packet,
    CancellationToken cancellationToken);

/// <summary>
/// One-shot WiZ LAN discovery: broadcast a registration probe on UDP 38899,
/// collect the replies (MAC + source IP), then ask each light for its
/// system config so the Outputs Discover list can show what kind of module
/// it is. Destination value is the device IP. Concurrent callers share one
/// scan; the last result is cached until the next refresh.
/// </summary>
internal sealed class WizDiscovery
{
    private readonly WizDiscoverFunc? discoverOverride;
    private readonly object gate = new();
    private IReadOnlyList<WizLight>? cached;
    private Task<IReadOnlyList<WizLight>>? inFlight;

    public WizDiscovery(WizDiscoverFunc? discoverOverride = null)
    {
        this.discoverOverride = discoverOverride;
    }

    public async Task<IReadOnlyList<WizLight>> GetLightsAsync(bool refresh, CancellationToken cancellationToken)
    {
        TaskCompletionSource<IReadOnlyList<WizLight>>? owner = null;
        Task<IReadOnlyList<WizLight>> pending;
        lock (this.gate)
        {
            if (!refresh && this.cached != null)
            {
                return this.cached;
            }

            if (this.inFlight != null)
            {
                pending = this.inFlight;
            }
            else
            {
                owner = new TaskCompletionSource<IReadOnlyList<WizLight>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                this.inFlight = owner.Task;
                pending = owner.Task;
            }
        }

        if (owner != null)
        {
            _ = this.RunScanAsync(owner, refresh);
        }

        return await pending.WaitAsync(cancellationToken);
    }

    public WizLight? LightFor(string ip)
    {
        return this.cached?.FirstOrDefault(light =>
            string.Equals(light.Ip, ip, StringComparison.OrdinalIgnoreCase));
    }

    private async Task RunScanAsync(TaskCompletionSource<IReadOnlyList<WizLight>> owner, bool refresh)
    {
        // The scan runs on its own token: the caller that started it may
        // cancel, but the other callers sharing the scan still want the result
        using var lifetime = new CancellationTokenSource();
        try
        {
            IReadOnlyList<WizLight> lights = this.discoverOverride != null
                ? await this.discoverOverride(refresh, lifetime.Token)
                : await BroadcastRegistrationAsync(
                    TimeSpan.FromMilliseconds(WizConstants.DiscoveryTimeoutMs),
                    lifetime.Token);
            lights = lights.Where(static light => light.IsLight).ToArray();
            lock (this.gate)
            {
                this.cached = lights;
            }

            owner.TrySetResult(lights);
        }
        catch (Exception ex)
        {
            owner.TrySetException(ex);
        }
        finally
        {
            lock (this.gate)
            {
                this.inFlight = null;
            }
        }
    }

    internal static async Task<IReadOnlyList<WizLight>> BroadcastRegistrationAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var lights = new Dictionary<string, WizLight>(StringComparer.OrdinalIgnoreCase);
        using var udp = CreateUdp();
        using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task listen = ListenAsync(udp, lights, listenCts.Token);

        foreach (IPAddress broadcast in DiscoveryBroadcastAddresses())
        {
            Send(udp, WizMessages.Registration, broadcast);
        }

        try
        {
            await Task.Delay(timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await StopListen(listenCts, listen);
            throw;
        }

        WizLight[] snapshot;
        lock (lights)
        {
            snapshot = [.. lights.Values];
        }

        foreach (WizLight light in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Send(udp, WizMessages.GetSystemConfig, IPAddress.Parse(light.Ip));
        }

        if (snapshot.Length > 0)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(WizConstants.SystemConfigTimeoutMs), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await StopListen(listenCts, listen);
                throw;
            }
        }

        await StopListen(listenCts, listen);

        lock (lights)
        {
            return lights.Values
                .Where(static light => light.IsLight)
                .OrderBy(static light => light.Ip, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private static async Task ListenAsync(
        UdpClient udp,
        Dictionary<string, WizLight> lights,
        CancellationToken cancellationToken)
    {
        int consecutiveErrors = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                // Windows reports ICMP port-unreachable of an earlier send as
                // a receive failure; keep listening for the real replies, but
                // give up on a socket that keeps failing
                if (++consecutiveErrors >= 10)
                {
                    break;
                }

                continue;
            }

            consecutiveErrors = 0;
            HandleReply(result.Buffer, result.RemoteEndPoint.Address.ToString(), lights);
        }
    }

    internal static void HandleReply(byte[] datagram, string ip, Dictionary<string, WizLight> lights)
    {
        if (!WizMessages.TryParse(datagram, out string method, out JsonElement result))
        {
            return;
        }

        string? mac = WizMessages.GetMac(result);
        lock (lights)
        {
            WizLight? light = null;
            if (mac != null && !lights.TryGetValue(mac, out light))
            {
                light = new WizLight(mac, ip);
                lights[mac] = light;
            }

            // Config replies do not always repeat the MAC: match on IP then
            light ??= lights.Values.FirstOrDefault(item =>
                string.Equals(item.Ip, ip, StringComparison.OrdinalIgnoreCase));
            if (light == null)
            {
                return;
            }

            light.Ip = ip;
            ApplyReply(light, method, result);
        }
    }

    internal static void ApplyReply(WizLight light, string method, JsonElement result)
    {
        if (method != "getSystemConfig")
        {
            return;
        }

        string? moduleName = WizMessages.GetString(result, "moduleName");
        if (!string.IsNullOrWhiteSpace(moduleName))
        {
            light.ModuleName = moduleName;
        }

        string? firmware = WizMessages.GetString(result, "fwVersion");
        if (!string.IsNullOrWhiteSpace(firmware))
        {
            light.FirmwareVersion = firmware;
        }
    }

    internal static string DestinationLabel(WizLight light)
    {
        string kind = WizLight.KindLabel(light.Kind);
        string module = string.IsNullOrWhiteSpace(light.ModuleName) ? light.Mac : $"{light.ModuleName}, {light.Mac}";
        return $"{kind} ({light.Ip}, {module})";
    }

    internal static IReadOnlyList<IPAddress> DiscoveryBroadcastAddresses()
    {
        var addresses = new List<IPAddress>();
        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork
                    || unicast.IPv4Mask is not { } mask)
                {
                    continue;
                }

                addresses.Add(DirectedBroadcast(unicast.Address, mask));
            }
        }

        // The limited broadcast reaches lights on interfaces without a mask;
        // WiZ modules answer either
        addresses.Add(IPAddress.Broadcast);
        return addresses.Distinct().ToArray();
    }

    internal static IPAddress DirectedBroadcast(IPAddress address, IPAddress mask)
    {
        byte[] ip = address.GetAddressBytes();
        byte[] netmask = mask.GetAddressBytes();
        if (ip.Length != 4 || netmask.Length != 4)
        {
            throw new ArgumentException("Directed broadcast requires IPv4 address and mask.");
        }

        byte[] broadcast = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            broadcast[i] = (byte)(ip[i] | (byte)~netmask[i]);
        }

        return new IPAddress(broadcast);
    }

    private static UdpClient CreateUdp()
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        client.EnableBroadcast = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        WizSessionIo.IgnoreConnectionReset(client);
        return client;
    }

    private static void Send(UdpClient udp, byte[] packet, IPAddress ip)
    {
        try
        {
            udp.Send(packet, packet.Length, new IPEndPoint(ip, WizConstants.Port));
        }
        catch (SocketException)
        {
            // An interface that refuses broadcast must not abort the scan on
            // the others
        }
    }

    private static async Task StopListen(CancellationTokenSource listenCts, Task listen)
    {
        await listenCts.CancelAsync();
        try
        {
            await listen;
        }
        catch (OperationCanceledException)
        {
        }
    }
}

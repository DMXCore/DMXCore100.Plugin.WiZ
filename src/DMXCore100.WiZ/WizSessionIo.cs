using System.Net;
using System.Net.Sockets;
using DMXCore.PluginSdk;

namespace DMXCore100.WiZ;

/// <summary>
/// The UDP socket of one output session (or the test sender override).
/// Commands are fire-and-forget: the light's <c>{"result":{"success":true}}</c>
/// replies are left unread.
/// </summary>
internal sealed class WizSessionIo : IAsyncDisposable
{
    // SIO_UDP_CONNRESET: without this Windows turns an ICMP port-unreachable
    // (light offline / rebooting) into a SocketException on the next
    // operation of the same socket
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private readonly UdpClient? udp;

    public WizSessionIo(IPEndPoint endpoint, WizDatagramSender? sender)
    {
        if (sender != null)
        {
            Send = sender;
        }
        else
        {
            this.udp = new UdpClient(endpoint.AddressFamily);
            IgnoreConnectionReset(this.udp);
            UdpClient socket = this.udp;
            Send = async (ep, packet, ct) =>
            {
                await socket.SendAsync(packet, ep, ct);
            };
        }
    }

    public WizDatagramSender Send { get; }

    public ValueTask DisposeAsync()
    {
        this.udp?.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static void IgnoreConnectionReset(UdpClient client)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            client.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }
        catch (SocketException)
        {
        }
        catch (PlatformNotSupportedException)
        {
        }
    }
}

internal static class WizMapping
{
    public static IPEndPoint RequireEndpoint(PluginOutputMappingConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.DestinationAddress))
        {
            throw new InvalidOperationException("Destination address (the WiZ light IP) is required.");
        }

        if (!IPAddress.TryParse(config.DestinationAddress.Trim(), out IPAddress? ip))
        {
            throw new InvalidOperationException(
                $"Destination address '{config.DestinationAddress}' is not a valid IP address.");
        }

        return new IPEndPoint(ip, WizConstants.Port);
    }
}

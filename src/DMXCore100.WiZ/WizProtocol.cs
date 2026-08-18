using System.Net;
using System.Net.Sockets;
using DMXCore.PluginSdk;

namespace DMXCore100.WiZ;

/// <summary>
/// One WiZ output protocol: a channel slice laid out per its
/// <see cref="WizMode"/> becomes a <c>setPilot</c> UDP datagram to port
/// 38899. The host rate-limits, dedupes, and coalesces latest-wins.
/// </summary>
internal sealed class WizProtocol : IPluginOutputProtocol
{
    private readonly WizMode mode;
    private readonly WizDiscovery discovery;
    private readonly WizDatagramSender? sender;

    public WizProtocol(WizMode mode, WizDiscovery discovery, WizDatagramSender? sender = null)
    {
        this.mode = mode;
        this.discovery = discovery;
        this.sender = sender;
    }

    public int GetChannelCount(PluginOutputMappingConfig config) => this.mode.ChannelCount;

    public Task<IPluginOutputSession> OpenSessionAsync(
        PluginOutputMappingConfig config,
        CancellationToken cancellationToken)
    {
        IPEndPoint endpoint = WizMapping.RequireEndpoint(config);
        return Task.FromResult<IPluginOutputSession>(new WizSession(this.mode, endpoint, this.sender));
    }

    public async Task<IReadOnlyList<PluginOutputDestinationOption>?> GetDestinationOptionsAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<WizLight> lights = await this.discovery.GetLightsAsync(refresh, cancellationToken);
        return lights
            .Where(light => light.Supports(this.mode.Kind))
            .Select(static light => new PluginOutputDestinationOption(
                light.Ip,
                WizDiscovery.DestinationLabel(light)))
            .ToArray();
    }
}

internal sealed class WizSession : IPluginOutputSession
{
    private readonly WizMode mode;
    private readonly IPEndPoint endpoint;
    private readonly WizSessionIo io;

    public WizSession(WizMode mode, IPEndPoint endpoint, WizDatagramSender? sender)
    {
        this.mode = mode;
        this.endpoint = endpoint;
        this.io = new WizSessionIo(endpoint, sender);
    }

    public async Task<bool> SendAsync(ReadOnlyMemory<byte> channelValues, CancellationToken cancellationToken)
    {
        if (channelValues.Length < this.mode.ChannelCount)
        {
            return false;
        }

        WizPilot pilot = this.mode.ToPilot(channelValues.Span);

        try
        {
            await this.io.Send(this.endpoint, pilot.ToDatagram(), cancellationToken);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync() => this.io.DisposeAsync();
}

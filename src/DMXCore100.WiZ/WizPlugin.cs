using DMXCore.PluginSdk;

namespace DMXCore100.WiZ;

/// <summary>
/// WiZ output plugin: registers color and white output protocols plus the
/// matching fixture profiles so WiZ lights can be mapped on the Outputs page
/// and patched like any other fixture. Every update is a JSON
/// <c>setPilot</c> datagram to UDP 38899; the host rate-limits, dedupes,
/// and coalesces latest-wins.
/// </summary>
public class WizPlugin : IPlugin
{
    public const string ColorProtocolId = "WIZ_COLOR";
    public const string ColorRgbwCtProtocolId = "WIZ_COLOR_RGBW_CT";
    public const string ColorRgbCwWwProtocolId = "WIZ_COLOR_RGBCW";
    public const string WhiteCtProtocolId = "WIZ_WHITE_CT";
    public const string WhiteProtocolId = "WIZ_WHITE";
    public const string ColorProfileCode = "WIZ_COLOR";
    public const string WhiteProfileCode = "WIZ_WHITE";
    public const string PortType = "WIZ";

    private readonly List<IDisposable> registrations = [];
    private readonly WizDiscoverFunc? discoverOverride;
    private readonly WizDatagramSender? sendOverride;

    public WizPlugin()
        : this(null, null)
    {
    }

    internal WizPlugin(WizDiscoverFunc? discoverOverride, WizDatagramSender? sendOverride)
    {
        this.discoverOverride = discoverOverride;
        this.sendOverride = sendOverride;
        Info = new()
        {
            // Id/Name/Version come from the csproj (PluginId,
            // PluginDisplayName, Version) via the SDK-generated
            // PluginBuildInfo, always in sync with the generated manifest.json
            Id = PluginBuildInfo.Id,
            Name = PluginBuildInfo.Name,
            Version = PluginBuildInfo.Version,
            Description = "Drives WiZ WiFi color and tunable-white lights from DMX over the WiZ local UDP protocol.",
        };
    }

    public PluginInfo Info { get; }

    public Task InitializeAsync(IPluginHost host, CancellationToken cancellationToken)
    {
        var discovery = new WizDiscovery(this.discoverOverride);

        // One profile personality per protocol, same name, so the fixture
        // editor can prefill the personality from a mapping
        this.registrations.Add(host.Outputs.RegisterFixtureProfile(Profile(
            ColorProfileCode,
            "Color Bulb",
            WizModeKind.Color)));
        this.registrations.Add(host.Outputs.RegisterFixtureProfile(Profile(
            WhiteProfileCode,
            "White Bulb",
            WizModeKind.White)));

        foreach (WizMode mode in WizMode.All)
        {
            this.registrations.Add(host.Outputs.RegisterOutputProtocol(
                Descriptor(mode),
                new WizProtocol(mode, discovery, this.sendOverride)));
        }

        host.SetConnectionState(true, "WiZ output ready");
        return Task.CompletedTask;
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        foreach (IDisposable registration in this.registrations)
        {
            registration.Dispose();
        }

        this.registrations.Clear();
        return Task.CompletedTask;
    }

    private static PluginFixtureProfileDescriptor Profile(string code, string name, WizModeKind kind) =>
        new()
        {
            Code = code,
            Name = name,
            Manufacturer = "WiZ",
            Personalities = WizMode.All
                .Where(mode => mode.Kind == kind)
                .Select(static mode => new PluginFixturePersonality
                {
                    Name = mode.Personality,
                    Channels = mode.Channels,
                })
                .ToArray(),
        };

    private static OutputProtocolDescriptor Descriptor(WizMode mode) =>
        new()
        {
            Id = mode.ProtocolId,
            DisplayName = mode.DisplayName,
            PortType = PortType,
            PortTypeDisplayName = "WiZ",
            MaxUpdatesPerSecond = WizConstants.MaxUpdatesPerSecond,
            SupportsDestinationDiscovery = true,
            SuggestedProfileCode = mode.ProfileCode,
            SuggestedPersonality = mode.Personality,
        };
}

using DMXCore.PluginSdk;

namespace DMXCore100.WiZ;

/// <summary>
/// The kind of WiZ device a mode drives; decides which discovered lights are
/// offered for it.
/// </summary>
internal enum WizModeKind
{
    /// <summary>Full-color (RGB + cool/warm white) bulbs and strips.</summary>
    Color,

    /// <summary>Any light with white output: color, tunable-white and dimmable bulbs.</summary>
    White,
}

/// <summary>
/// One output protocol of the plugin: its DMX channel layout (also the
/// personality of the fixture profile it suggests) and how a channel slice
/// becomes a <see cref="WizPilot"/>.
/// </summary>
internal sealed record WizMode(
    string ProtocolId,
    string ProfileCode,
    string Personality,
    string DisplayName,
    WizModeKind Kind,
    IReadOnlyList<PluginFixtureFunction> Channels)
{
    /// <summary>RGB: white is mixed in the color, the white LEDs stay off.</summary>
    public static readonly WizMode Rgb = new(
        WizPlugin.ColorProtocolId,
        WizPlugin.ColorProfileCode,
        "RGB",
        "WiZ Color RGB",
        WizModeKind.Color,
        [PluginFixtureFunction.Red, PluginFixtureFunction.Green, PluginFixtureFunction.Blue]);

    /// <summary>
    /// RGB + White + ColorTemperature: the White level is split across the
    /// bulb's cool and warm white LEDs by the CT channel (0 = warm, 255 = cool).
    /// </summary>
    public static readonly WizMode RgbwCt = new(
        WizPlugin.ColorRgbwCtProtocolId,
        WizPlugin.ColorProfileCode,
        "RGBW+CT",
        "WiZ Color RGBW + CT",
        WizModeKind.Color,
        [
            PluginFixtureFunction.Red,
            PluginFixtureFunction.Green,
            PluginFixtureFunction.Blue,
            PluginFixtureFunction.White,
            PluginFixtureFunction.ColorTemperature,
        ]);

    /// <summary>RGB + CoolWhite + WarmWhite: the bulb's five channels directly.</summary>
    public static readonly WizMode RgbCwWw = new(
        WizPlugin.ColorRgbCwWwProtocolId,
        WizPlugin.ColorProfileCode,
        "RGB+CW+WW",
        "WiZ Color RGB + Cool/Warm White",
        WizModeKind.Color,
        [
            PluginFixtureFunction.Red,
            PluginFixtureFunction.Green,
            PluginFixtureFunction.Blue,
            PluginFixtureFunction.CoolWhite,
            PluginFixtureFunction.WarmWhite,
        ]);

    /// <summary>Intensity + ColorTemperature for tunable-white bulbs (kelvin mode).</summary>
    public static readonly WizMode TunableWhite = new(
        WizPlugin.WhiteCtProtocolId,
        WizPlugin.WhiteProfileCode,
        "Dimmer+CT",
        "WiZ Tunable White (Dimmer + CT)",
        WizModeKind.White,
        [PluginFixtureFunction.Intensity, PluginFixtureFunction.ColorTemperature]);

    /// <summary>Intensity only for dimmable-white bulbs.</summary>
    public static readonly WizMode DimmableWhite = new(
        WizPlugin.WhiteProtocolId,
        WizPlugin.WhiteProfileCode,
        "Dimmer",
        "WiZ Dimmable White",
        WizModeKind.White,
        [PluginFixtureFunction.Intensity]);

    /// <summary>
    /// Every mode, in the order the protocols are registered and the profile
    /// personalities are listed.
    /// </summary>
    public static readonly IReadOnlyList<WizMode> All =
    [
        Rgb,
        RgbwCt,
        RgbCwWw,
        TunableWhite,
        DimmableWhite,
    ];

    public int ChannelCount => Channels.Count;

    /// <summary>
    /// Convert one channel slice (at least <see cref="ChannelCount"/> bytes)
    /// to the pilot to send.
    /// </summary>
    public WizPilot ToPilot(ReadOnlySpan<byte> channels)
    {
        if (ReferenceEquals(this, Rgb))
        {
            return WizColor.Color(Read(channels, 0), Read(channels, 1), Read(channels, 2), 0.0, 0.0);
        }

        if (ReferenceEquals(this, RgbwCt))
        {
            // Crossfade the white level over the two white LED sets: warm
            // only at CT 0, both fully at the midpoint, cool only at CT 255
            double white = Read(channels, 3);
            double coolness = Read(channels, 4);
            return WizColor.Color(
                Read(channels, 0),
                Read(channels, 1),
                Read(channels, 2),
                white * Math.Min(1.0, 2.0 * coolness),
                white * Math.Min(1.0, 2.0 * (1.0 - coolness)));
        }

        if (ReferenceEquals(this, RgbCwWw))
        {
            return WizColor.Color(Read(channels, 0), Read(channels, 1), Read(channels, 2), Read(channels, 3), Read(channels, 4));
        }

        if (ReferenceEquals(this, TunableWhite))
        {
            return WizColor.White(Read(channels, 0), Read(channels, 1));
        }

        return WizColor.White(Read(channels, 0));
    }

    private static double Read(ReadOnlySpan<byte> channels, int index) =>
        (index < channels.Length ? channels[index] : 0) / 255.0;
}

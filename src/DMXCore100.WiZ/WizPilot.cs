using System.Text.Json;

namespace DMXCore100.WiZ;

/// <summary>
/// The parameters of one <c>setPilot</c> command. <see cref="State"/> false
/// turns the light off and no other value is sent; otherwise the color
/// channels (0-255) and/or <see cref="Temp"/> (kelvin) plus
/// <see cref="Dimming"/> (percent) are sent, each only when set.
/// </summary>
internal sealed record WizPilot
{
    public static readonly WizPilot Off = new() { State = false };

    public bool State { get; init; } = true;

    public byte? R { get; init; }

    public byte? G { get; init; }

    public byte? B { get; init; }

    /// <summary>Cool white channel, 0-255.</summary>
    public byte? C { get; init; }

    /// <summary>Warm white channel, 0-255.</summary>
    public byte? W { get; init; }

    /// <summary>Brightness in percent, 1-100.</summary>
    public int? Dimming { get; init; }

    /// <summary>White color temperature in kelvin (tunable-white mode).</summary>
    public int? Temp { get; init; }

    /// <summary>
    /// The UTF-8 JSON datagram for this pilot:
    /// <c>{"method":"setPilot","params":{...}}</c>.
    /// </summary>
    public byte[] ToDatagram()
    {
        using var stream = new MemoryStream(96);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("method", "setPilot");
            writer.WritePropertyName("params");
            writer.WriteStartObject();
            writer.WriteBoolean("state", State);
            if (State)
            {
                WriteIfSet(writer, "r", R);
                WriteIfSet(writer, "g", G);
                WriteIfSet(writer, "b", B);
                WriteIfSet(writer, "c", C);
                WriteIfSet(writer, "w", W);
                WriteIfSet(writer, "temp", Temp);
                WriteIfSet(writer, "dimming", Dimming);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static void WriteIfSet(Utf8JsonWriter writer, string name, int? value)
    {
        if (value.HasValue)
        {
            writer.WriteNumber(name, value.Value);
        }
    }
}

/// <summary>
/// Turns 0..1 channel levels into WiZ pilot values. WiZ renders
/// <c>r,g,b,c,w</c> as the color and <c>dimming</c> (10-100 on older
/// firmware) as overall brightness, so the brightest channel sets the
/// dimming and the channels are normalized toward 255 for the best color
/// resolution; below the dimming floor the channels themselves are scaled
/// down so fades keep going instead of stepping to black.
/// </summary>
internal static class WizColor
{
    /// <summary>
    /// Color pilot from 0..1 red, green, blue, cool white, warm white. All
    /// zero is <see cref="WizPilot.Off"/>.
    /// </summary>
    public static WizPilot Color(double r, double g, double b, double c, double w)
    {
        r = Clamp01(r);
        g = Clamp01(g);
        b = Clamp01(b);
        c = Clamp01(c);
        w = Clamp01(w);
        double level = Math.Max(Math.Max(r, g), Math.Max(Math.Max(b, c), w));
        if (level <= 0.0)
        {
            return WizPilot.Off;
        }

        int dimming = DimmingFor(level);
        // Scale so the brightest channel lands on 255 when dimming carries the
        // level exactly, and proportionally lower when the level is under the
        // dimming floor (dimming stays at the floor, the channels dim).
        double scale = 255.0 * WizConstants.MaxDimming / dimming;
        return new WizPilot
        {
            R = ToByte(r * scale),
            G = ToByte(g * scale),
            B = ToByte(b * scale),
            C = ToByte(c * scale),
            W = ToByte(w * scale),
            Dimming = dimming,
        };
    }

    /// <summary>
    /// Tunable-white pilot from a 0..1 intensity and a 0..1 color temperature
    /// (0 = warm, 1 = cool). Zero intensity is <see cref="WizPilot.Off"/>;
    /// levels under the dimming floor clamp to it (temp mode has no channel
    /// to scale).
    /// </summary>
    public static WizPilot White(double intensity, double colorTemperature)
    {
        intensity = Clamp01(intensity);
        if (intensity <= 0.0)
        {
            return WizPilot.Off;
        }

        return new WizPilot
        {
            Temp = KelvinFromUnit(colorTemperature),
            Dimming = DimmingFor(intensity),
        };
    }

    /// <summary>
    /// Dimmable-white pilot from a 0..1 intensity; zero is off.
    /// </summary>
    public static WizPilot White(double intensity)
    {
        intensity = Clamp01(intensity);
        if (intensity <= 0.0)
        {
            return WizPilot.Off;
        }

        return new WizPilot { Dimming = DimmingFor(intensity) };
    }

    /// <summary>
    /// Map a 0..1 ColorTemperature value onto WiZ kelvin (warm at 0, cool at 1).
    /// </summary>
    public static int KelvinFromUnit(double t)
    {
        t = Clamp01(t);
        return (int)Math.Round(WizConstants.KelvinMin + (t * (WizConstants.KelvinMax - WizConstants.KelvinMin)));
    }

    /// <summary>
    /// Dimming percent for a 0..1 level: rounded up so the color scale never
    /// exceeds 255, floored at <see cref="WizConstants.MinDimming"/>.
    /// </summary>
    internal static int DimmingFor(double level)
    {
        int dimming = (int)Math.Ceiling(level * WizConstants.MaxDimming);
        return Math.Clamp(dimming, WizConstants.MinDimming, WizConstants.MaxDimming);
    }

    private static byte ToByte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value) || value < 0.0)
        {
            return 0.0;
        }

        return value > 1.0 ? 1.0 : value;
    }
}

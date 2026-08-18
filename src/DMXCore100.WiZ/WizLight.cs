namespace DMXCore100.WiZ;

/// <summary>
/// What a WiZ module can render, inferred from its <c>moduleName</c>
/// (getSystemConfig): e.g. <c>ESP01_SHRGB1C_31</c> is a full-color bulb,
/// <c>ESP06_SHTW1_01</c> tunable white, <c>ESP03_SHDW1_01</c> dimmable
/// white, <c>ESP10_SOCKET_06</c> a smart plug.
/// </summary>
internal enum WizLightKind
{
    Unknown,
    Color,
    TunableWhite,
    DimmableWhite,
    Socket,
}

internal sealed class WizLight
{
    public WizLight(string mac, string ip)
    {
        Mac = mac;
        Ip = ip;
    }

    /// <summary>Device MAC as WiZ reports it: 12 lowercase hex digits, no separators.</summary>
    public string Mac { get; }

    public string Ip { get; set; }

    public string ModuleName { get; set; } = "";

    public string FirmwareVersion { get; set; } = "";

    public WizLightKind Kind => KindOf(ModuleName);

    /// <summary>A light (not a plug) — what the output protocols can drive.</summary>
    public bool IsLight => Kind != WizLightKind.Socket;

    /// <summary>
    /// Whether the light can be driven by a mode of the given kind: color
    /// modes need a color module; white modes work on every light (color
    /// bulbs have white LEDs and accept temp/dimming too). Unknown modules
    /// are offered everywhere rather than hidden.
    /// </summary>
    public bool Supports(WizModeKind kind) =>
        IsLight && (kind == WizModeKind.White || Kind is WizLightKind.Color or WizLightKind.Unknown);

    internal static WizLightKind KindOf(string moduleName)
    {
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return WizLightKind.Unknown;
        }

        string name = moduleName.ToUpperInvariant();
        if (name.Contains("SOCKET", StringComparison.Ordinal))
        {
            return WizLightKind.Socket;
        }

        if (name.Contains("RGB", StringComparison.Ordinal))
        {
            return WizLightKind.Color;
        }

        if (name.Contains("TW", StringComparison.Ordinal))
        {
            return WizLightKind.TunableWhite;
        }

        if (name.Contains("DW", StringComparison.Ordinal))
        {
            return WizLightKind.DimmableWhite;
        }

        return WizLightKind.Unknown;
    }

    internal static string KindLabel(WizLightKind kind) =>
        kind switch
        {
            WizLightKind.Color => "WiZ Color",
            WizLightKind.TunableWhite => "WiZ Tunable White",
            WizLightKind.DimmableWhite => "WiZ Dimmable White",
            WizLightKind.Socket => "WiZ Plug",
            _ => "WiZ",
        };
}

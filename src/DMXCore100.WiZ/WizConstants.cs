namespace DMXCore100.WiZ;

internal static class WizConstants
{
    /// <summary>
    /// UDP port every WiZ light listens on for JSON commands (setPilot,
    /// getPilot, getSystemConfig, registration).
    /// </summary>
    public const int Port = 38899;

    /// <summary>
    /// Community-established safe streaming rate per WiZ device; faster than
    /// this and the module starts dropping datagrams.
    /// </summary>
    public const int MaxUpdatesPerSecond = 10;

    /// <summary>
    /// Lowest <c>dimming</c> the plugin sends. Firmware-dependent on the
    /// bulb side: an ESP25_SHRGB_01 on 1.31.37 answers "Invalid params" to
    /// anything below 10, while 1.38.0 accepts down to 1. Staying at 10 works
    /// everywhere; levels below it are rendered by scaling the color channels
    /// instead (visually lossless on the color protocols), so the plugin
    /// never sends a value an older bulb rejects.
    /// </summary>
    public const int MinDimming = 10;

    public const int MaxDimming = 100;

    /// <summary>
    /// White range of the common WiZ full-color and tunable-white bulbs
    /// (getModelConfig cctRange [2200, 2700, 6500, 6500]); the bulb clamps
    /// anything outside its own range.
    /// </summary>
    public const int KelvinMin = 2200;

    public const int KelvinMax = 6500;

    public const int DiscoveryTimeoutMs = 2000;

    public const int SystemConfigTimeoutMs = 600;
}

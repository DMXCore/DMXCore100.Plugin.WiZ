# DMX Core 100 — WiZ Plugin

Drives **WiZ** (Signify) WiFi lights — full-color, tunable-white and
dimmable bulbs, strips, and lamps, including the WiZ-based Philips "Smart
LED" range — from DMX Core fixture or playback data over the WiZ local UDP
protocol (JSON `setPilot` on port 38899). No cloud account or hub required;
the light only needs to have been joined to the WiFi with the WiZ app once.

## Setup

Lights and the DMX Core must be on the same LAN (or have UDP 38899 routed
between them). Give each light a **static DHCP lease** so the mapping's IP
does not move.

On the Core's **Outputs** page, add an output of type **WiZ**:

1. **Protocol** — pick the layout that matches the light and how you want
   to drive it (table below).
2. **Destination Address** — the light's IP. Use **Discover**, or type a
   known address. Discover lists what each module can do, e.g.
   `WiZ Color (192.168.1.10, ESP01_SHRGB1C_31, a8bb50123456)`; color
   protocols only offer color-capable lights, white protocols offer every
   light.
3. **Start Channel** — DMX start address of that light's channels.

In the fixture editor, patch lights with the plugin's **WiZ / Color Bulb**
or **WiZ / White Bulb** profile; their personalities match the protocols
one-to-one, and the Mapped Device selector prefills from an existing WiZ
mapping. Presets, cues, effects, and Fixture Control then drive the light
through the normal lighting pipeline.

| Protocol | Profile / personality | Channels | Notes |
|---|---|---|---|
| `WIZ_COLOR` | Color Bulb / **RGB** | R, G, B | White is mixed in RGB; the bulb's white LEDs stay off |
| `WIZ_COLOR_RGBW_CT` | Color Bulb / **RGBW+CT** | R, G, B, White, ColorTemperature | White is crossfaded over the bulb's warm and cool white LEDs by CT: 0 = warm only, 128 = both, 255 = cool only |
| `WIZ_COLOR_RGBCW` | Color Bulb / **RGB+CW+WW** | R, G, B, CoolWhite, WarmWhite | The bulb's five channels directly |
| `WIZ_WHITE_CT` | White Bulb / **Dimmer+CT** | Intensity, ColorTemperature | Kelvin mode: CT 0 = 2200 K (warm), 255 = 6500 K (cool). Works on tunable-white and color bulbs |
| `WIZ_WHITE` | White Bulb / **Dimmer** | Intensity | Dimmable-white bulbs (and any other WiZ light) |

The Core rate-limits each mapping to 10 updates/second and coalesces
latest-wins. Every update is one `setPilot` datagram; all channels at zero
sends `state: false` (light off).

**Brightness:** WiZ renders `r,g,b,c,w` as the color and a separate
`dimming` percentage as brightness, and the firmware rejects `dimming`
below 10 % (`Invalid params`; verified on an ESP25_SHRGB_01 A19 running
1.31.37). The plugin therefore puts the level of the brightest channel
into `dimming` and normalizes the color channels toward 255 (best color
resolution), and below 10 % keeps `dimming` at 10 and scales the color
channels down instead, so fades continue smoothly to black rather than
stepping. Kelvin-mode protocols (`WIZ_WHITE_CT`, `WIZ_WHITE`) have no
channel to scale and floor at 10 %.

Requires a Core whose plugin SDK contract is **1.6** or newer.

Verified on hardware with a WiZ 60 W A19 Full Color (Matter generation,
module `ESP25_SHRGB_01`, firmware 1.31.37): discovery, every protocol,
smooth 10 Hz fades through the low end, and kelvin-mode whites.

## Troubleshooting

- **No lights in Discover:** confirm the lights are powered, joined to the
  WiFi with the WiZ app, and on the same subnet as the Core, then press
  Discover again. The plugin broadcasts a WiZ `registration` probe (not
  mDNS) on UDP 38899 and then asks each reply for its `getSystemConfig`.
- **Light does not follow cues:** check the mapping's IP, that the fixture
  is patched to a WiZ profile whose personality matches the protocol, and
  that the output is enabled. A light that was power-cycled at the wall
  takes a few seconds to rejoin WiFi.
- **Color protocol shows no lights but Discover finds them:** the module
  reported a non-color `moduleName` (`…TW…` tunable white, `…DW…`
  dimmable). Use a White protocol for it.
- **Wrong light:** destination is the IP address. Re-run Discover after a
  DHCP change, or set a static lease.
- **Plugin will not load:** the device firmware must expose SDK 1.6+.

## Development

```shell
dotnet test tests/DMXCore100.WiZ.Tests
./pack.sh            # or pack.ps1 — produces artifacts/wiz.dmxplugin + the .nupkg
```

```powershell
pwsh ./deploy-dev.ps1     # pack and upload to localhost:8080 (prompts for PIN)
```

The SDK is restored from nuget.org (`DMXCore.PluginSdk` 1.*). To build
against an unpublished SDK, pack `src/PluginSdk` and `src/PluginSdk.Testing`
from the Software repo into `local-feed/` (see the comment in
`nuget.config`); the `.nupkg` files are git-ignored.

Iterate with `tools/DMXCore100.WiZ.DevHost` (F5 in Visual Studio) and the
unit tests — both use [`TestPluginHost`](https://www.nuget.org/packages/DMXCore.PluginSdk.Testing).
Core 2026.8+ hot-reloads an uploaded `.dmxplugin`; older firmware applies
it on the next device restart.

```text
discover
config 192.168.1.10                       # getSystemConfig / getModelConfig / getPilot
send 192.168.1.10 255 0 0
sendrgbwct 192.168.1.10 0 0 0 255 128     # neutral white via the white LEDs
sendrgbcw 192.168.1.10 0 0 0 255 0        # cool white LED only
sendwhite 192.168.1.10 200 0              # kelvin mode, warm
fade 192.168.1.10 6                       # red ramp at the streaming rate
raw 192.168.1.10 {"method":"getPilot","params":{}}
r                                         # shutdown + initialize again
d                                         # dump registered protocols / profiles
```

Every push to `main` builds, tests, packs, and publishes the package to
nuget.org (the plugin registry the devices install from) via trusted
publishing; bumping `<Version>` in the csproj is what publishes a new
release.

## License

[MIT](LICENSE)

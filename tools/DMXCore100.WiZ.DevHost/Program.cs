using System.Net;
using System.Net.Sockets;
using System.Text;
using DMXCore.PluginSdk;
using DMXCore.PluginSdk.Testing;
using DMXCore100.WiZ;

// Interactive harness: F5 this project to talk to real WiZ lights on the
// LAN through the output protocol, without a DMX Core 100 device. Use `r`
// to recycle Initialize/Shutdown in-process — the host cannot unload plugin
// assemblies, so this is the practical restart.

WizPlugin plugin = new();
var host = new TestPluginHost(plugin.Info);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cts.Cancel();
};

Console.WriteLine($"=== {plugin.Info.Name} {plugin.Info.Version} dev host ===");
Console.WriteLine();

await plugin.InitializeAsync(host, cts.Token);

PrintHelp();

try
{
    bool running = true;
    while (running && !cts.IsCancellationRequested)
    {
        Console.Write("> ");
        string? input;
        try
        {
            input = (await ReadLineAsync(cts.Token))?.Trim();
        }
        catch (OperationCanceledException)
        {
            break;
        }

        if (input == null || cts.IsCancellationRequested)
        {
            break;
        }

        try
        {
            string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0].ToLowerInvariant())
            {
                case "discover":
                case "m":
                    IReadOnlyList<PluginOutputDestinationOption>? color =
                        await host.OutputProtocols[WizPlugin.ColorProtocolId].Protocol
                            .GetDestinationOptionsAsync(refresh: true, cts.Token);
                    IReadOnlyList<PluginOutputDestinationOption>? white =
                        await host.OutputProtocols[WizPlugin.WhiteProtocolId].Protocol
                            .GetDestinationOptionsAsync(refresh: false, cts.Token);
                    if (white == null || white.Count == 0)
                    {
                        Console.WriteLine("  no lights found");
                        break;
                    }

                    Console.WriteLine("  color-capable:");
                    foreach (PluginOutputDestinationOption option in color ?? [])
                    {
                        Console.WriteLine($"    {option.Value}  {option.Label}");
                    }

                    Console.WriteLine("  all lights (white protocols):");
                    foreach (PluginOutputDestinationOption option in white)
                    {
                        Console.WriteLine($"    {option.Value}  {option.Label}");
                    }

                    break;

                case "send":
                case "color":
                    if (parts.Length < 5
                        || !byte.TryParse(parts[2], out byte red)
                        || !byte.TryParse(parts[3], out byte green)
                        || !byte.TryParse(parts[4], out byte blue))
                    {
                        Console.WriteLine("usage: send <ip> <r> <g> <b>");
                        break;
                    }

                    Report(await host.SimulateOutputDeliveryAsync(
                        WizPlugin.ColorProtocolId,
                        Mapping(parts[1]),
                        [red, green, blue],
                        cts.Token));
                    break;

                case "sendrgbwct":
                    if (parts.Length < 7
                        || !byte.TryParse(parts[2], out byte wr)
                        || !byte.TryParse(parts[3], out byte wg)
                        || !byte.TryParse(parts[4], out byte wb)
                        || !byte.TryParse(parts[5], out byte ww)
                        || !byte.TryParse(parts[6], out byte wct))
                    {
                        Console.WriteLine("usage: sendrgbwct <ip> <r> <g> <b> <w> <ct>");
                        break;
                    }

                    Report(await host.SimulateOutputDeliveryAsync(
                        WizPlugin.ColorRgbwCtProtocolId,
                        Mapping(parts[1]),
                        [wr, wg, wb, ww, wct],
                        cts.Token));
                    break;

                case "sendrgbcw":
                    if (parts.Length < 7
                        || !byte.TryParse(parts[2], out byte cr)
                        || !byte.TryParse(parts[3], out byte cg)
                        || !byte.TryParse(parts[4], out byte cb)
                        || !byte.TryParse(parts[5], out byte cc)
                        || !byte.TryParse(parts[6], out byte cw))
                    {
                        Console.WriteLine("usage: sendrgbcw <ip> <r> <g> <b> <cool> <warm>");
                        break;
                    }

                    Report(await host.SimulateOutputDeliveryAsync(
                        WizPlugin.ColorRgbCwWwProtocolId,
                        Mapping(parts[1]),
                        [cr, cg, cb, cc, cw],
                        cts.Token));
                    break;

                case "sendwhite":
                    if (parts.Length < 4
                        || !byte.TryParse(parts[2], out byte intensity)
                        || !byte.TryParse(parts[3], out byte ct))
                    {
                        Console.WriteLine("usage: sendwhite <ip> <dimmer> <ct>");
                        break;
                    }

                    Report(await host.SimulateOutputDeliveryAsync(
                        WizPlugin.WhiteCtProtocolId,
                        Mapping(parts[1]),
                        [intensity, ct],
                        cts.Token));
                    break;

                case "senddim":
                    if (parts.Length < 3 || !byte.TryParse(parts[2], out byte dim))
                    {
                        Console.WriteLine("usage: senddim <ip> <dimmer>");
                        break;
                    }

                    Report(await host.SimulateOutputDeliveryAsync(
                        WizPlugin.WhiteProtocolId,
                        Mapping(parts[1]),
                        [dim],
                        cts.Token));
                    break;

                case "sendmode":
                {
                    // Any protocol by id with a raw channel slice, e.g.
                    // sendmode WIZ_COLOR_RGBCW 192.168.1.10 255 0 0 0 0
                    if (parts.Length < 4 || !host.OutputProtocols.ContainsKey(parts[1].ToUpperInvariant()))
                    {
                        Console.WriteLine("usage: sendmode <protocolId> <ip> <ch1> <ch2> ... (0-255 each)");
                        Console.WriteLine($"  protocols: {string.Join(", ", host.OutputProtocols.Keys)}");
                        break;
                    }

                    byte[] slice = new byte[parts.Length - 3];
                    bool parsed = true;
                    for (int i = 0; i < slice.Length && parsed; i++)
                    {
                        parsed = byte.TryParse(parts[i + 3], out slice[i]);
                    }

                    if (!parsed)
                    {
                        Console.WriteLine("  channel values must be 0-255");
                        break;
                    }

                    Report(await host.SimulateOutputDeliveryAsync(
                        parts[1].ToUpperInvariant(),
                        Mapping(parts[2]),
                        slice,
                        cts.Token));
                    break;
                }

                case "fade":
                {
                    // Stream a 0→255→0 red ramp at the protocol's rate to eyeball
                    // smoothness and the low-end (below 10% dimming) behavior
                    if (parts.Length < 2)
                    {
                        Console.WriteLine("usage: fade <ip> [seconds]");
                        break;
                    }

                    double seconds = parts.Length > 2 && double.TryParse(parts[2], out double s) ? s : 6;
                    int steps = (int)(seconds * WizConstants.MaxUpdatesPerSecond);
                    var delay = TimeSpan.FromMilliseconds(1000.0 / WizConstants.MaxUpdatesPerSecond);
                    for (int i = 0; i <= steps && !cts.IsCancellationRequested; i++)
                    {
                        double phase = (double)i / steps;
                        byte level = (byte)Math.Round(255 * (phase < 0.5 ? phase * 2 : (1 - phase) * 2));
                        await host.SimulateOutputDeliveryAsync(WizPlugin.ColorProtocolId, Mapping(parts[1]), [level, 0, 0], cts.Token);
                        await Task.Delay(delay, cts.Token);
                    }

                    Console.WriteLine("  fade done");
                    break;
                }

                case "raw":
                {
                    // Send any JSON to a light and print the reply, e.g.
                    // raw 192.168.1.10 {"method":"getPilot","params":{}}
                    if (parts.Length < 3)
                    {
                        Console.WriteLine("usage: raw <ip> <json>");
                        break;
                    }

                    string json = string.Join(' ', parts.Skip(2));
                    Console.WriteLine($"  reply: {await RawAsync(parts[1], json, cts.Token)}");
                    break;
                }

                case "config":
                    if (parts.Length < 2)
                    {
                        Console.WriteLine("usage: config <ip>");
                        break;
                    }

                    Console.WriteLine($"  getSystemConfig: {await RawAsync(parts[1], Encoding.UTF8.GetString(WizMessages.GetSystemConfig), cts.Token)}");
                    Console.WriteLine($"  getModelConfig:  {await RawAsync(parts[1], """{"method":"getModelConfig","params":{}}""", cts.Token)}");
                    Console.WriteLine($"  getPilot:        {await RawAsync(parts[1], Encoding.UTF8.GetString(WizMessages.GetPilot), cts.Token)}");
                    break;

                case "r":
                {
                    WizPlugin replacement = new();
                    await replacement.InitializeAsync(host, cts.Token);
                    using var reinitCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                    reinitCts.CancelAfter(TimeSpan.FromSeconds(5));
                    await plugin.ShutdownAsync(reinitCts.Token);
                    plugin = replacement;
                    Console.WriteLine("  plugin re-initialized in-process (assemblies stay loaded)");
                    break;
                }

                case "i":
                    Console.WriteLine($"  device: {host.DeviceInfo.ProductName} '{host.DeviceInfo.DeviceName}'");
                    Console.WriteLine($"  serial: {host.DeviceInfo.Serial}");
                    Console.WriteLine($"  version: {host.DeviceInfo.SoftwareVersion}");
                    break;

                case "d":
                    Console.WriteLine($"  protocols: {string.Join(", ", host.OutputProtocols.Keys)}");
                    Console.WriteLine($"  profiles:  {string.Join(", ", host.FixtureProfiles.Keys)}");
                    Console.WriteLine($"  connected: {host.ConnectionState} {host.ConnectionDetail}");
                    break;

                case "q":
                    running = false;
                    break;

                case "?":
                case "help":
                    PrintHelp();
                    break;

                default:
                    Console.WriteLine("unknown command, ? for help");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  !! {ex.GetType().Name}: {ex.Message}");
        }
    }
}
finally
{
    using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    await plugin.ShutdownAsync(shutdownCts.Token);
    Console.WriteLine("shut down cleanly");
}

static void Report(bool ok) => Console.WriteLine(ok ? "  sent" : "  send failed");

static async Task<string> RawAsync(string ip, string json, CancellationToken cancellationToken)
{
    using var udp = new UdpClient(AddressFamily.InterNetwork);
    var endpoint = new IPEndPoint(IPAddress.Parse(ip), WizConstants.Port);
    await udp.SendAsync(Encoding.UTF8.GetBytes(json), endpoint, cancellationToken);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(2));
    try
    {
        UdpReceiveResult reply = await udp.ReceiveAsync(timeout.Token);
        return Encoding.UTF8.GetString(reply.Buffer);
    }
    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
    {
        return "(no reply within 2 s)";
    }
}

static async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
{
    Task<string?> read = Task.Run(Console.ReadLine, cancellationToken);
    return await read.WaitAsync(cancellationToken);
}

static PluginOutputMappingConfig Mapping(string ip) =>
    new()
    {
        DestinationAddress = ip,
        ChannelOffset = 0,
        UniverseId = 1,
    };

static void PrintHelp()
{
    Console.WriteLine("""
        Commands (output protocol, same path as the Core's Outputs page):
          discover                      broadcast-discover WiZ lights
          send <ip> r g b               WIZ_COLOR (0-255)
          sendrgbwct <ip> r g b w ct    WIZ_COLOR_RGBW_CT, ct 0=warm 255=cool
          sendrgbcw <ip> r g b cool warm  WIZ_COLOR_RGBCW, the bulb's five channels
          sendwhite <ip> dim ct         WIZ_WHITE_CT (kelvin mode)
          senddim <ip> dim              WIZ_WHITE
          sendmode <proto> <ip> ch...   any protocol id with a raw slice
          fade <ip> [seconds]           red 0→255→0 ramp at 10 updates/s
          config <ip>                   print getSystemConfig / getModelConfig / getPilot
          raw <ip> <json>               send any JSON and print the reply
          r                             shutdown + initialize again (no assembly unload)
          i                             show device info
          d                             dump registered protocols / profiles
          q                             quit
        """);
}

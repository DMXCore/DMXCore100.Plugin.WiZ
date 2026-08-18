using System.Net;
using System.Text.Json;
using DMXCore.PluginSdk;
using DMXCore.PluginSdk.Testing;

namespace DMXCore100.WiZ.Tests;

[TestClass]
public class WizPluginTests
{
    private readonly List<WizPlugin> plugins = [];

    [TestCleanup]
    public async Task CleanupAsync()
    {
        foreach (WizPlugin plugin in this.plugins)
        {
            await plugin.ShutdownAsync(CancellationToken.None);
        }

        this.plugins.Clear();
    }

    private async Task<(WizPlugin Plugin, TestPluginHost Host, List<(IPEndPoint Endpoint, byte[] Packet)> Sent)> CreateInitializedAsync(
        IReadOnlyList<WizLight>? discovered = null)
    {
        var sent = new List<(IPEndPoint, byte[])>();
        WizLight[] lights = discovered?.ToArray() ??
        [
            new WizLight("a8bb50123456", "192.168.1.10") { ModuleName = "ESP01_SHRGB1C_31" },
        ];

        var plugin = new WizPlugin(
            (_, _) => Task.FromResult<IReadOnlyList<WizLight>>(lights),
            (endpoint, packet, _) =>
            {
                sent.Add((endpoint, packet.ToArray()));
                return ValueTask.CompletedTask;
            });
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        await plugin.InitializeAsync(host, CancellationToken.None);
        return (plugin, host, sent);
    }

    [TestMethod]
    public void Info_ComesFromTheProjectFile()
    {
        var plugin = new WizPlugin();

        Assert.AreEqual("wiz", plugin.Info.Id);
        Assert.AreEqual("WiZ", plugin.Info.Name);
        Assert.IsFalse(string.IsNullOrWhiteSpace(plugin.Info.Version));
        // The declared floor is what the manifest carries; a newer SDK at
        // build time must not raise it
        Assert.AreEqual(new Version(1, 6), Version.Parse(PluginBuildInfo.MinSdkVersion));
    }

    [TestMethod]
    public async Task Initialize_RegistersProtocolsAndProfiles()
    {
        var (_, host, _) = await CreateInitializedAsync();

        Assert.IsTrue(host.OutputProtocols.ContainsKey(WizPlugin.ColorProtocolId));
        Assert.IsTrue(host.OutputProtocols.ContainsKey(WizPlugin.ColorRgbwCtProtocolId));
        Assert.IsTrue(host.OutputProtocols.ContainsKey(WizPlugin.ColorRgbCwWwProtocolId));
        Assert.IsTrue(host.OutputProtocols.ContainsKey(WizPlugin.WhiteCtProtocolId));
        Assert.IsTrue(host.OutputProtocols.ContainsKey(WizPlugin.WhiteProtocolId));
        Assert.AreEqual(5, host.OutputProtocols.Count);
        Assert.IsTrue(host.FixtureProfiles.ContainsKey(WizPlugin.ColorProfileCode));
        Assert.IsTrue(host.FixtureProfiles.ContainsKey(WizPlugin.WhiteProfileCode));
        Assert.AreEqual(true, host.ConnectionState);
        Assert.AreEqual("WiZ output ready", host.ConnectionDetail);

        OutputProtocolDescriptor color = host.OutputProtocols[WizPlugin.ColorProtocolId].Descriptor;
        Assert.AreEqual(WizPlugin.PortType, color.PortType);
        Assert.AreEqual("WiZ", color.PortTypeDisplayName);
        Assert.AreEqual(WizConstants.MaxUpdatesPerSecond, color.MaxUpdatesPerSecond);
        Assert.IsTrue(color.SupportsDestinationDiscovery);
        Assert.AreEqual(WizPlugin.ColorProfileCode, color.SuggestedProfileCode);
        Assert.AreEqual("WiZ Color RGB", color.DisplayName);
        Assert.AreEqual("RGB", color.SuggestedPersonality);
        Assert.AreEqual("RGBW+CT", host.OutputProtocols[WizPlugin.ColorRgbwCtProtocolId].Descriptor.SuggestedPersonality);
        Assert.AreEqual("RGB+CW+WW", host.OutputProtocols[WizPlugin.ColorRgbCwWwProtocolId].Descriptor.SuggestedPersonality);
        Assert.AreEqual(WizPlugin.WhiteProfileCode, host.OutputProtocols[WizPlugin.WhiteCtProtocolId].Descriptor.SuggestedProfileCode);
        Assert.AreEqual("Dimmer+CT", host.OutputProtocols[WizPlugin.WhiteCtProtocolId].Descriptor.SuggestedPersonality);
        Assert.AreEqual("Dimmer", host.OutputProtocols[WizPlugin.WhiteProtocolId].Descriptor.SuggestedPersonality);

        PluginFixtureProfileDescriptor colorProfile = host.FixtureProfiles[WizPlugin.ColorProfileCode];
        Assert.AreEqual("WiZ", colorProfile.Manufacturer);
        CollectionAssert.AreEqual(
            new[] { "RGB", "RGBW+CT", "RGB+CW+WW" },
            colorProfile.Personalities.Select(p => p.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { PluginFixtureFunction.Red, PluginFixtureFunction.Green, PluginFixtureFunction.Blue },
            colorProfile.Personalities[0].Channels.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                PluginFixtureFunction.Red,
                PluginFixtureFunction.Green,
                PluginFixtureFunction.Blue,
                PluginFixtureFunction.White,
                PluginFixtureFunction.ColorTemperature,
            },
            colorProfile.Personalities[1].Channels.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                PluginFixtureFunction.Red,
                PluginFixtureFunction.Green,
                PluginFixtureFunction.Blue,
                PluginFixtureFunction.CoolWhite,
                PluginFixtureFunction.WarmWhite,
            },
            colorProfile.Personalities[2].Channels.ToArray());

        PluginFixtureProfileDescriptor whiteProfile = host.FixtureProfiles[WizPlugin.WhiteProfileCode];
        CollectionAssert.AreEqual(
            new[] { "Dimmer+CT", "Dimmer" },
            whiteProfile.Personalities.Select(p => p.Name).ToArray());
        CollectionAssert.AreEqual(
            new[] { PluginFixtureFunction.Intensity, PluginFixtureFunction.ColorTemperature },
            whiteProfile.Personalities[0].Channels.ToArray());
        CollectionAssert.AreEqual(
            new[] { PluginFixtureFunction.Intensity },
            whiteProfile.Personalities[1].Channels.ToArray());
    }

    [TestMethod]
    public async Task Initialize_EveryProtocolHasAMatchingPersonality()
    {
        var (_, host, _) = await CreateInitializedAsync();
        PluginOutputMappingConfig config = Mapping("192.168.1.10");

        foreach (WizMode mode in WizMode.All)
        {
            OutputProtocolDescriptor descriptor = host.OutputProtocols[mode.ProtocolId].Descriptor;
            PluginFixtureProfileDescriptor profile = host.FixtureProfiles[descriptor.SuggestedProfileCode!];
            PluginFixturePersonality? personality = profile.Personalities
                .SingleOrDefault(p => p.Name == descriptor.SuggestedPersonality);
            Assert.IsNotNull(personality, mode.ProtocolId);

            // The personality footprint and the protocol channel count must
            // agree or the patch and the mapping drift apart
            Assert.AreEqual(
                personality.Channels.Count,
                host.OutputProtocols[mode.ProtocolId].Protocol.GetChannelCount(config),
                mode.ProtocolId);
        }
    }

    [TestMethod]
    public async Task GetChannelCount_MatchesPersonality()
    {
        var (_, host, _) = await CreateInitializedAsync();
        PluginOutputMappingConfig config = Mapping("192.168.1.10");

        Assert.AreEqual(3, Protocol(host, WizPlugin.ColorProtocolId).GetChannelCount(config));
        Assert.AreEqual(5, Protocol(host, WizPlugin.ColorRgbwCtProtocolId).GetChannelCount(config));
        Assert.AreEqual(5, Protocol(host, WizPlugin.ColorRgbCwWwProtocolId).GetChannelCount(config));
        Assert.AreEqual(2, Protocol(host, WizPlugin.WhiteCtProtocolId).GetChannelCount(config));
        Assert.AreEqual(1, Protocol(host, WizPlugin.WhiteProtocolId).GetChannelCount(config));
    }

    [TestMethod]
    public async Task SendRgb_WritesSetPilotToPort38899()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        bool ok = await host.SimulateOutputDeliveryAsync(
            WizPlugin.ColorProtocolId,
            Mapping("192.168.1.10"),
            [255, 0, 0]);

        Assert.IsTrue(ok);
        Assert.AreEqual(1, sent.Count);
        Assert.AreEqual(WizConstants.Port, sent[0].Endpoint.Port);
        Assert.AreEqual("192.168.1.10", sent[0].Endpoint.Address.ToString());
        JsonElement parameters = Params(sent[0].Packet);
        Assert.IsTrue(parameters.GetProperty("state").GetBoolean());
        Assert.AreEqual(255, parameters.GetProperty("r").GetInt32());
        Assert.AreEqual(0, parameters.GetProperty("g").GetInt32());
        Assert.AreEqual(0, parameters.GetProperty("b").GetInt32());
        Assert.AreEqual(0, parameters.GetProperty("c").GetInt32());
        Assert.AreEqual(0, parameters.GetProperty("w").GetInt32());
        Assert.AreEqual(100, parameters.GetProperty("dimming").GetInt32());
        Assert.IsFalse(parameters.TryGetProperty("temp", out _));
    }

    [TestMethod]
    public async Task SendRgb_BlackTurnsTheLightOff()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        bool ok = await host.SimulateOutputDeliveryAsync(
            WizPlugin.ColorProtocolId,
            Mapping("192.168.1.10"),
            [0, 0, 0]);

        Assert.IsTrue(ok);
        JsonElement parameters = Params(sent[0].Packet);
        Assert.IsFalse(parameters.GetProperty("state").GetBoolean());
        Assert.IsFalse(parameters.TryGetProperty("r", out _));
        Assert.IsFalse(parameters.TryGetProperty("dimming", out _));
    }

    [TestMethod]
    public async Task SendRgb_HalfLevelGoesToDimming()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        bool ok = await host.SimulateOutputDeliveryAsync(
            WizPlugin.ColorProtocolId,
            Mapping("192.168.1.10"),
            [128, 64, 0]);

        Assert.IsTrue(ok);
        JsonElement parameters = Params(sent[0].Packet);
        Assert.AreEqual(51, parameters.GetProperty("dimming").GetInt32());
        Assert.AreEqual(251, parameters.GetProperty("r").GetInt32());
        Assert.AreEqual(125, parameters.GetProperty("g").GetInt32());
        Assert.AreEqual(0, parameters.GetProperty("b").GetInt32());
    }

    [TestMethod]
    public async Task SendRgbwCt_SplitsWhiteAcrossCoolAndWarmByCt()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        // Warm end
        Assert.IsTrue(await host.SimulateOutputDeliveryAsync(
            WizPlugin.ColorRgbwCtProtocolId, Mapping("192.168.1.10"), [0, 0, 0, 255, 0]));
        // Cool end
        Assert.IsTrue(await host.SimulateOutputDeliveryAsync(
            WizPlugin.ColorRgbwCtProtocolId, Mapping("192.168.1.10"), [0, 0, 0, 255, 255]));
        // Midpoint: both white LED sets fully on
        Assert.IsTrue(await host.SimulateOutputDeliveryAsync(
            WizPlugin.ColorRgbwCtProtocolId, Mapping("192.168.1.10"), [0, 0, 0, 255, 128]));

        JsonElement warm = Params(sent[0].Packet);
        Assert.AreEqual(0, warm.GetProperty("c").GetInt32());
        Assert.AreEqual(255, warm.GetProperty("w").GetInt32());
        Assert.AreEqual(100, warm.GetProperty("dimming").GetInt32());
        Assert.IsFalse(warm.TryGetProperty("temp", out _));

        JsonElement cool = Params(sent[1].Packet);
        Assert.AreEqual(255, cool.GetProperty("c").GetInt32());
        Assert.AreEqual(0, cool.GetProperty("w").GetInt32());

        JsonElement mid = Params(sent[2].Packet);
        Assert.AreEqual(255, mid.GetProperty("c").GetInt32());
        Assert.AreEqual(254, mid.GetProperty("w").GetInt32(), 1);
    }

    [TestMethod]
    public async Task SendRgbwCt_MixesColorAndWhite()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        bool ok = await host.SimulateOutputDeliveryAsync(
            WizPlugin.ColorRgbwCtProtocolId,
            Mapping("192.168.1.10"),
            [255, 0, 0, 128, 255]);

        Assert.IsTrue(ok);
        JsonElement parameters = Params(sent[0].Packet);
        Assert.AreEqual(255, parameters.GetProperty("r").GetInt32());
        Assert.AreEqual(128, parameters.GetProperty("c").GetInt32());
        Assert.AreEqual(0, parameters.GetProperty("w").GetInt32());
        Assert.AreEqual(100, parameters.GetProperty("dimming").GetInt32());
    }

    [TestMethod]
    public async Task SendRgbCwWw_PassesTheFiveChannelsThrough()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        bool ok = await host.SimulateOutputDeliveryAsync(
            WizPlugin.ColorRgbCwWwProtocolId,
            Mapping("192.168.1.10"),
            [10, 20, 30, 255, 40]);

        Assert.IsTrue(ok);
        JsonElement parameters = Params(sent[0].Packet);
        Assert.AreEqual(10, parameters.GetProperty("r").GetInt32());
        Assert.AreEqual(20, parameters.GetProperty("g").GetInt32());
        Assert.AreEqual(30, parameters.GetProperty("b").GetInt32());
        Assert.AreEqual(255, parameters.GetProperty("c").GetInt32());
        Assert.AreEqual(40, parameters.GetProperty("w").GetInt32());
        Assert.AreEqual(100, parameters.GetProperty("dimming").GetInt32());
    }

    [TestMethod]
    public async Task SendWhiteCt_SendsKelvinAndDimming()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        bool ok = await host.SimulateOutputDeliveryAsync(
            WizPlugin.WhiteCtProtocolId,
            Mapping("192.168.1.10"),
            [128, 255]);

        Assert.IsTrue(ok);
        JsonElement parameters = Params(sent[0].Packet);
        Assert.AreEqual(WizConstants.KelvinMax, parameters.GetProperty("temp").GetInt32());
        Assert.AreEqual(51, parameters.GetProperty("dimming").GetInt32());
        Assert.IsFalse(parameters.TryGetProperty("r", out _));
        Assert.IsFalse(parameters.TryGetProperty("c", out _));
    }

    [TestMethod]
    public async Task SendWhite_SendsDimmingOnly_ZeroIsOff()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        Assert.IsTrue(await host.SimulateOutputDeliveryAsync(WizPlugin.WhiteProtocolId, Mapping("192.168.1.10"), [255]));
        Assert.IsTrue(await host.SimulateOutputDeliveryAsync(WizPlugin.WhiteProtocolId, Mapping("192.168.1.10"), [0]));

        JsonElement on = Params(sent[0].Packet);
        Assert.AreEqual(100, on.GetProperty("dimming").GetInt32());
        Assert.IsFalse(on.TryGetProperty("temp", out _));
        Assert.IsFalse(Params(sent[1].Packet).GetProperty("state").GetBoolean());
    }

    [TestMethod]
    public async Task Send_RejectsShortSliceForEveryMode()
    {
        var (_, host, sent) = await CreateInitializedAsync();

        foreach (WizMode mode in WizMode.All)
        {
            bool ok = await host.SimulateOutputDeliveryAsync(
                mode.ProtocolId,
                Mapping("192.168.1.10"),
                new byte[mode.ChannelCount - 1]);
            Assert.IsFalse(ok, mode.ProtocolId);
        }

        Assert.AreEqual(0, sent.Count);
    }

    [TestMethod]
    public async Task Send_ReportsFailureWhenTheSocketFails()
    {
        var plugin = new WizPlugin(
            (_, _) => Task.FromResult<IReadOnlyList<WizLight>>([]),
            (_, _, _) => throw new System.Net.Sockets.SocketException());
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        await plugin.InitializeAsync(host, CancellationToken.None);

        bool ok = await host.SimulateOutputDeliveryAsync(WizPlugin.ColorProtocolId, Mapping("192.168.1.10"), [1, 2, 3]);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public async Task OpenSession_RejectsMissingOrInvalidAddress()
    {
        var (_, host, _) = await CreateInitializedAsync();
        IPluginOutputProtocol protocol = Protocol(host, WizPlugin.ColorProtocolId);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            protocol.OpenSessionAsync(Mapping(""), CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            protocol.OpenSessionAsync(Mapping("kitchen-bulb"), CancellationToken.None));
    }

    [TestMethod]
    public async Task Discover_ReturnsIpDestinationsAndFiltersByKind()
    {
        WizLight[] lights =
        [
            new WizLight("a8bb50000001", "192.168.1.10") { ModuleName = "ESP01_SHRGB1C_31" },
            new WizLight("a8bb50000002", "192.168.1.11") { ModuleName = "ESP06_SHTW1_01" },
            new WizLight("a8bb50000003", "192.168.1.12") { ModuleName = "ESP03_SHDW1_01" },
            new WizLight("a8bb50000004", "192.168.1.13") { ModuleName = "ESP10_SOCKET_06" },
            new WizLight("a8bb50000005", "192.168.1.14"),
        ];
        var (_, host, _) = await CreateInitializedAsync(lights);

        IReadOnlyList<PluginOutputDestinationOption>? color =
            await Protocol(host, WizPlugin.ColorProtocolId)
                .GetDestinationOptionsAsync(refresh: true, CancellationToken.None);
        IReadOnlyList<PluginOutputDestinationOption>? white =
            await Protocol(host, WizPlugin.WhiteCtProtocolId)
                .GetDestinationOptionsAsync(refresh: false, CancellationToken.None);

        Assert.IsNotNull(color);
        CollectionAssert.AreEqual(new[] { "192.168.1.10", "192.168.1.14" }, color.Select(o => o.Value).ToArray());
        Assert.AreEqual("WiZ Color (192.168.1.10, ESP01_SHRGB1C_31, a8bb50000001)", color[0].Label);
        Assert.AreEqual("WiZ (192.168.1.14, a8bb50000005)", color[1].Label);

        Assert.IsNotNull(white);
        CollectionAssert.AreEqual(
            new[] { "192.168.1.10", "192.168.1.11", "192.168.1.12", "192.168.1.14" },
            white.Select(o => o.Value).ToArray());
        Assert.AreEqual("WiZ Tunable White (192.168.1.11, ESP06_SHTW1_01, a8bb50000002)", white[1].Label);
    }

    [TestMethod]
    public async Task Discover_UsesCacheUntilRefresh()
    {
        int calls = 0;
        WizLight kitchen = new("a8bb50123456", "192.168.1.10") { ModuleName = "ESP01_SHRGB1C_31" };
        var plugin = new WizPlugin(
            (_, _) =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<WizLight>>([kitchen]);
            },
            null);
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        await plugin.InitializeAsync(host, CancellationToken.None);
        IPluginOutputProtocol protocol = Protocol(host, WizPlugin.ColorProtocolId);

        _ = await protocol.GetDestinationOptionsAsync(refresh: false, CancellationToken.None);
        _ = await protocol.GetDestinationOptionsAsync(refresh: false, CancellationToken.None);
        _ = await protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);
        // The cache is shared by every protocol of the plugin
        _ = await Protocol(host, WizPlugin.WhiteProtocolId).GetDestinationOptionsAsync(refresh: false, CancellationToken.None);

        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task Discover_ConcurrentRefresh_SharesInFlightScan()
    {
        int calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WizLight kitchen = new("a8bb50123456", "192.168.1.10") { ModuleName = "ESP01_SHRGB1C_31" };
        var plugin = new WizPlugin(
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                started.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return (IReadOnlyList<WizLight>)[kitchen];
            },
            null);
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        await plugin.InitializeAsync(host, CancellationToken.None);
        IPluginOutputProtocol protocol = Protocol(host, WizPlugin.ColorProtocolId);

        Task<IReadOnlyList<PluginOutputDestinationOption>?> first =
            protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);
        await started.Task;
        Task<IReadOnlyList<PluginOutputDestinationOption>?> second =
            protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);
        release.SetResult();

        IReadOnlyList<PluginOutputDestinationOption>?[] results = await Task.WhenAll(first, second);

        Assert.AreEqual(1, calls);
        Assert.AreEqual(1, results[0]!.Count);
        Assert.AreEqual(1, results[1]!.Count);
        Assert.AreEqual("192.168.1.10", results[0]![0].Value);
    }

    [TestMethod]
    public async Task Discover_InitiatorCancel_DoesNotFailOtherRefresh()
    {
        int calls = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken scanToken = CancellationToken.None;
        WizLight kitchen = new("a8bb50123456", "192.168.1.10") { ModuleName = "ESP01_SHRGB1C_31" };
        var plugin = new WizPlugin(
            async (_, cancellationToken) =>
            {
                Interlocked.Increment(ref calls);
                scanToken = cancellationToken;
                started.TrySetResult();
                await release.Task;
                return (IReadOnlyList<WizLight>)[kitchen];
            },
            null);
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        await plugin.InitializeAsync(host, CancellationToken.None);
        IPluginOutputProtocol protocol = Protocol(host, WizPlugin.ColorProtocolId);

        using var firstCts = new CancellationTokenSource();
        Task<IReadOnlyList<PluginOutputDestinationOption>?> first =
            protocol.GetDestinationOptionsAsync(refresh: true, firstCts.Token);
        await started.Task;
        Task<IReadOnlyList<PluginOutputDestinationOption>?> second =
            protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        firstCts.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => first);
        Assert.IsFalse(scanToken.IsCancellationRequested);

        release.SetResult();
        IReadOnlyList<PluginOutputDestinationOption>? options = await second;

        Assert.AreEqual(1, calls);
        Assert.IsNotNull(options);
        Assert.AreEqual(1, options.Count);
        Assert.AreEqual("192.168.1.10", options[0].Value);
    }

    [TestMethod]
    public async Task Discover_ScanFailure_SurfacesAndNextRefreshRetries()
    {
        int calls = 0;
        var plugin = new WizPlugin(
            (_, _) =>
            {
                calls++;
                return calls == 1
                    ? Task.FromException<IReadOnlyList<WizLight>>(new InvalidOperationException("no network"))
                    : Task.FromResult<IReadOnlyList<WizLight>>([new WizLight("a8bb50123456", "192.168.1.10")]);
            },
            null);
        this.plugins.Add(plugin);
        var host = new TestPluginHost(plugin.Info, logOutput: _ => { });
        await plugin.InitializeAsync(host, CancellationToken.None);
        IPluginOutputProtocol protocol = Protocol(host, WizPlugin.ColorProtocolId);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None));
        IReadOnlyList<PluginOutputDestinationOption>? options =
            await protocol.GetDestinationOptionsAsync(refresh: true, CancellationToken.None);

        Assert.AreEqual(2, calls);
        Assert.IsNotNull(options);
        Assert.AreEqual(1, options.Count);
    }

    [TestMethod]
    public async Task Shutdown_UnregistersEverything()
    {
        var (plugin, host, _) = await CreateInitializedAsync();

        await plugin.ShutdownAsync(CancellationToken.None);

        Assert.AreEqual(0, host.OutputProtocols.Count);
        Assert.AreEqual(0, host.FixtureProfiles.Count);
    }

    private static IPluginOutputProtocol Protocol(TestPluginHost host, string id) =>
        host.OutputProtocols[id].Protocol;

    private static PluginOutputMappingConfig Mapping(string ip) =>
        new()
        {
            DestinationAddress = ip,
            ChannelOffset = 0,
            UniverseId = 1,
        };

    private static JsonElement Params(byte[] datagram)
    {
        using JsonDocument document = JsonDocument.Parse(datagram);
        Assert.AreEqual("setPilot", document.RootElement.GetProperty("method").GetString());
        return document.RootElement.GetProperty("params").Clone();
    }
}

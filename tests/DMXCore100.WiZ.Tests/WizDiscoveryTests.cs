using System.Net;
using System.Text;
using System.Text.Json;

namespace DMXCore100.WiZ.Tests;

[TestClass]
public class WizDiscoveryTests
{
    [TestMethod]
    public void Registration_IsTheProbeTheWizAppSends()
    {
        string json = Encoding.UTF8.GetString(WizMessages.Registration);
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.AreEqual("registration", document.RootElement.GetProperty("method").GetString());
        Assert.IsFalse(document.RootElement.GetProperty("params").GetProperty("register").GetBoolean());
    }

    [TestMethod]
    public void TryParse_ReadsMethodAndResult()
    {
        byte[] reply = Encoding.UTF8.GetBytes(
            """{"method":"registration","env":"pro","result":{"mac":"a8bb50123456","success":true}}""");

        Assert.IsTrue(WizMessages.TryParse(reply, out string method, out JsonElement result));
        Assert.AreEqual("registration", method);
        Assert.AreEqual("a8bb50123456", WizMessages.GetMac(result));
    }

    [TestMethod]
    public void TryParse_RejectsErrorsAndGarbage()
    {
        Assert.IsFalse(WizMessages.TryParse(
            Encoding.UTF8.GetBytes("""{"method":"setPilot","env":"pro","error":{"code":-32600,"message":"Invalid Request"}}"""),
            out _,
            out _));
        Assert.IsFalse(WizMessages.TryParse(Encoding.UTF8.GetBytes("not json"), out _, out _));
        Assert.IsFalse(WizMessages.TryParse(Encoding.UTF8.GetBytes("[1,2]"), out _, out _));
        Assert.IsFalse(WizMessages.TryParse([], out _, out _));
    }

    [TestMethod]
    public void GetMac_NormalizesSeparatorsAndCase()
    {
        using JsonDocument colons = JsonDocument.Parse("""{"mac":"A8:BB:50:12:34:56"}""");
        using JsonDocument bad = JsonDocument.Parse("""{"mac":"nope"}""");
        using JsonDocument missing = JsonDocument.Parse("""{"success":true}""");

        Assert.AreEqual("a8bb50123456", WizMessages.GetMac(colons.RootElement));
        Assert.IsNull(WizMessages.GetMac(bad.RootElement));
        Assert.IsNull(WizMessages.GetMac(missing.RootElement));
    }

    [TestMethod]
    public void HandleReply_RegistrationThenSystemConfigBuildsTheLight()
    {
        var lights = new Dictionary<string, WizLight>(StringComparer.OrdinalIgnoreCase);

        WizDiscovery.HandleReply(
            Encoding.UTF8.GetBytes("""{"method":"registration","env":"pro","result":{"mac":"a8bb50123456","success":true}}"""),
            "192.168.1.10",
            lights);
        WizDiscovery.HandleReply(
            Encoding.UTF8.GetBytes("""{"method":"getSystemConfig","env":"pro","result":{"mac":"a8bb50123456","homeId":1,"roomId":2,"moduleName":"ESP01_SHRGB1C_31","fwVersion":"1.31.32","groupId":0,"drvConf":[20,2]}}"""),
            "192.168.1.10",
            lights);

        Assert.AreEqual(1, lights.Count);
        WizLight light = lights["a8bb50123456"];
        Assert.AreEqual("192.168.1.10", light.Ip);
        Assert.AreEqual("ESP01_SHRGB1C_31", light.ModuleName);
        Assert.AreEqual("1.31.32", light.FirmwareVersion);
        Assert.AreEqual(WizLightKind.Color, light.Kind);
        Assert.AreEqual("WiZ Color (192.168.1.10, ESP01_SHRGB1C_31, a8bb50123456)", WizDiscovery.DestinationLabel(light));
    }

    [TestMethod]
    public void HandleReply_SystemConfigWithoutMacMatchesByIp()
    {
        var lights = new Dictionary<string, WizLight>(StringComparer.OrdinalIgnoreCase);
        WizDiscovery.HandleReply(
            Encoding.UTF8.GetBytes("""{"method":"registration","env":"pro","result":{"mac":"a8bb50123456","success":true}}"""),
            "192.168.1.10",
            lights);

        WizDiscovery.HandleReply(
            Encoding.UTF8.GetBytes("""{"method":"getSystemConfig","env":"pro","result":{"moduleName":"ESP06_SHTW1_01","fwVersion":"1.16.64"}}"""),
            "192.168.1.10",
            lights);

        Assert.AreEqual(WizLightKind.TunableWhite, lights["a8bb50123456"].Kind);
    }

    [TestMethod]
    public void HandleReply_IgnoresRepliesFromUnknownDevices()
    {
        var lights = new Dictionary<string, WizLight>(StringComparer.OrdinalIgnoreCase);

        WizDiscovery.HandleReply(
            Encoding.UTF8.GetBytes("""{"method":"getSystemConfig","env":"pro","result":{"moduleName":"ESP06_SHTW1_01"}}"""),
            "192.168.1.99",
            lights);
        WizDiscovery.HandleReply(
            Encoding.UTF8.GetBytes("""{"method":"registration","env":"pro","result":{"success":true}}"""),
            "192.168.1.98",
            lights);

        Assert.AreEqual(0, lights.Count);
    }

    [TestMethod]
    public void KindOf_ClassifiesModuleNames()
    {
        Assert.AreEqual(WizLightKind.Color, WizLight.KindOf("ESP01_SHRGB1C_31"));
        Assert.AreEqual(WizLightKind.Color, WizLight.KindOf("ESP20_SHRGBC_01"));
        Assert.AreEqual(WizLightKind.TunableWhite, WizLight.KindOf("ESP06_SHTW1_01"));
        Assert.AreEqual(WizLightKind.DimmableWhite, WizLight.KindOf("ESP03_SHDW1_01"));
        Assert.AreEqual(WizLightKind.Socket, WizLight.KindOf("ESP10_SOCKET_06"));
        Assert.AreEqual(WizLightKind.Unknown, WizLight.KindOf(""));
        Assert.AreEqual(WizLightKind.Unknown, WizLight.KindOf("ESP99_MYSTERY_01"));
    }

    [TestMethod]
    public void Supports_ColorModesNeedColorModules_WhiteModesTakeAnyLight()
    {
        WizLight color = new("aa", "1.1.1.1") { ModuleName = "ESP01_SHRGB1C_31" };
        WizLight tw = new("bb", "1.1.1.2") { ModuleName = "ESP06_SHTW1_01" };
        WizLight unknown = new("cc", "1.1.1.3");
        WizLight socket = new("dd", "1.1.1.4") { ModuleName = "ESP10_SOCKET_06" };

        Assert.IsTrue(color.Supports(WizModeKind.Color));
        Assert.IsTrue(color.Supports(WizModeKind.White));
        Assert.IsFalse(tw.Supports(WizModeKind.Color));
        Assert.IsTrue(tw.Supports(WizModeKind.White));
        Assert.IsTrue(unknown.Supports(WizModeKind.Color));
        Assert.IsTrue(unknown.Supports(WizModeKind.White));
        Assert.IsFalse(socket.Supports(WizModeKind.Color));
        Assert.IsFalse(socket.Supports(WizModeKind.White));
        Assert.IsFalse(socket.IsLight);
    }

    [TestMethod]
    public void DirectedBroadcast_ComputesFromMask()
    {
        Assert.AreEqual(
            IPAddress.Parse("192.168.1.255"),
            WizDiscovery.DirectedBroadcast(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.0")));
        Assert.AreEqual(
            IPAddress.Parse("10.0.63.255"),
            WizDiscovery.DirectedBroadcast(IPAddress.Parse("10.0.1.2"), IPAddress.Parse("255.255.192.0")));
    }

    [TestMethod]
    public void DiscoveryBroadcastAddresses_AlwaysIncludesLimitedBroadcast()
    {
        IReadOnlyList<IPAddress> addresses = WizDiscovery.DiscoveryBroadcastAddresses();

        CollectionAssert.Contains(addresses.ToArray(), IPAddress.Broadcast);
        Assert.AreEqual(addresses.Count, addresses.Distinct().Count());
    }
}

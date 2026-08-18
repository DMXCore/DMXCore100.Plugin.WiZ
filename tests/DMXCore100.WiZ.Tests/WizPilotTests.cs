using System.Text;
using System.Text.Json;

namespace DMXCore100.WiZ.Tests;

[TestClass]
public class WizPilotTests
{
    [TestMethod]
    public void Off_SendsOnlyStateFalse()
    {
        JsonElement parameters = Params(WizPilot.Off);

        Assert.AreEqual(1, parameters.EnumerateObject().Count());
        Assert.IsFalse(parameters.GetProperty("state").GetBoolean());
    }

    [TestMethod]
    public void Datagram_IsSetPilotWithOnlySetValues()
    {
        var pilot = new WizPilot { R = 255, G = 0, B = 12, Dimming = 50 };
        byte[] datagram = pilot.ToDatagram();
        using JsonDocument document = JsonDocument.Parse(datagram);

        Assert.AreEqual("setPilot", document.RootElement.GetProperty("method").GetString());
        JsonElement parameters = document.RootElement.GetProperty("params");
        Assert.IsTrue(parameters.GetProperty("state").GetBoolean());
        Assert.AreEqual(255, parameters.GetProperty("r").GetInt32());
        Assert.AreEqual(0, parameters.GetProperty("g").GetInt32());
        Assert.AreEqual(12, parameters.GetProperty("b").GetInt32());
        Assert.AreEqual(50, parameters.GetProperty("dimming").GetInt32());
        Assert.IsFalse(parameters.TryGetProperty("c", out _));
        Assert.IsFalse(parameters.TryGetProperty("w", out _));
        Assert.IsFalse(parameters.TryGetProperty("temp", out _));
    }

    [TestMethod]
    public void Datagram_IsCompactUtf8Json()
    {
        string json = Encoding.UTF8.GetString(new WizPilot { Temp = 2700, Dimming = 100 }.ToDatagram());

        Assert.AreEqual("""{"method":"setPilot","params":{"state":true,"temp":2700,"dimming":100}}""", json);
    }

    [TestMethod]
    public void Color_AllZeroIsOff()
    {
        Assert.AreSame(WizPilot.Off, WizColor.Color(0, 0, 0, 0, 0));
    }

    [TestMethod]
    public void Color_FullRedIsFullDimmingFullRed()
    {
        WizPilot pilot = WizColor.Color(1, 0, 0, 0, 0);

        Assert.IsTrue(pilot.State);
        Assert.AreEqual((byte)255, pilot.R);
        Assert.AreEqual((byte)0, pilot.G);
        Assert.AreEqual((byte)0, pilot.B);
        Assert.AreEqual((byte)0, pilot.C);
        Assert.AreEqual((byte)0, pilot.W);
        Assert.AreEqual(100, pilot.Dimming);
    }

    [TestMethod]
    public void Color_HalfLevelKeepsColorAtFullAndDimsViaDimming()
    {
        // (0.5, 0.25, 0) — an orange at half level: color normalized so red
        // hits 255, brightness carried by dimming 50
        WizPilot pilot = WizColor.Color(0.5, 0.25, 0, 0, 0);

        Assert.AreEqual(50, pilot.Dimming);
        Assert.AreEqual((byte)255, pilot.R);
        Assert.AreEqual((byte)128, pilot.G);
        Assert.AreEqual((byte)0, pilot.B);
    }

    [TestMethod]
    public void Color_LevelRoundsDimmingUpSoChannelsNeverOverflow()
    {
        // 0.501 → dimming 51 (not 50); red scaled 0.501 * 255 * 100 / 51 = 250
        WizPilot pilot = WizColor.Color(0.501, 0, 0, 0, 0);

        Assert.AreEqual(51, pilot.Dimming);
        Assert.AreEqual((byte)250, pilot.R);
    }

    [TestMethod]
    public void Color_BelowDimmingFloorScalesTheChannelsInstead()
    {
        // 2% white: dimming stays at the 10% floor and the channel drops to
        // 20% of full so the fade continues down instead of stepping to black
        WizPilot pilot = WizColor.Color(0, 0, 0, 0.02, 0.02);

        Assert.AreEqual(WizConstants.MinDimming, pilot.Dimming);
        Assert.AreEqual((byte)51, pilot.C);
        Assert.AreEqual((byte)51, pilot.W);
        Assert.AreEqual((byte)0, pilot.R);
    }

    [TestMethod]
    public void Color_MixesWhiteChannelsAlongsideRgb()
    {
        WizPilot pilot = WizColor.Color(1, 1, 1, 1, 0);

        Assert.AreEqual(100, pilot.Dimming);
        Assert.AreEqual((byte)255, pilot.R);
        Assert.AreEqual((byte)255, pilot.C);
        Assert.AreEqual((byte)0, pilot.W);
    }

    [TestMethod]
    public void Color_ClampsOutOfRangeInput()
    {
        WizPilot pilot = WizColor.Color(2, -1, double.NaN, 0, 0);

        Assert.AreEqual(100, pilot.Dimming);
        Assert.AreEqual((byte)255, pilot.R);
        Assert.AreEqual((byte)0, pilot.G);
        Assert.AreEqual((byte)0, pilot.B);
    }

    [TestMethod]
    public void White_ZeroIsOff()
    {
        Assert.AreSame(WizPilot.Off, WizColor.White(0));
        Assert.AreSame(WizPilot.Off, WizColor.White(0, 0.5));
    }

    [TestMethod]
    public void White_TunableSendsKelvinAndDimmingOnly()
    {
        WizPilot warm = WizColor.White(1, 0);
        WizPilot cool = WizColor.White(0.5, 1);
        WizPilot mid = WizColor.White(0.3, 0.5);

        Assert.AreEqual(WizConstants.KelvinMin, warm.Temp);
        Assert.AreEqual(100, warm.Dimming);
        Assert.IsNull(warm.R);
        Assert.AreEqual(WizConstants.KelvinMax, cool.Temp);
        Assert.AreEqual(50, cool.Dimming);
        Assert.AreEqual(4350, mid.Temp);
        Assert.AreEqual(30, mid.Dimming);
    }

    [TestMethod]
    public void White_DimmableFloorsAtMinDimming()
    {
        WizPilot pilot = WizColor.White(0.01);

        Assert.AreEqual(WizConstants.MinDimming, pilot.Dimming);
        Assert.IsNull(pilot.Temp);
        Assert.IsNull(pilot.R);
    }

    [TestMethod]
    public void DimmingFor_CeilsAndClamps()
    {
        Assert.AreEqual(WizConstants.MinDimming, WizColor.DimmingFor(0.001));
        Assert.AreEqual(10, WizColor.DimmingFor(0.1));
        Assert.AreEqual(11, WizColor.DimmingFor(0.101));
        Assert.AreEqual(100, WizColor.DimmingFor(1));
        Assert.AreEqual(100, WizColor.DimmingFor(5));
    }

    private static JsonElement Params(WizPilot pilot)
    {
        using JsonDocument document = JsonDocument.Parse(pilot.ToDatagram());
        return document.RootElement.GetProperty("params").Clone();
    }
}

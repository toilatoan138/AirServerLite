using AirServerLite.Core;
using Xunit;

namespace AirServerLite.Tests.UI;

public class RtxTelemetryTests
{
    [Fact]
    public void FormatFps_WithoutMemc_FormatsDirectFps()
    {
        string fpsText = RtxTelemetryFormatter.FormatFps(59.94, false);
        Assert.Equal("59.9 FPS", fpsText);
    }

    [Fact]
    public void FormatFps_WithMemc_DoublesFpsAndAddsTag()
    {
        string fpsText = RtxTelemetryFormatter.FormatFps(60.0, true);
        Assert.Equal("120.0 FPS [120Hz MEMC]", fpsText);
    }

    [Fact]
    public void FormatHardwareInfo_DetectsD3D11AndThreads()
    {
        string hwText = RtxTelemetryFormatter.FormatHardwareInfo("D3D11VA", 24);
        Assert.Contains("RTX 4060", hwText);
        Assert.Contains("24T AVX2", hwText);
    }

    [Fact]
    public void FormatEnhancements_FormatsRtxUltraFeatures()
    {
        var settings = RtxFidelitySettings.CreateRtxUltra();
        string text = RtxTelemetryFormatter.FormatEnhancements(settings);

        Assert.Contains("NIS", text);
        Assert.Contains("Vibrance", text);
        Assert.Contains("3D Studio Audio", text);
    }

    [Fact]
    public void FormatFullSummary_CombinesAllTelemetry()
    {
        var settings = RtxFidelitySettings.CreateRtxUltra();
        string summary = RtxTelemetryFormatter.FormatFullSummary(60.0, "D3D11VA", settings);

        Assert.Contains("120.0 FPS [120Hz MEMC]", summary);
        Assert.Contains("RTX 4060", summary);
        Assert.Contains("NIS", summary);
    }
}

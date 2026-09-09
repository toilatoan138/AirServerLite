using AirServerLite.Core;
using Xunit;

namespace AirServerLite.Tests.Core;

public class RtxFidelitySettingsTests
{
    [Fact]
    public void RtxUltraPreset_EnablesNvidiaAndIntelAccelerations()
    {
        var settings = RtxFidelitySettings.CreateRtxUltra();

        Assert.True(settings.EnableNvidiaImageScaling);
        Assert.True(settings.EnableMotionInterpolation);
        Assert.Equal(120, settings.TargetFps);
        Assert.True(settings.EnableRtxDigitalVibrance);
        Assert.True(settings.CpuThreadCount >= 8);
        Assert.Equal(RtxUpscaleMode.NvidiaDirectionalLanczos, settings.UpscaleMode);
    }

    [Fact]
    public void BalancedPreset_SetsStandardParameters()
    {
        var settings = RtxFidelitySettings.CreateBalanced();

        Assert.True(settings.EnableNvidiaImageScaling);
        Assert.False(settings.EnableMotionInterpolation);
        Assert.Equal(60, settings.TargetFps);
    }
}

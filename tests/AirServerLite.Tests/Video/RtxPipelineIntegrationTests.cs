using AirServerLite.Core;
using AirServerLite.Video;
using Xunit;

namespace AirServerLite.Tests.Video;

public class RtxPipelineIntegrationTests
{
    [Fact]
    public void VideoPipeline_InitializesWithRtxUltraSettings()
    {
        var settings = RtxFidelitySettings.CreateRtxUltra();
        using var pipeline = new VideoPipeline(10, settings);

        Assert.NotNull(pipeline.RtxSettings);
        Assert.True(pipeline.RtxSettings.EnableNvidiaImageScaling);
        Assert.True(pipeline.RtxSettings.EnableMotionInterpolation);
        Assert.Equal(120, pipeline.RtxSettings.TargetFps);
        Assert.Equal(RtxUpscaleMode.NvidiaDirectionalLanczos, pipeline.RtxSettings.UpscaleMode);
    }

    [Fact]
    public void VideoPipeline_InitializesWithBalancedSettings()
    {
        var settings = RtxFidelitySettings.CreateBalanced();
        using var pipeline = new VideoPipeline(10, settings);

        Assert.NotNull(pipeline.RtxSettings);
        Assert.True(pipeline.RtxSettings.EnableNvidiaImageScaling);
        Assert.False(pipeline.RtxSettings.EnableMotionInterpolation);
        Assert.Equal(60, pipeline.RtxSettings.TargetFps);
        Assert.Equal(RtxUpscaleMode.Bicubic, pipeline.RtxSettings.UpscaleMode);
    }

    [Fact]
    public void VideoPipeline_InitializesWithPowerSaverSettings()
    {
        var settings = RtxFidelitySettings.CreatePowerSaver();
        using var pipeline = new VideoPipeline(10, settings);

        Assert.NotNull(pipeline.RtxSettings);
        Assert.False(pipeline.RtxSettings.EnableNvidiaImageScaling);
        Assert.False(pipeline.RtxSettings.EnableMotionInterpolation);
        Assert.Equal(60, pipeline.RtxSettings.TargetFps);
        Assert.Equal(RtxUpscaleMode.FastBilinear, pipeline.RtxSettings.UpscaleMode);
    }

    [Fact]
    public void VideoPipeline_Disposal_DoesNotThrow()
    {
        var pipeline = new VideoPipeline(10, RtxFidelitySettings.CreateRtxUltra());
        pipeline.Start();
        var ex = Record.Exception(() => pipeline.Dispose());
        Assert.Null(ex);
    }
}

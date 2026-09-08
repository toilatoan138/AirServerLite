using AirServerLite.AirPlay.Streaming;
using AirServerLite.Video;
using Xunit;

namespace AirServerLite.Tests.Video;

public class VideoPipelineTests
{
    [Fact]
    public void VideoPipeline_AbsorbsBurstWithoutDroppingPackets()
    {
        using var pipeline = new VideoPipeline(2);

        // Submit 50 packets rapidly as in a typical Wi-Fi burst
        for (int i = 0; i < 50; i++)
        {
            pipeline.Submit(new VideoPacket
            {
                Data = new byte[] { 0, 0, 0, 1, 0x65, 0x88 },
                Timestamp = (ulong)i
            });
        }

        Assert.Equal(0, pipeline.PacketsDropped);
    }
}

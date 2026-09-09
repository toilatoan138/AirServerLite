using AirServerLite.Video;
using AirServerLite.Video.Processing;
using Xunit;

namespace AirServerLite.Tests.Video;

public class MotionFrameSynthesizerTests
{
    [Fact]
    public void Synthesize_BlendsMotionAndDoublesFrameRate()
    {
        var f1 = new BgraFrame { Width = 4, Height = 4, Stride = 16, Pixels = new byte[64], Timestamp = 1000 };
        Array.Fill(f1.Pixels, (byte)50);

        var f2 = new BgraFrame { Width = 4, Height = 4, Stride = 16, Pixels = new byte[64], Timestamp = 2000 };
        Array.Fill(f2.Pixels, (byte)150);

        var synth = new MotionFrameSynthesizer(threads: 8);
        var middle = synth.Synthesize(f1, f2);

        Assert.NotNull(middle);
        Assert.Equal(f1.Width, middle.Width);
        Assert.Equal(f1.Height, middle.Height);
        Assert.Equal((ulong)1500, middle.Timestamp);
        // Average of 50 and 150 = 100
        Assert.Equal(100, middle.Pixels[0]);
    }
}

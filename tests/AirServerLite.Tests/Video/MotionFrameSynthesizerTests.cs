using AirServerLite.Video;
using AirServerLite.Video.Processing;
using Xunit;

namespace AirServerLite.Tests.Video;

public class MotionFrameSynthesizerTests
{
    private static BgraFrame Frame(int w, int h, byte fill, ulong timestamp)
    {
        var f = new BgraFrame
        {
            Width = w,
            Height = h,
            Stride = w * 4,
            Pixels = new byte[w * h * 4],
            Timestamp = timestamp
        };
        Array.Fill(f.Pixels, fill);
        return f;
    }

    [Fact]
    public void Synthesize_BlendsMotionAndDoublesFrameRate()
    {
        var f1 = Frame(4, 4, 50, 1000);
        var f2 = Frame(4, 4, 150, 2000);
        var dst = new BgraFrame();

        var synth = new MotionFrameSynthesizer(threads: 8);

        Assert.True(synth.Synthesize(f1, f2, dst));
        Assert.Equal(f1.Width, dst.Width);
        Assert.Equal(f1.Height, dst.Height);
        Assert.Equal((ulong)1500, dst.Timestamp);
        // Average of 50 and 150 = 100
        Assert.Equal(100, dst.Pixels[0]);
        Assert.Equal(100, dst.Pixels[^1]);
    }

    [Fact]
    public void Synthesize_CoversEveryByteWhenLengthDoesNotDivideEvenly()
    {
        // 5x5 gives 100 bytes, which no thread count divides cleanly. An earlier slice
        // calculation floored the size and left the tail bytes untouched.
        var f1 = Frame(5, 5, 0, 0);
        var f2 = Frame(5, 5, 200, 100);
        var dst = new BgraFrame();

        Assert.True(new MotionFrameSynthesizer(threads: 8).Synthesize(f1, f2, dst));
        Assert.All(dst.Pixels, b => Assert.Equal(100, b));
    }

    [Fact]
    public void Synthesize_RefusesMismatchedGeometryInsteadOfAliasing()
    {
        // One frame after a resolution change the two sides disagree. Returning `next` here -
        // as this used to - handed the caller a frame it already owned, so the decoder and the
        // UI ended up writing and reading the same buffer.
        var small = Frame(4, 4, 10, 0);
        var large = Frame(8, 8, 20, 100);
        var dst = new BgraFrame();

        Assert.False(new MotionFrameSynthesizer(threads: 4).Synthesize(small, large, dst));
    }

    [Fact]
    public void Synthesize_RefusesToWriteIntoOneOfItsInputs()
    {
        var f1 = Frame(4, 4, 50, 1000);
        var f2 = Frame(4, 4, 150, 2000);

        var synth = new MotionFrameSynthesizer(threads: 4);

        Assert.False(synth.Synthesize(f1, f2, f1));
        Assert.False(synth.Synthesize(f1, f2, f2));
        Assert.All(f1.Pixels, b => Assert.Equal(50, b));
        Assert.All(f2.Pixels, b => Assert.Equal(150, b));
    }
}

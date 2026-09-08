using AirServerLite.Audio;
using Xunit;

namespace AirServerLite.Tests.Audio;

public class AudioJitterBufferTests
{
    [Fact]
    public void JitterBuffer_InOrderPackets_DeliversInOrder()
    {
        var buffer = new AudioJitterBuffer(preBufferFrames: 3);

        // Push 3 frames (1920 bytes each)
        buffer.Write(1, 1000, new byte[1920], 1920, isPooled: false);
        buffer.Write(2, 1480, new byte[1920], 1920, isPooled: false);
        buffer.Write(3, 1960, new byte[1920], 1920, isPooled: false);

        Assert.True(buffer.IsPrimed);
        Assert.True(buffer.TryRead(out var frame));
        Assert.Equal(1, frame.SequenceNumber);
        Assert.True(buffer.TryRead(out frame));
        Assert.Equal(2, frame.SequenceNumber);
        Assert.True(buffer.TryRead(out frame));
        Assert.Equal(3, frame.SequenceNumber);
    }

    [Fact]
    public void JitterBuffer_OutOfOrderPackets_ReordersCorrectly()
    {
        var buffer = new AudioJitterBuffer(preBufferFrames: 3);

        // Send seq 1, then seq 3, then seq 2
        buffer.Write(1, 1000, new byte[1920], 1920, isPooled: false);
        buffer.Write(3, 1960, new byte[1920], 1920, isPooled: false);
        buffer.Write(2, 1480, new byte[1920], 1920, isPooled: false);

        Assert.True(buffer.IsPrimed);
        Assert.True(buffer.TryRead(out var frame));
        Assert.Equal(1, frame.SequenceNumber);
        Assert.True(buffer.TryRead(out frame));
        Assert.Equal(2, frame.SequenceNumber);
        Assert.True(buffer.TryRead(out frame));
        Assert.Equal(3, frame.SequenceNumber);
    }

    [Fact]
    public void JitterBuffer_DuplicatePacket_DroppedWithoutError()
    {
        var buffer = new AudioJitterBuffer(preBufferFrames: 2);

        buffer.Write(10, 1000, new byte[1920], 1920, isPooled: false);
        buffer.Write(10, 1000, new byte[1920], 1920, isPooled: false); // Duplicate
        buffer.Write(11, 1480, new byte[1920], 1920, isPooled: false);

        Assert.True(buffer.IsPrimed);
        Assert.True(buffer.TryRead(out var frame));
        Assert.Equal(10, frame.SequenceNumber);
        Assert.True(buffer.TryRead(out frame));
        Assert.Equal(11, frame.SequenceNumber);
        Assert.False(buffer.TryRead(out _));
    }
}

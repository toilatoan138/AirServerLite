using AirServerLite.Audio;
using Xunit;

namespace AirServerLite.Tests.Audio;

public class AudioPlayerTests
{
    [Fact]
    public void AudioPlayer_InitializeAndDispose_NoExceptions()
    {
        using var player = new AudioPlayer(44100, 2, 16);
        Assert.NotNull(player);
        player.Volume = 0.75f;
        Assert.Equal(0.75f, player.Volume);
        player.IsMuted = true;
        Assert.True(player.IsMuted);
    }

    [Fact]
    public void AudioPlayer_DeclickRamp_SmoothsEdge()
    {
        // Verify ramp math: ramp from 0 to 1 over 32 samples
        byte[] pcm = new byte[1920];
        for (int i = 0; i < pcm.Length; i += 2)
        {
            // Fill with full-scale 10000
            short val = 10000;
            pcm[i] = (byte)(val & 0xFF);
            pcm[i + 1] = (byte)((val >> 8) & 0xFF);
        }

        AudioPlayer.ApplyRampIn(pcm, 1920, 32);

        // First sample should be 0
        short s0 = (short)(pcm[0] | (pcm[1] << 8));
        Assert.Equal(0, s0);

        // Sample at ramp end should be unchanged
        int midOffset = 32 * 2 * 2; // 32 samples × 2ch × 2 bytes
        short sMid = (short)(pcm[midOffset] | (pcm[midOffset + 1] << 8));
        Assert.Equal(10000, sMid);
    }

    [Fact]
    public void AudioPlayer_PlayPcm_MultipleBuffers_DoesNotFreeze()
    {
        using var player = new AudioPlayer(44100, 2, 16);
        byte[] pcm = new byte[1920]; // 10.88ms frame

        // Writing 20 consecutive buffers must not block or throw
        for (int i = 0; i < 20; i++)
        {
            player.PlayPcm(pcm, pcm.Length);
        }

        Assert.True(player.Volume > 0);
    }

    [Fact]
    public void AudioPlayer_AttachJitterBuffer_SustainedPlayback_PlaysContinuously()
    {
        using var player = new AudioPlayer(44100, 2, 16);
        var jitter = new AudioJitterBuffer(preBufferFrames: 4);
        player.AttachJitterBuffer(jitter);

        // Feed 15 frames into jitter buffer
        for (ushort i = 0; i < 15; i++)
        {
            byte[] frame = new byte[1920];
            jitter.Write(i, (uint)(i * 480), frame, frame.Length, isPooled: false);
        }

        // Allow pump thread to process
        Thread.Sleep(120);

        // Verify player is alive and did not freeze at 4
        Assert.True(player.PlayedCount > 4, $"Expected played > 4, but got {player.PlayedCount}");
    }
}


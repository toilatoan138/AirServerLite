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
}

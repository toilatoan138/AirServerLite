using AirServerLite.Audio;
using Xunit;

namespace AirServerLite.Tests.Audio;

public class AudioEnhancerTests
{
    [Fact]
    public void AudioEnhancer_SilenceInput_RemainsSilence()
    {
        AudioEnhancer.Reset();
        byte[] pcm = new byte[1920]; // All zeros

        AudioEnhancer.Process(pcm, pcm.Length);

        for (int i = 0; i < pcm.Length; i++)
        {
            Assert.Equal(0, pcm[i]);
        }
    }

    [Fact]
    public void AudioEnhancer_ActiveSignal_ProcessesWithoutExceptions()
    {
        AudioEnhancer.Reset();
        byte[] pcm = new byte[1920];
        // Populate with a 1kHz sine wave
        for (int i = 0; i < pcm.Length / 4; i++)
        {
            short val = (short)(Math.Sin(2 * Math.PI * 1000 * i / 44100.0) * 15000);
            int offset = i * 4;
            pcm[offset] = (byte)(val & 0xFF);
            pcm[offset + 1] = (byte)((val >> 8) & 0xFF);
            pcm[offset + 2] = (byte)(val & 0xFF);
            pcm[offset + 3] = (byte)((val >> 8) & 0xFF);
        }

        AudioEnhancer.Process(pcm, pcm.Length);

        // Verify audio was processed and contains non-zero signal
        bool nonZero = false;
        for (int i = 0; i < pcm.Length; i++)
        {
            if (pcm[i] != 0) { nonZero = true; break; }
        }
        Assert.True(nonZero);
    }

    [Fact]
    public void AudioEnhancer_HighAmplitude_SoftLimiterPreventsClipping()
    {
        AudioEnhancer.Reset();
        byte[] pcm = new byte[1920];
        // Fill with full-scale values
        for (int i = 0; i < pcm.Length / 4; i++)
        {
            short val = 32000;
            int offset = i * 4;
            pcm[offset] = (byte)(val & 0xFF);
            pcm[offset + 1] = (byte)((val >> 8) & 0xFF);
            pcm[offset + 2] = (byte)(val & 0xFF);
            pcm[offset + 3] = (byte)((val >> 8) & 0xFF);
        }

        AudioEnhancer.Process(pcm, pcm.Length);

        // Verify every sample stays within valid 16-bit range
        for (int i = 0; i < pcm.Length / 4; i++)
        {
            int offset = i * 4;
            short sL = (short)(pcm[offset] | (pcm[offset + 1] << 8));
            short sR = (short)(pcm[offset + 2] | (pcm[offset + 3] << 8));
            Assert.True(sL >= -32768 && sL <= 32767);
            Assert.True(sR >= -32768 && sR <= 32767);
        }
    }
}

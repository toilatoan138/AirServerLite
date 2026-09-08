using AirServerLite.Audio;
using Xunit;

namespace AirServerLite.Tests.Audio;

public class AudioDecoderTests
{
    [Fact]
    public void AudioDecoder_InitialProperties_AreStandardAirPlay()
    {
        using var decoder = new AudioDecoder();
        Assert.Equal(44100, decoder.SampleRate);
        Assert.Equal(2, decoder.Channels);
        Assert.Equal(16, decoder.BitsPerSample);
    }
}

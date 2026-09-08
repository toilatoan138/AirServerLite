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

    [Fact]
    public void AudioDecoder_AlacCodec_InitializesSuccessfully()
    {
        using var decoder = new AudioDecoder(AudioCodecType.Alac, sampleRate: 44100, channels: 2, framesPerPacket: 352);
        Assert.NotNull(decoder);
        Assert.Equal(AudioCodecType.Alac, decoder.CodecType);
        Assert.Equal(44100, decoder.SampleRate);
        Assert.Equal(2, decoder.Channels);
    }
}

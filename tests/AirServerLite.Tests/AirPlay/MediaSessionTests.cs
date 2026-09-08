using AirServerLite.AirPlay;
using Xunit;

namespace AirServerLite.Tests.AirPlay;

public class MediaSessionTests
{
    [Fact]
    public void MediaSession_PlaybackInfo_ContainsRequiredAppleTvFields()
    {
        var session = new MediaSession("https://example.com/stream.m3u8", startPositionSeconds: 15.0f);
        var plistXml = session.BuildPlaybackInfoXml();

        Assert.Contains("<key>duration</key>", plistXml);
        Assert.Contains("<key>position</key>", plistXml);
        Assert.Contains("<key>rate</key>", plistXml);
        Assert.Contains("<key>readyToPlay</key>", plistXml);
    }
}

using AirServerLite.Discovery;
using Xunit;

namespace AirServerLite.Tests.UI;

public class LocalVideoPlaybackTests
{
    [Fact]
    public void IsSupportedVideoFile_ValidExtensions_ReturnsTrue()
    {
        Assert.True(DialServer.IsSupportedVideoFile("video.mp4"));
        Assert.True(DialServer.IsSupportedVideoFile("C:\\Users\\video.mkv"));
        Assert.True(DialServer.IsSupportedVideoFile("movie.webm"));
        Assert.True(DialServer.IsSupportedVideoFile("clip.mov"));
        Assert.False(DialServer.IsSupportedVideoFile("file.txt"));
        Assert.False(DialServer.IsSupportedVideoFile(""));
    }
}

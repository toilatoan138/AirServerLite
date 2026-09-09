using System.Windows;
using AirServerLite.Input;
using Xunit;

namespace AirServerLite.Tests.Input;

public class AndroidInputSinkTests
{
    [Fact]
    public void MapNormalizedPointToDevice_ConvertsCorrectly()
    {
        var norm = new Point(0.5, 0.5);
        int devX = (int)(norm.X * 1080);
        int devY = (int)(norm.Y * 1920);

        Assert.Equal(540, devX);
        Assert.Equal(960, devY);
    }
}

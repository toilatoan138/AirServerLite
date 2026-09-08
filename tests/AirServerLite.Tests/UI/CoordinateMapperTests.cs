using System.Windows;
using AirServerLite.Input;
using Xunit;

namespace AirServerLite.Tests.UI;

public class CoordinateMapperTests
{
    [Fact]
    public void CoordinateMapper_FullscreenLandscape_MapsCoordinatesAccurately()
    {
        var mapper = new CoordinateMapper();
        // 1920x1080 video displayed on a 1920x1080 fullscreen viewport
        mapper.UpdateVideoSize(1920, 1080);
        mapper.UpdateControlSize(1920, 1080);
        var window = new WdaWindowSize(844, 390);

        var point = mapper.ToDevicePoint(new Point(960, 540), window);
        Assert.NotNull(point);
        Assert.InRange(point.Value.X, 420, 424);
        Assert.InRange(point.Value.Y, 193, 197);
    }

    [Fact]
    public void CoordinateMapper_Letterboxed_ReturnsNullOutsideVideo()
    {
        var mapper = new CoordinateMapper();
        // 1080x1920 portrait video in 1920x1080 landscape screen (black bars left and right)
        mapper.UpdateVideoSize(1080, 1920);
        mapper.UpdateControlSize(1920, 1080);
        var window = new WdaWindowSize(390, 844);

        // Click far left in black letterbox
        var point = mapper.ToDevicePoint(new Point(50, 540), window);
        Assert.Null(point);
    }
}

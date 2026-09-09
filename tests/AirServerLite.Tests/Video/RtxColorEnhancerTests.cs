using AirServerLite.Video.Processing;
using Xunit;

namespace AirServerLite.Tests.Video;

public class RtxColorEnhancerTests
{
    [Fact]
    public void Enhance_ExpandsColorGamutAndMaintainsAlpha()
    {
        byte[] pixels = new byte[] { 80, 120, 200, 255 }; // Saturated pixel
        var enhancer = new RtxColorEnhancer(0.35f);
        enhancer.Enhance(pixels, 1, 1, 4);

        Assert.Equal(255, pixels[3]); // Alpha preserved
        Assert.True(pixels[2] >= 200); // Red channel vibrance enhanced
    }
}

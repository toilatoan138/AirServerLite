using AirServerLite.Video.Processing;
using Xunit;

namespace AirServerLite.Tests.Video;

public class NvidiaImageScalerTests
{
    [Fact]
    public void Process_SharpensEdgesAndMaintainsAlpha()
    {
        int width = 8;
        int height = 8;
        byte[] src = new byte[width * height * 4];
        byte[] dst = new byte[width * height * 4];

        // Fill with alternating edge pattern
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte val = (byte)(x < 4 ? 60 : 220);
                int idx = (y * width + x) * 4;
                src[idx] = val;     // B
                src[idx + 1] = val; // G
                src[idx + 2] = val; // R
                src[idx + 3] = 255; // A
            }
        }

        var nis = new NvidiaImageScaler(0.85f);
        nis.Process(src, dst, width, height, width * 4);

        // Alpha channel preserved
        Assert.Equal(255, dst[3]);
        // Pixel at boundary should show enhanced contrast
        int edgeDark = (4 * width + 3) * 4;
        int edgeLight = (4 * width + 4) * 4;
        Assert.True(dst[edgeDark] <= 65);
        Assert.True(dst[edgeLight] >= 215);
    }
}

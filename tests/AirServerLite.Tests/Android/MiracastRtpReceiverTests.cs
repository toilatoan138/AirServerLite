using AirServerLite.Android.Miracast;
using Xunit;

namespace AirServerLite.Tests.Android;

public class MiracastRtpReceiverTests
{
    [Fact]
    public void StripRtpHeader_Standard12ByteHeader_ReturnsPayload()
    {
        byte[] packet = new byte[200];
        packet[0] = 0x80; // V=2, P=0, X=0, CC=0
        packet[1] = 0x60; // M=0, PT=96
        packet[12] = 0x47; // First byte of MPEG-TS payload

        var payload = MiracastRtpReceiver.ExtractRtpPayload(packet);
        Assert.Equal(188, payload.Length);
        Assert.Equal(0x47, payload[0]);
    }
}

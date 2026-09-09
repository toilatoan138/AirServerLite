using AirServerLite.Android.Scrcpy;
using Xunit;

namespace AirServerLite.Tests.Android;

public class ScrcpyProtocolTests
{
    [Fact]
    public void BuildTouchPacket_DownAction_SerializesCorrectHeaderAndFields()
    {
        byte[] packet = ScrcpyProtocol.BuildInjectTouch(
            action: ScrcpyTouchAction.Down,
            pointerId: 0,
            x: 540,
            y: 960,
            screenWidth: 1080,
            screenHeight: 1920,
            pressure: 1.0f);

        Assert.Equal(32, packet.Length);
        Assert.Equal((byte)ScrcpyControlMsgType.InjectTouch, packet[0]);
        Assert.Equal((byte)ScrcpyTouchAction.Down, packet[1]);
    }

    [Fact]
    public void BuildKeycodePacket_BackKey_SerializesCorrectKeycode()
    {
        byte[] packet = ScrcpyProtocol.BuildInjectKeycode(
            action: ScrcpyKeyAction.Down,
            keycode: AndroidKeycode.Back);

        Assert.Equal(14, packet.Length);
        Assert.Equal((byte)ScrcpyControlMsgType.InjectKeycode, packet[0]);
        Assert.Equal((int)AndroidKeycode.Back, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(2)));
    }
}

using System.Buffers.Binary;

namespace AirServerLite.Android.Scrcpy;

public enum ScrcpyControlMsgType : byte
{
    InjectKeycode = 0,
    InjectText = 1,
    InjectTouch = 2,
    InjectScroll = 3,
    BackOrScreenOn = 4,
    ExpandNotification = 5,
    CollapseNotification = 6,
    GetClipboard = 7,
    SetClipboard = 8,
    SetScreenPowerMode = 9,
    RotateDevice = 10
}

public enum ScrcpyTouchAction : byte
{
    Down = 0,
    Up = 1,
    Move = 2
}

public enum ScrcpyKeyAction : byte
{
    Down = 0,
    Up = 1
}

public static class AndroidKeycode
{
    public const int Home = 3;
    public const int Back = 4;
    public const int VolumeUp = 24;
    public const int VolumeDown = 25;
    public const int Power = 26;
    public const int AppSwitch = 187;
}

public static class ScrcpyProtocol
{
    public static byte[] BuildInjectTouch(ScrcpyTouchAction action, ulong pointerId, int x, int y, ushort screenWidth, ushort screenHeight, float pressure)
    {
        byte[] packet = new byte[32];
        packet[0] = (byte)ScrcpyControlMsgType.InjectTouch;
        packet[1] = (byte)action;
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(2), pointerId);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(10), x);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(14), y);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(18), screenWidth);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), screenHeight);
        
        ushort pressureInt = (ushort)(Math.Clamp(pressure, 0f, 1f) * 65535);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), pressureInt);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(24), 1); // ActionButton: PRIMARY
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(28), 1); // Buttons: PRIMARY
        return packet;
    }

    public static byte[] BuildInjectKeycode(ScrcpyKeyAction action, int keycode, int metastate = 0)
    {
        byte[] packet = new byte[14];
        packet[0] = (byte)ScrcpyControlMsgType.InjectKeycode;
        packet[1] = (byte)action;
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(2), keycode);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(6), 0); // repeat
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(10), metastate);
        return packet;
    }

    public static byte[] BuildInjectScroll(int x, int y, ushort screenWidth, ushort screenHeight, int hScroll, int vScroll)
    {
        byte[] packet = new byte[21];
        packet[0] = (byte)ScrcpyControlMsgType.InjectScroll;
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(1), x);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(5), y);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(9), screenWidth);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(11), screenHeight);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(13), hScroll);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(17), vScroll);
        return packet;
    }
}

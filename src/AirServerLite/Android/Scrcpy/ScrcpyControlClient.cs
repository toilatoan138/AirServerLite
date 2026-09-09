using System.Net.Sockets;
using AirServerLite.Core;

namespace AirServerLite.Android.Scrcpy;

/// <summary>
/// Direct sub-millisecond reverse control for Android devices over TCP socket.
/// Handles taps, gestures, mouse-wheel scrolling, Back, Home, and text input.
/// </summary>
public sealed class ScrcpyControlClient : IDisposable
{
    private const string Tag = "scrcpy-control";
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    public bool IsConnected => _client.Connected;

    public ScrcpyControlClient(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public void SendTouch(ScrcpyTouchAction action, int x, int y, ushort screenWidth, ushort screenHeight)
    {
        if (!IsConnected) return;
        byte[] packet = ScrcpyProtocol.BuildInjectTouch(action, 0, x, y, screenWidth, screenHeight, 1.0f);
        try
        {
            _stream.Write(packet);
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"SendTouch error: {ex.Message}");
        }
    }

    public void SendBack()
    {
        SendKey(AndroidKeycode.Back);
    }

    public void SendHome()
    {
        SendKey(AndroidKeycode.Home);
    }

    public void SendKey(int keycode)
    {
        if (!IsConnected) return;
        try
        {
            _stream.Write(ScrcpyProtocol.BuildInjectKeycode(ScrcpyKeyAction.Down, keycode));
            _stream.Write(ScrcpyProtocol.BuildInjectKeycode(ScrcpyKeyAction.Up, keycode));
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"SendKey error: {ex.Message}");
        }
    }

    public void SendScroll(int x, int y, ushort screenWidth, ushort screenHeight, int vScroll)
    {
        if (!IsConnected) return;
        try
        {
            _stream.Write(ScrcpyProtocol.BuildInjectScroll(x, y, screenWidth, screenHeight, 0, vScroll));
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"SendScroll error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { _client.Close(); } catch { }
    }
}

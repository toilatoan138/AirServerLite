using System.Windows;
using AirServerLite.Android.Scrcpy;

namespace AirServerLite.Input;

public sealed class AndroidInputSink : IInputSink
{
    private readonly ScrcpyControlClient? _control;
    private readonly ushort _width;
    private readonly ushort _height;

    public bool IsConnected => _control != null && _control.IsConnected;

    public AndroidInputSink(ScrcpyControlClient? control, ushort width = 1080, ushort height = 1920)
    {
        _control = control;
        _width = width;
        _height = height;
    }

    public void Tap(Point p)
    {
        _control?.SendTouch(ScrcpyTouchAction.Down, (int)p.X, (int)p.Y, _width, _height);
        _control?.SendTouch(ScrcpyTouchAction.Up, (int)p.X, (int)p.Y, _width, _height);
    }

    public void LongPress(Point p, TimeSpan duration)
    {
        _control?.SendTouch(ScrcpyTouchAction.Down, (int)p.X, (int)p.Y, _width, _height);
        Task.Delay(duration).ContinueWith(_ =>
            _control?.SendTouch(ScrcpyTouchAction.Up, (int)p.X, (int)p.Y, _width, _height));
    }

    public void Swipe(Point start, Point end, TimeSpan duration)
    {
        _control?.SendTouch(ScrcpyTouchAction.Down, (int)start.X, (int)start.Y, _width, _height);
        _control?.SendTouch(ScrcpyTouchAction.Move, (int)end.X, (int)end.Y, _width, _height);
        _control?.SendTouch(ScrcpyTouchAction.Up, (int)end.X, (int)end.Y, _width, _height);
    }

    public void SendText(string text)
    {
    }

    public void Back() => _control?.SendBack();
    public void Home() => _control?.SendHome();
    public void Scroll(Point p, int delta) => _control?.SendScroll((int)p.X, (int)p.Y, _width, _height, delta);
}

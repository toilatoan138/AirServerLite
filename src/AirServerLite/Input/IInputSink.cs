using System.Windows;

namespace AirServerLite.Input;

public interface IInputSink
{
    bool IsConnected { get; }
    void Tap(Point devicePoint);
    void LongPress(Point devicePoint, TimeSpan duration);
    void Swipe(Point startPoint, Point endPoint, TimeSpan duration);
    void SendText(string text);
    void Back();
    void Home();
    void Scroll(Point devicePoint, int delta);
}

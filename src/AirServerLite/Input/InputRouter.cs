using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using AirServerLite.Core;

namespace AirServerLite.Input;

/// <summary>
/// Turns WPF mouse and keyboard events into iOS gestures.
///
/// Gesture classification happens on mouse-up, because that is the first moment we know
/// whether a press was a tap, a long press, or a drag:
///
///   press &lt; TapMaxDurationMs and moved &lt; TapMaxMovePx  -> tap
///   press longer, barely moved                             -> long press
///   moved further                                          -> swipe along the path
///
/// Everything dispatches fire-and-forget onto the thread pool. A WDA round trip is 5-15 ms
/// over USB, and awaiting it on the UI thread would stutter the video.
///
/// Keystrokes are coalesced: typing "hello" as five separate /wda/keys calls is five round
/// trips and iOS drops characters when they overlap. A 30 ms idle window batches them into
/// one request, which is both faster and more reliable.
/// </summary>
public sealed class InputRouter : IDisposable
{
    private const string Tag = "input";

    private readonly WdaClient _wda;
    private readonly CoordinateMapper _mapper;
    private readonly InputSettings _settings;

    private Point _pressControlPoint;
    private Point? _pressDevicePoint;
    private readonly Stopwatch _pressTimer = new();
    private bool _pressed;

    private readonly StringBuilder _pendingText = new();
    private readonly object _textLock = new();
    private Timer? _textFlushTimer;

    private bool _disposed;

    public bool Enabled { get; set; } = true;

    public event Action<string>? ActionPerformed;

    public InputRouter(WdaClient wda, CoordinateMapper mapper, InputSettings settings)
    {
        _wda = wda;
        _mapper = mapper;
        _settings = settings;
    }

    // ---------------------------------------------------------------- mouse

    public void OnMouseDown(Point controlPoint, MouseButton button)
    {
        if (!Enabled || !_wda.IsConnected) return;

        if (button == MouseButton.Right)
        {
            Dispatch("Home", _wda.HomeAsync(CancellationToken.None));
            return;
        }

        if (button != MouseButton.Left) return;

        _pressControlPoint = controlPoint;
        _pressed = true;
        _pressTimer.Restart();

        // Resolve the device point up front so a drag has a valid origin even if the pointer
        // later leaves the video rect.
        _ = Task.Run(async () =>
        {
            try
            {
                var size = await _wda.GetWindowSizeAsync().ConfigureAwait(false);
                if (size is not null) _pressDevicePoint = _mapper.ToDevicePoint(controlPoint, size);
            }
            catch (Exception ex)
            {
                Log.Debug(Tag, $"Failed to query window size on mouse down: {ex.Message}");
            }
        });
    }

    public void OnMouseUp(Point controlPoint, MouseButton button)
    {
        if (!Enabled || !_pressed || button != MouseButton.Left) return;

        _pressed = false;
        _pressTimer.Stop();

        var elapsed = (int)_pressTimer.ElapsedMilliseconds;
        var dx = controlPoint.X - _pressControlPoint.X;
        var dy = controlPoint.Y - _pressControlPoint.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        var from = _pressControlPoint;
        _pressDevicePoint = null;

        _ = Task.Run(async () =>
        {
            try
            {
                var size = await _wda.GetWindowSizeAsync().ConfigureAwait(false);
                if (size is null) return;

                if (_mapper.OrientationMismatch(size))
                    Log.Warn(Tag, "Video and device orientation disagree - taps may be transposed " +
                                  "until WebDriverAgent catches up");

                var start = _mapper.ToDevicePoint(from, size);
                if (start is null) return; // clicked the letterbox

                if (distance <= _settings.TapMaxMovePx)
                {
                    if (elapsed <= _settings.TapMaxDurationMs)
                    {
                        await _wda.TapAsync(start.Value.X, start.Value.Y).ConfigureAwait(false);
                        Report($"tap ({start.Value.X:F0}, {start.Value.Y:F0})");
                    }
                    else
                    {
                        await _wda.LongPressAsync(start.Value.X, start.Value.Y, elapsed).ConfigureAwait(false);
                        Report($"long press {elapsed} ms");
                    }
                }
                else
                {
                    var end = _mapper.ToDevicePoint(controlPoint, size);
                    if (end is null) return;

                    // Clamp the duration: iOS treats a very slow drag as a scrub and a very
                    // fast one as a flick, and neither matches what the user did with a mouse.
                    var duration = Math.Clamp(elapsed, 80, 1200);
                    await _wda.SwipeAsync(start.Value.X, start.Value.Y, end.Value.X, end.Value.Y, duration)
                              .ConfigureAwait(false);
                    Report($"swipe -> ({end.Value.X:F0}, {end.Value.Y:F0}) in {duration} ms");
                }
            }
            catch (Exception ex)
            {
                Log.Error(Tag, "Gesture dispatch failed", ex);
            }
        });
    }

    /// <summary>Wheel scroll becomes a swipe in the opposite direction (natural scrolling).</summary>
    public void OnMouseWheel(Point controlPoint, int delta)
    {
        if (!Enabled || !_wda.IsConnected) return;

        _ = Task.Run(async () =>
        {
            try
            {
                var size = await _wda.GetWindowSizeAsync().ConfigureAwait(false);
                if (size is null) return;

                var centre = _mapper.ToDevicePoint(controlPoint, size);
                if (centre is null) return;

                var distance = _settings.WheelSwipeDistance * Math.Sign(delta);

                // Keep both endpoints on screen, otherwise iOS reads it as an edge gesture
                // (Control Centre, app switcher) instead of a scroll.
                var y1 = Math.Clamp(centre.Value.Y - distance / 2.0, 40, size.Height - 40);
                var y2 = Math.Clamp(centre.Value.Y + distance / 2.0, 40, size.Height - 40);

                await _wda.SwipeAsync(centre.Value.X, y1, centre.Value.X, y2, 180).ConfigureAwait(false);
                Report($"scroll {(delta > 0 ? "up" : "down")}");
            }
            catch (Exception ex)
            {
                Log.Error(Tag, "Wheel dispatch failed", ex);
            }
        });
    }

    // ---------------------------------------------------------------- keyboard

    public void OnTextInput(string text)
    {
        if (!Enabled || !_wda.IsConnected || string.IsNullOrEmpty(text)) return;

        lock (_textLock)
        {
            _pendingText.Append(text);
            _textFlushTimer ??= new Timer(FlushText, null, Timeout.Infinite, Timeout.Infinite);
            _textFlushTimer.Change(30, Timeout.Infinite);
        }
    }

    /// <summary>Returns true if the key was consumed as a special key.</summary>
    public bool OnKeyDown(Key key, ModifierKeys modifiers)
    {
        if (!Enabled || !_wda.IsConnected) return false;

        // Ctrl+H is a keyboard-only way to reach Home without giving up right-click.
        if (modifiers.HasFlag(ModifierKeys.Control) && key == Key.H)
        {
            Dispatch("Home", _wda.HomeAsync(CancellationToken.None));
            return true;
        }

        var special = KeyMap.ToWdaKey(key);
        if (special is null) return false;

        FlushTextNow();
        Dispatch(KeyMap.Describe(key), _wda.TypeTextAsync(special));
        return true;
    }

    private void FlushText(object? _) => FlushTextNow();

    private void FlushTextNow()
    {
        string text;
        lock (_textLock)
        {
            if (_pendingText.Length == 0) return;
            text = _pendingText.ToString();
            _pendingText.Clear();
        }

        Dispatch($"type \"{text}\"", _wda.TypeTextAsync(text));
    }

    // ---------------------------------------------------------------- hardware buttons

    public void PressHome() => Dispatch("Home", _wda.HomeAsync(CancellationToken.None));
    public void PressVolumeUp() => Dispatch("Volume +", _wda.PressButtonAsync("volumeUp"));
    public void PressVolumeDown() => Dispatch("Volume -", _wda.PressButtonAsync("volumeDown"));
    public void PressLock() => Dispatch("Lock", _wda.LockAsync());

    private void Dispatch(string description, Task work)
    {
        Report(description);
        _ = work.ContinueWith(t =>
        {
            if (t.IsFaulted)
                Log.Error(Tag, description + " failed", t.Exception!.GetBaseException());
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private void Report(string what)
    {
        Log.Debug(Tag, what);
        ActionPerformed?.Invoke(what);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FlushTextNow();
        _textFlushTimer?.Dispose();
    }
}

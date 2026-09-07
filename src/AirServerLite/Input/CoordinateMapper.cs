using System.Windows;
using AirServerLite.Core;

namespace AirServerLite.Input;

/// <summary>
/// Maps a mouse position inside the WPF display control to a coordinate WebDriverAgent
/// understands.
///
/// Three coordinate spaces are involved and conflating any two of them puts every tap in the
/// wrong place:
///
///   1. Control space   - device-independent WPF units, includes letterbox bars
///   2. Video space     - decoded frame pixels (e.g. 886 x 1920)
///   3. Device points   - what WDA wants (e.g. 414 x 896 on an iPhone XS Max)
///
/// We never assume a fixed scale factor between 2 and 3. The mirror resolution is negotiated
/// per session and differs between iOS versions, so we normalise through [0,1] and let WDA's
/// own reported window size supply the final scale. That also makes rotation free: when the
/// phone rotates, both the frame and the window size change together.
/// </summary>
public sealed class CoordinateMapper
{
    private const string Tag = "coords";

    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }
    public double ControlWidth { get; private set; }
    public double ControlHeight { get; private set; }

    public void UpdateVideoSize(int width, int height)
    {
        if (width == VideoWidth && height == VideoHeight) return;
        VideoWidth = width;
        VideoHeight = height;
        Log.Debug(Tag, $"Video size {width}x{height}");
    }

    public void UpdateControlSize(double width, double height)
    {
        ControlWidth = width;
        ControlHeight = height;
    }

    /// <summary>
    /// The rectangle the video actually occupies inside the control, given Stretch=Uniform.
    /// Clicks on the letterbox bars fall outside this and must be ignored.
    /// </summary>
    public Rect GetVideoRect()
    {
        if (VideoWidth <= 0 || VideoHeight <= 0 || ControlWidth <= 0 || ControlHeight <= 0)
            return Rect.Empty;

        var videoAspect = (double)VideoWidth / VideoHeight;
        var controlAspect = ControlWidth / ControlHeight;

        double w, h;
        if (controlAspect > videoAspect)
        {
            // Control is wider than the video: pillarbox, height is the binding constraint.
            h = ControlHeight;
            w = h * videoAspect;
        }
        else
        {
            w = ControlWidth;
            h = w / videoAspect;
        }

        return new Rect((ControlWidth - w) / 2, (ControlHeight - h) / 2, w, h);
    }

    /// <summary>
    /// Control point -> normalised [0,1] within the video image.
    /// Returns null when the point is on a letterbox bar.
    /// </summary>
    public Point? ToNormalized(Point controlPoint)
    {
        var rect = GetVideoRect();
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return null;
        if (!rect.Contains(controlPoint)) return null;

        return new Point((controlPoint.X - rect.X) / rect.Width,
                         (controlPoint.Y - rect.Y) / rect.Height);
    }

    /// <summary>Control point -> device points, using WDA's reported window size.</summary>
    public Point? ToDevicePoint(Point controlPoint, WdaWindowSize window)
    {
        var norm = ToNormalized(controlPoint);
        if (norm is null) return null;

        // Clamp rather than reject: a click one pixel outside due to rounding should still
        // reach the edge of the screen, which is where a lot of iOS affordances live.
        var x = Math.Clamp(norm.Value.X * window.Width, 0, window.Width - 1);
        var y = Math.Clamp(norm.Value.Y * window.Height, 0, window.Height - 1);
        return new Point(x, y);
    }

    /// <summary>
    /// True when the video frame and the device window disagree about orientation. WDA
    /// occasionally lags a rotation by a frame or two and taps land transposed until it
    /// catches up - worth surfacing rather than silently mis-tapping.
    /// </summary>
    public bool OrientationMismatch(WdaWindowSize window)
    {
        if (VideoWidth <= 0 || VideoHeight <= 0) return false;
        var videoLandscape = VideoWidth > VideoHeight;
        var windowLandscape = window.Width > window.Height;
        return videoLandscape != windowLandscape;
    }
}

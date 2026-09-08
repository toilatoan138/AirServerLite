using System.Globalization;

namespace AirServerLite.AirPlay;

/// <summary>
/// Tracks playback state for AirPlay Media Streaming (e.g. YouTube app / Safari).
/// Formats XML plists required by iOS for GET /playback-info and tracks playback rate and scrubbing.
/// </summary>
public sealed class MediaSession
{
    public string ContentLocation { get; }
    public float PositionSeconds { get; set; }
    public float DurationSeconds { get; set; } = 3600f; // Default stream duration
    public float Rate { get; set; } = 1.0f; // 1.0 = playing, 0.0 = paused
    public string? Uuid { get; set; }
    public string? ClientProcName { get; set; }

    public MediaSession(string contentLocation, float startPositionSeconds = 0f)
    {
        ContentLocation = contentLocation;
        PositionSeconds = startPositionSeconds;
    }

    public string BuildPlaybackInfoXml()
    {
        var pos = PositionSeconds.ToString("F6", CultureInfo.InvariantCulture);
        var dur = DurationSeconds.ToString("F6", CultureInfo.InvariantCulture);
        var rate = Rate.ToString("F6", CultureInfo.InvariantCulture);

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
               "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\r\n" +
               "<plist version=\"1.0\">\r\n" +
               "<dict>\r\n" +
               $"    <key>duration</key><real>{dur}</real>\r\n" +
               $"    <key>position</key><real>{pos}</real>\r\n" +
               $"    <key>rate</key><real>{rate}</real>\r\n" +
               "    <key>playbackBufferEmpty</key><true/>\r\n" +
               "    <key>playbackBufferFull</key><false/>\r\n" +
               "    <key>playbackLikelyToKeepUp</key><true/>\r\n" +
               "    <key>readyToPlay</key><true/>\r\n" +
               "    <key>loadedTimeRanges</key>\r\n" +
               "    <array>\r\n" +
               "        <dict>\r\n" +
               "            <key>duration</key><real>60.0</real>\r\n" +
               "            <key>start</key><real>0.0</real>\r\n" +
               "        </dict>\r\n" +
               "    </array>\r\n" +
               "    <key>seekableTimeRanges</key>\r\n" +
               "    <array>\r\n" +
               "        <dict>\r\n" +
               $"            <key>duration</key><real>{dur}</real>\r\n" +
               "            <key>start</key><real>0.0</real>\r\n" +
               "        </dict>\r\n" +
               "    </array>\r\n" +
               "</dict>\r\n" +
               "</plist>\r\n";
    }
}

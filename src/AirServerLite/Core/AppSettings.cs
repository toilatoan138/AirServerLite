using System.IO;
using System.Text.Json;

namespace AirServerLite.Core;

public sealed class VideoSettings
{
    public string FFmpegPath { get; set; } = "ffmpeg/bin";
    public int MaxQueuedFrames { get; set; } = 2;
    public bool LowLatency { get; set; } = true;
    public int MaxWidth { get; set; } = 2560;
    public int MaxHeight { get; set; } = 1440;
}

public sealed class InputSettings
{
    public bool Enabled { get; set; } = true;
    public string WdaBaseUrl { get; set; } = "http://127.0.0.1:8100";
    public bool AutoStartIProxy { get; set; } = true;
    public string IProxyPath { get; set; } = "tools/iproxy.exe";
    public int LocalPort { get; set; } = 8100;
    public int DevicePort { get; set; } = 8100;
    public string Udid { get; set; } = "";
    public int TapMaxDurationMs { get; set; } = 300;
    public int TapMaxMovePx { get; set; } = 8;
    public int WheelSwipeDistance { get; set; } = 240;
    public int RequestTimeoutMs { get; set; } = 4000;
}

public sealed class LoggingSettings
{
    public string MinLevel { get; set; } = "Debug";
    public bool TraceRtsp { get; set; } = true;
}

public sealed class AppSettings
{
    /// <summary>Set when appsettings.json existed but could not be parsed, so the UI can
    /// say so instead of silently running on defaults.</summary>
    public string? LoadError { get; private set; }

    public string DeviceName { get; set; } = "AirServer-LITE";
    public int AirPlayPort { get; set; } = 7000;
    public bool AdvertiseRaop { get; set; } = true;
    public string PreferredInterface { get; set; } = "";
    public VideoSettings Video { get; set; } = new();
    public InputSettings Input { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public static AppSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
                if (s is not null)
                {
                    Log.Info("config", "Loaded " + path);
                    return s;
                }
            }
            Log.Warn("config", path + " not found - using defaults");
        }
        catch (Exception ex)
        {
            Log.Error("config", "Failed to parse appsettings.json, using defaults", ex);
            return new AppSettings { LoadError = ex.Message };
        }
        return new AppSettings();
    }

    public LogLevel ParsedLogLevel =>
        Enum.TryParse<LogLevel>(Logging.MinLevel, ignoreCase: true, out var l) ? l : LogLevel.Debug;

    /// <summary>Resolve a possibly-relative path against the application directory.</summary>
    public static string ResolvePath(string p) =>
        string.IsNullOrWhiteSpace(p) ? p
        : Path.IsPathRooted(p) ? p
        : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, p));
}

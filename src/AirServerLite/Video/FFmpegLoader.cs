using System.IO;
using AirServerLite.Core;
using FFmpeg.AutoGen;

namespace AirServerLite.Video;

/// <summary>
/// Locates the native FFmpeg shared libraries and binds them once per process.
///
/// FFmpeg.AutoGen 9.0 generates P/Invoke against FFmpeg 9.0's ABI. Pointing it at a different
/// major version produces either a BadImageFormatException or, worse, silent memory corruption,
/// so we verify the reported version at startup rather than finding out during a session.
/// </summary>
public static class FFmpegLoader
{
    private const string Tag = "ffmpeg";
    private const int ExpectedAvCodecMajor = 63; // FFmpeg 9.0 ships libavcodec 63

    public static bool IsLoaded { get; private set; }
    public static string? LoadError { get; private set; }
    public static string Version { get; private set; } = "not loaded";

    public static bool Initialize(string configuredPath)
    {
        if (IsLoaded) return true;

        try
        {
            var path = ResolveLibraryPath(configuredPath);
            if (path is null)
            {
                LoadError =
                    "FFmpeg shared libraries not found. Download an FFmpeg 9.0 *shared* build " +
                    "(BtbN/FFmpeg-Builds, ffmpeg-n9.0-latest-win64-gpl-shared-9.0.zip) and put its " +
                    "bin/*.dll into '" + AppSettings.ResolvePath(configuredPath) + "'.";
                Log.Error(Tag, LoadError);
                return false;
            }

            ffmpeg.RootPath = path;
            DynamicallyLoadedBindings.Initialize();

            var versionNumber = ffmpeg.avcodec_version();
            var major = (int)(versionNumber >> 16);
            Version = $"libavcodec {major}.{(versionNumber >> 8) & 0xFF}.{versionNumber & 0xFF}";

            if (major != ExpectedAvCodecMajor)
            {
                LoadError =
                    $"Found {Version} but this build binds libavcodec {ExpectedAvCodecMajor} (FFmpeg 9.0). " +
                    "Mixing ABI versions will crash or corrupt memory - install an FFmpeg 9.0 shared build.";
                Log.Error(Tag, LoadError);
                return false;
            }

            // FFmpeg is chatty on stderr; keep it out of the way unless we are debugging.
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

            Log.Info(Tag, $"Loaded {Version} from {path}");
            IsLoaded = true;
            return true;
        }
        catch (Exception ex)
        {
            LoadError = "Failed to load FFmpeg: " + ex.Message;
            Log.Error(Tag, "FFmpeg initialisation failed", ex);
            return false;
        }
    }

    private static string? ResolveLibraryPath(string configuredPath)
    {
        var candidates = new List<string>();

        // A single-file build unpacks FFmpeg into a cache directory; that copy is the one the
        // OS loader has been told about, so it must win over anything on PATH.
        if (NativeAssets.Directory is { } unpacked)
            candidates.Add(unpacked);

        if (!string.IsNullOrWhiteSpace(configuredPath))
            candidates.Add(AppSettings.ResolvePath(configuredPath));

        candidates.Add(AppContext.BaseDirectory);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin"));

        // Fall back to anything already on PATH (e.g. a winget/choco install).
        foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            if (!string.IsNullOrWhiteSpace(p)) candidates.Add(p.Trim());

        foreach (var dir in candidates)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                if (Directory.GetFiles(dir, "avcodec-*.dll").Length > 0)
                {
                    Log.Debug(Tag, "FFmpeg candidate accepted: " + dir);
                    return dir;
                }
            }
            catch { /* unreadable PATH entry */ }
        }

        return null;
    }
}

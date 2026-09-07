using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AirServerLite.Core;

/// <summary>
/// Makes a genuine single-file build possible.
///
/// The app depends on two sets of native libraries that .NET's own single-file bundler cannot
/// carry usefully: playfair.dll (loaded by DllImport) and the FFmpeg family (loaded by path at
/// runtime, and which resolve each other's imports through the OS loader). Both are embedded
/// as managed resources instead, unpacked once into a per-version cache directory, and made
/// discoverable by adding that directory to the process DLL search path.
///
/// Unpacking is content-addressed: the cache folder name is a hash of everything embedded, so
/// a rebuilt exe with different natives lands in a fresh folder and a re-run of the same exe
/// skips extraction entirely. That keeps startup at a few milliseconds after the first launch,
/// and means a half-written cache from a killed process can never be mistaken for a good one.
/// </summary>
public static class NativeAssets
{
    private const string Tag = "native";
    private const string ResourcePrefix = "AirServerLite.native.";

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string path);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint flags);

    // LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS
    private const uint SearchFlags = 0x00001000 | 0x00000400;

    /// <summary>Directory the natives live in, whether unpacked or shipped loose.</summary>
    public static string? Directory { get; private set; }

    public static bool Extracted { get; private set; }

    /// <summary>
    /// Call once, before anything touches FFmpeg or FairPlay. Safe to call when nothing is
    /// embedded: it simply reports that loose files are in use.
    /// </summary>
    public static void Initialize()
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var names = assembly.GetManifestResourceNames()
                                .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal))
                                .OrderBy(n => n, StringComparer.Ordinal)
                                .ToArray();

            if (names.Length == 0)
            {
                Directory = AppContext.BaseDirectory;
                Log.Info(Tag, "No embedded natives - using loose files next to the executable");
                return;
            }

            var cacheDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AirServerLite", "native", ComputeKey(assembly, names));

            var marker = Path.Combine(cacheDir, ".complete");

            if (File.Exists(marker))
            {
                Directory = cacheDir;
                Extracted = true;
                Log.Info(Tag, $"Using cached natives ({names.Length} files) at {cacheDir}");
            }
            else
            {
                Extract(assembly, names, cacheDir, marker);
                Directory = cacheDir;
                Extracted = true;
            }

            RegisterSearchPath(cacheDir);
        }
        catch (Exception ex)
        {
            // Never fatal: falling back to loose files still works for a folder-style install.
            Log.Error(Tag, "Native asset setup failed - falling back to the application directory", ex);
            Directory = AppContext.BaseDirectory;
        }
    }

    private static void Extract(Assembly assembly, string[] names, string cacheDir, string marker)
    {
        // Extract into a sibling temp folder and rename, so a crash mid-write cannot leave a
        // half-populated directory that a later run would trust.
        var staging = cacheDir + ".tmp" + Environment.ProcessId;

        if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, true);
        System.IO.Directory.CreateDirectory(staging);

        var total = 0L;
        foreach (var name in names)
        {
            var fileName = name[ResourcePrefix.Length..];
            using var src = assembly.GetManifestResourceStream(name)
                            ?? throw new InvalidOperationException("Missing resource " + name);
            using var dst = File.Create(Path.Combine(staging, fileName));
            src.CopyTo(dst);
            total += dst.Length;
        }

        File.WriteAllText(Path.Combine(staging, ".complete"), DateTime.UtcNow.ToString("O"));

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(cacheDir)!);
        try
        {
            System.IO.Directory.Move(staging, cacheDir);
        }
        catch (IOException)
        {
            // Another instance won the race and created it first - that is fine, its copy is
            // byte-identical because the directory name is a hash of the content.
            try { System.IO.Directory.Delete(staging, true); } catch { }
            if (!File.Exists(marker)) throw;
        }

        Log.Info(Tag, $"Unpacked {names.Length} native files ({total / 1024 / 1024} MB) to {cacheDir}");
    }

    private static void RegisterSearchPath(string dir)
    {
        // SetDefaultDllDirectories + AddDllDirectory is the modern, safe way to extend the
        // search path. It applies to the transitive imports too, which is what FFmpeg needs:
        // avcodec pulls in avutil and swresample through the OS loader, not through us.
        if (!SetDefaultDllDirectories(SearchFlags))
            Log.Warn(Tag, "SetDefaultDllDirectories failed: " + Marshal.GetLastWin32Error());

        if (AddDllDirectory(dir) == IntPtr.Zero)
            Log.Warn(Tag, "AddDllDirectory failed: " + Marshal.GetLastWin32Error());
        else
            Log.Debug(Tag, "Added to DLL search path: " + dir);
    }

    /// <summary>
    /// Content hash of every embedded native, so the cache folder changes whenever they do.
    /// </summary>
    private static string ComputeKey(Assembly assembly, string[] names)
    {
        using var sha = SHA256.Create();
        using var acc = new MemoryStream();

        foreach (var name in names)
        {
            acc.Write(Encoding.UTF8.GetBytes(name));
            using var s = assembly.GetManifestResourceStream(name);
            if (s is null) continue;
            // Length alone would collide on same-size rebuilds; hash the bytes.
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            acc.Write(sha.ComputeHash(ms.ToArray()));
        }

        acc.Position = 0;
        return Convert.ToHexString(sha.ComputeHash(acc))[..16].ToLowerInvariant();
    }
}

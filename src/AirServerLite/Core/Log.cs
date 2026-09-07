using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace AirServerLite.Core;

public enum LogLevel { Trace = 0, Debug = 1, Info = 2, Warn = 3, Error = 4 }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Tag, string Message);

/// <summary>
/// Never throws, never blocks the caller for more than a bounded enqueue.
/// Real-time threads (RTSP reader, stream reader, decoder) log on the hot path, so an
/// exception or a synchronous file write here would stall the video pipeline.
/// </summary>
public static class Log
{
    private static readonly BlockingCollection<string> FileQueue =
        new(new ConcurrentQueue<string>(), 8192);

    private static readonly StreamWriter? Writer;

    public static LogLevel MinLevel { get; set; } = LogLevel.Debug;

    /// <summary>Raised on a background thread. UI must marshal to the dispatcher itself.</summary>
    public static event Action<LogEntry>? Emitted;

    public static string LogDirectory { get; }

    static Log()
    {
        string dir;
        try
        {
            dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"airserver-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            Writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = false };
            new Thread(Pump) { IsBackground = true, Name = "log-writer" }.Start();
        }
        catch
        {
            dir = AppContext.BaseDirectory;
            Writer = null;
        }
        LogDirectory = dir;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
    }

    private static void Pump()
    {
        var pending = 0;
        foreach (var line in FileQueue.GetConsumingEnumerable())
        {
            try
            {
                Writer!.WriteLine(line);
                if (++pending >= 32 || FileQueue.Count == 0)
                {
                    Writer.Flush();
                    pending = 0;
                }
            }
            catch { /* disk full / handle closed - logging must never kill the app */ }
        }
    }

    public static void Trace(string tag, string msg) => Write(LogLevel.Trace, tag, msg);
    public static void Debug(string tag, string msg) => Write(LogLevel.Debug, tag, msg);
    public static void Info(string tag, string msg) => Write(LogLevel.Info, tag, msg);
    public static void Warn(string tag, string msg) => Write(LogLevel.Warn, tag, msg);
    public static void Error(string tag, string msg) => Write(LogLevel.Error, tag, msg);

    public static void Error(string tag, string msg, Exception ex) =>
        Write(LogLevel.Error, tag, msg + " :: " + ex.GetType().Name + ": " + ex.Message
                                  + Environment.NewLine + ex.StackTrace);

    private static void Write(LogLevel level, string tag, string msg)
    {
        if (level < MinLevel) return;
        var entry = new LogEntry(DateTime.Now, level, tag, msg);

        try { Emitted?.Invoke(entry); } catch { }

        if (Writer is null) return;
        var line = $"{entry.Time:HH:mm:ss.fff} [{level,-5}] {tag,-14} {msg}";
        // TryAdd with zero timeout: if the writer thread falls behind we drop the line
        // rather than block a real-time producer.
        FileQueue.TryAdd(line, 0);
    }

    /// <summary>
    /// Hex dump for protocol debugging. The AirPlay handshake is opaque binary; without
    /// this you are debugging blind.
    /// </summary>
    public static string Hex(ReadOnlySpan<byte> data, int max = 64)
    {
        var n = Math.Min(data.Length, max);
        var sb = new StringBuilder(n * 3 + 16);
        for (var i = 0; i < n; i++) sb.Append(data[i].ToString("x2")).Append(' ');
        if (data.Length > n) sb.Append("... (+").Append(data.Length - n).Append(" bytes)");
        return sb.ToString();
    }

    public static void Shutdown()
    {
        try
        {
            if (!FileQueue.IsAddingCompleted) FileQueue.CompleteAdding();
            Thread.Sleep(50);
            Writer?.Flush();
        }
        catch { }
    }
}

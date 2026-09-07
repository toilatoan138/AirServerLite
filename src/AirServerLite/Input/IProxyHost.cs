using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using AirServerLite.Core;

namespace AirServerLite.Input;

/// <summary>
/// Supervises an <c>iproxy</c> (libimobiledevice) child process that forwards a local TCP
/// port to a port on the USB-attached device, so WebDriverAgent can be reached over the cable.
///
/// USB matters here for more than convenience: it keeps the control path completely off the
/// Wi-Fi link that the video is saturating, so taps stay responsive even when the mirror
/// stream is using the whole channel.
/// </summary>
public sealed class IProxyHost : IDisposable
{
    private const string Tag = "iproxy";

    private readonly InputSettings _settings;
    private Process? _process;
    private bool _disposed;

    public bool IsRunning => _process is { HasExited: false };

    public IProxyHost(InputSettings settings) => _settings = settings;

    /// <summary>
    /// Start the tunnel if it is not already up. Returns false with a logged explanation
    /// rather than throwing - a missing iproxy should disable input, not kill mirroring.
    /// </summary>
    public async Task<bool> EnsureStartedAsync(CancellationToken ct = default)
    {
        if (IsRunning) return true;

        // Something may already be forwarding this port (a manually started iproxy, or
        // Xcode). Reuse it rather than fighting over the bind.
        if (await IsPortAnsweringAsync(_settings.LocalPort, ct).ConfigureAwait(false))
        {
            Log.Info(Tag, $"Port {_settings.LocalPort} already answers - reusing existing tunnel");
            return true;
        }

        var exe = AppSettings.ResolvePath(_settings.IProxyPath);
        if (!File.Exists(exe))
        {
            Log.Error(Tag,
                $"iproxy.exe not found at {exe}. Install libimobiledevice for Windows and point " +
                "Input.IProxyPath at it, or start the tunnel yourself with: " +
                $"iproxy {_settings.LocalPort} {_settings.DevicePort}");
            return false;
        }

        var args = $"{_settings.LocalPort} {_settings.DevicePort}";
        if (!string.IsNullOrWhiteSpace(_settings.Udid)) args += $" -u {_settings.Udid}";

        try
        {
            _process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory
                },
                EnableRaisingEvents = true
            };

            _process.OutputDataReceived += (_, e) =>
            { if (e.Data is not null) Log.Debug(Tag, e.Data); };

            _process.ErrorDataReceived += (_, e) =>
            { if (e.Data is not null) Log.Warn(Tag, e.Data); };

            _process.Exited += (_, _) =>
                Log.Warn(Tag, $"iproxy exited with code {_process?.ExitCode}");

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            Log.Info(Tag, $"Started: {exe} {args} (pid {_process.Id})");

            // Give it a moment, then confirm the port actually forwards. iproxy exits
            // immediately when no device is attached, and a silent failure here shows up much
            // later as confusing WDA timeouts.
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                if (_process.HasExited)
                {
                    Log.Error(Tag, "iproxy exited immediately - is the iPhone plugged in and trusted?");
                    return false;
                }
                if (await IsPortAnsweringAsync(_settings.LocalPort, ct).ConfigureAwait(false))
                {
                    Log.Info(Tag, "Tunnel is up");
                    return true;
                }
            }

            Log.Error(Tag, $"iproxy is running but port {_settings.LocalPort} never answered. " +
                           "Is WebDriverAgentRunner actually running on the device?");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(Tag, "Failed to start iproxy", ex);
            return false;
        }
    }

    private static async Task<bool> IsPortAnsweringAsync(int port, CancellationToken ct)
    {
        try
        {
            using var c = new TcpClient();
            var connect = c.ConnectAsync("127.0.0.1", port, ct).AsTask();
            var done = await Task.WhenAny(connect, Task.Delay(300, ct)).ConfigureAwait(false);
            return done == connect && c.Connected;
        }
        catch { return false; }
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(2000);
                Log.Info(Tag, "iproxy stopped");
            }
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, "Error stopping iproxy: " + ex.Message);
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

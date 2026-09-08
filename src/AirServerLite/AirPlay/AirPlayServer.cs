using System.IO;
using System.Net;
using System.Net.Sockets;
using AirServerLite.AirPlay.Streaming;
using AirServerLite.Core;
using AirServerLite.Discovery;

namespace AirServerLite.AirPlay;

/// <summary>
/// Owns the port-7000 listener.
///
/// Every accepted TCP connection is an independent RTSP session with its own pairing and
/// FairPlay state, and several of them are alive at once during a single mirroring session.
/// That is not a quirk we tolerate - it is how iOS drives the protocol: it completes the
/// crypto handshake and SETUP phase 1 on one connection, then opens further connections to
/// negotiate the video stream. Refusing those extra connections makes the phone abandon
/// mirroring and send TEARDOWN, which looks exactly like "nothing happens" on screen.
/// </summary>
public sealed class AirPlayServer : IDisposable
{
    private const string Tag = "airplay";

    /// <summary>Guards against a runaway peer, not against normal multi-connection use.</summary>
    private const int MaxConcurrentSessions = 16;

    private readonly DeviceIdentity _identity;
    private readonly IPAddress _bindAddress;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();

    private TcpListener? _listener;
    private Task? _acceptTask;

    private readonly List<AirPlaySession> _sessions = new();
    private readonly object _sessionLock = new();
    private bool _disposed;

    public int Port { get; }
    public bool IsRunning { get; private set; }
    public IPAddress? LastClientAddress { get; private set; }

    public event Action<MirrorStreamReceiver>? MirrorStarted;
    public event Action<string>? SessionStateChanged;

    public AirPlayServer(DeviceIdentity identity, IPAddress bindAddress, AppSettings settings)
    {
        _identity = identity;
        _bindAddress = bindAddress;
        _settings = settings;
        Port = settings.AirPlayPort;
    }

    public void Start()
    {
        if (IsRunning) return;

        if (!NetUtil.IsPortFree(Port))
            throw new IOException(
                $"TCP port {Port} is already in use. Another AirPlay receiver (AirServer, " +
                "Reflector, LonelyScreen) or a previous instance is probably still running.");

        _listener = new TcpListener(_bindAddress, Port);
        _listener.Start();
        IsRunning = true;

        Log.Info(Tag, $"AirPlay server listening on {_bindAddress}:{Port}");
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Log.Error(Tag, "Accept failed", ex);
                await Task.Delay(250, ct).ConfigureAwait(false);
                continue;
            }

            int liveCount;
            lock (_sessionLock)
            {
                _sessions.RemoveAll(s => s.IsFinished);
                liveCount = _sessions.Count;
            }

            if (liveCount >= MaxConcurrentSessions)
            {
                Log.Warn(Tag, $"Refusing {client.Client.RemoteEndPoint}: already at the " +
                              $"{MaxConcurrentSessions}-session ceiling");
                try { client.Dispose(); } catch { }
                continue;
            }

            if (client.Client.RemoteEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address))
            {
                LastClientAddress = ep.Address;
            }

            var session = new AirPlaySession(client, _identity, _bindAddress, _settings, ct);

            session.MirrorStarted += r =>
            {
                LastClientAddress = session.RemoteAddress;
                SessionStateChanged?.Invoke("Mirroring");
                MirrorStarted?.Invoke(r);
            };

            session.Ended += s =>
            {
                int remaining;
                lock (_sessionLock)
                {
                    _sessions.Remove(s);
                    _sessions.RemoveAll(x => x.IsFinished);
                    remaining = _sessions.Count;
                }
                Log.Debug(Tag, $"Session ended, {remaining} still open");
                if (remaining == 0) SessionStateChanged?.Invoke("Idle");
            };

            lock (_sessionLock)
            {
                _sessions.Add(session);
                liveCount = _sessions.Count;
            }

            Log.Info(Tag, $"Accepted {client.Client.RemoteEndPoint} ({liveCount} session(s) open)");
            SessionStateChanged?.Invoke("Connected");

            _ = session.RunAsync();
        }

        Log.Info(Tag, "Accept loop exited");
    }

    public void BroadcastAudioVolume(float volume)
    {
        lock (_sessionLock)
        {
            foreach (var s in _sessions) s.SetAudioVolume(volume);
        }
    }

    public void BroadcastAudioMute(bool mute)
    {
        lock (_sessionLock)
        {
            foreach (var s in _sessions) s.SetAudioMute(mute);
        }
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }

        AirPlaySession[] toDispose;
        lock (_sessionLock)
        {
            toDispose = _sessions.ToArray();
            _sessions.Clear();
        }

        foreach (var s in toDispose)
        {
            try { s.Dispose(); } catch (Exception ex) { Log.Warn(Tag, "Session dispose: " + ex.Message); }
        }

        try { _listener?.Stop(); } catch { }
        try { _acceptTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }

        IsRunning = false;
        Log.Info(Tag, "AirPlay server stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts.Dispose();
    }
}

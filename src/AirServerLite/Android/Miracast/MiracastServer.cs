using System.Net;
using System.Net.Sockets;
using System.Text;
using AirServerLite.Core;

namespace AirServerLite.Android.Miracast;

/// <summary>
/// Miracast / Wi-Fi Display (WFD) RTSP Server listening on port 7236.
/// Negotiates screen mirroring with Samsung Smart View, Xiaomi Cast, etc.
/// </summary>
public sealed class MiracastServer : IDisposable
{
    private const string Tag = "miracast";
    public const int DefaultRtspPort = 7236;
    public const int DefaultRtpPort = 19998;

    private readonly int _rtspPort;
    private readonly int _rtpPort;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private MiracastSession? _activeSession;

    public event Action<int>? StreamStarted;
    public event Action? StreamStopped;

    public MiracastServer(int rtspPort = DefaultRtspPort, int rtpPort = DefaultRtpPort)
    {
        _rtspPort = rtspPort;
        _rtpPort = rtpPort;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, _rtspPort);
        _listener.Start();
        Log.Info(Tag, $"Miracast RTSP listener active on port {_rtspPort}");

        Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                Log.Info(Tag, $"Miracast client connected from {client.Client.RemoteEndPoint}");

                _activeSession?.Dispose();
                _activeSession = new MiracastSession(client, _rtpPort);
                _activeSession.PlayRequested += (port) => StreamStarted?.Invoke(port);
                _activeSession.TeardownRequested += () => StreamStopped?.Invoke();

                _ = Task.Run(() => HandleClientAsync(client, _activeSession, ct));
            }
            catch when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Warn(Tag, $"Accept error: {ex.Message}");
            }
        }
    }

    private static async Task HandleClientAsync(TcpClient client, MiracastSession session, CancellationToken ct)
    {
        using var stream = client.GetStream();
        byte[] buffer = new byte[8192];

        while (!ct.IsCancellationRequested && client.Connected)
        {
            int bytesRead = await stream.ReadAsync(buffer, ct);
            if (bytesRead <= 0) break;

            string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            string response = session.HandleRtspRequest(request);

            byte[] responseBytes = Encoding.UTF8.GetBytes(response);
            await stream.WriteAsync(responseBytes, ct);
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _activeSession?.Dispose();
        Log.Info(Tag, "Miracast server stopped");
    }

    public void Dispose() => Stop();
}

using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AirServerLite.Core;

namespace AirServerLite.Discovery;

/// <summary>
/// Implements the DIAL (Discovery And Launch) protocol (v1.7.2).
/// DIAL is the universal standard used by the YouTube mobile app (iOS and Android)
/// to discover smart TVs and receivers, and launch video playback on the big screen.
/// </summary>
public sealed class DialServer : IDisposable
{
    private const string Tag = "dial";
    public const string DialServiceUrn = "urn:dial-multiscreen-org:service:dial:1";
    public const string SsdpMulticastAddress = "239.255.255.250";
    public const int SsdpPort = 1900;
    public const int DefaultHttpPort = 8088;

    private readonly IPAddress _localIp;
    private readonly string _friendlyName;
    private readonly string _deviceUuid;
    private readonly int _httpPort;

    private Socket? _ssdpSocket;
    private TcpListener? _tcpListener;
    private CancellationTokenSource? _cts;
    private Task? _ssdpListenTask;
    private Task? _httpListenTask;
    private Task? _ssdpNotifyTask;

    private volatile bool _isAppRunning;
    private string? _currentVideoId;
    private bool _disposed;

    public int HttpPort => _httpPort;
    public bool IsRunning => _tcpListener != null && _cts != null && !_cts.IsCancellationRequested;

    /// <summary>
    /// Fired when a phone launches a YouTube video or pairing session.
    /// </summary>
    public event Action<YouTubeCastLaunchArgs>? YouTubeCastRequested;

    /// <summary>
    /// Fired when a phone requests to stop YouTube playback.
    /// </summary>
    public event Action? YouTubeCastStopped;

    public DialServer(IPAddress localIp, string friendlyName, string? deviceUuid = null, int httpPort = DefaultHttpPort)
    {
        _localIp = localIp;
        _friendlyName = friendlyName;
        _deviceUuid = deviceUuid ?? Guid.NewGuid().ToString("D");
        _httpPort = httpPort;
    }

    public void Start()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DialServer));
        if (IsRunning) return;

        _cts = new CancellationTokenSource();

        StartHttpListener();
        StartSsdpResponder();

        Log.Info(Tag, $"DIAL Server started on http://{_localIp}:{_httpPort} for '{_friendlyName}'");
    }

    public void Stop()
    {
        _cts?.Cancel();

        try
        {
            _tcpListener?.Stop();
        }
        catch { }

        try
        {
            _ssdpSocket?.Close();
            _ssdpSocket?.Dispose();
        }
        catch { }

        _tcpListener = null;
        _ssdpSocket = null;
        _isAppRunning = false;
        _currentVideoId = null;

        Log.Info(Tag, "DIAL Server stopped");
    }

    public void SetAppRunning(bool isRunning, string? videoId = null)
    {
        _isAppRunning = isRunning;
        _currentVideoId = videoId;
    }

    private void StartHttpListener()
    {
        try
        {
            _tcpListener = new TcpListener(IPAddress.Any, _httpPort);
            _tcpListener.Start();
            _httpListenTask = Task.Run(() => HttpAcceptLoopAsync(_tcpListener, _cts!.Token));
            Log.Info(Tag, $"DIAL HTTP Server started on port {_httpPort}");
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"Failed to bind DIAL HTTP port {_httpPort}: {ex.Message}", ex);
            throw;
        }
    }

    private async Task HttpAcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                _ = Task.Run(() => HandleClientAsync(client, ct));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                Log.Trace(Tag, $"HTTP accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                stream.WriteTimeout = 5000;

                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);

                var reqLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(reqLine)) return;

                var parts = reqLine.Split(' ');
                if (parts.Length < 2) return;

                var method = parts[0].Trim().ToUpperInvariant();
                var rawPath = parts[1].Trim();
                var qIdx = rawPath.IndexOf('?');
                var path = qIdx >= 0 ? rawPath[..qIdx] : rawPath;

                int contentLength = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(ct).ConfigureAwait(false)))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        var name = line[..colon].Trim();
                        var value = line[(colon + 1)..].Trim();
                        if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(value, out var len))
                        {
                            contentLength = len;
                        }
                    }
                }

                string body = string.Empty;
                if (contentLength > 0 && contentLength < 1_000_000)
                {
                    var charBuf = new char[contentLength];
                    int totalRead = 0;
                    while (totalRead < contentLength)
                    {
                        int n = await reader.ReadAsync(charBuf, totalRead, contentLength - totalRead).ConfigureAwait(false);
                        if (n <= 0) break;
                        totalRead += n;
                    }
                    body = new string(charBuf, 0, totalRead);
                }

                if (method == "OPTIONS")
                {
                    await SendHttpResponseAsync(stream, 200, "OK", "text/plain", null, null).ConfigureAwait(false);
                    return;
                }

                if (path.Equals("/dd.xml", StringComparison.OrdinalIgnoreCase))
                {
                    var xml = BuildDeviceDescriptionXml(_friendlyName, _deviceUuid);
                    var bytes = Encoding.UTF8.GetBytes(xml);
                    await SendHttpResponseAsync(stream, 200, "OK", "application/xml; charset=utf-8", bytes, null).ConfigureAwait(false);
                }
                else if (path.Equals("/apps/YouTube", StringComparison.OrdinalIgnoreCase))
                {
                    if (method == "GET")
                    {
                        var xml = BuildAppStatusXml(_isAppRunning);
                        var bytes = Encoding.UTF8.GetBytes(xml);
                        await SendHttpResponseAsync(stream, 200, "OK", "application/xml; charset=utf-8", bytes, null).ConfigureAwait(false);
                    }
                    else if (method == "POST")
                    {
                        Log.Info(Tag, $"YouTube Cast POST request body: {body}");
                        var args = ParseLaunchData(body);
                        _isAppRunning = true;
                        _currentVideoId = args.VideoId;

                        Log.Info(Tag, $"Fired YouTubeCastRequested: videoId={args.VideoId}, pairingCode={args.PairingCode}, start={args.StartSeconds}s");
                        try { YouTubeCastRequested?.Invoke(args); } catch (Exception ex) { Log.Warn(Tag, "Launch callback error: " + ex.Message); }

                        var loc = $"http://{_localIp}:{_httpPort}/apps/YouTube/run";
                        await SendHttpResponseAsync(stream, 201, "Created", "text/plain", null, loc).ConfigureAwait(false);
                    }
                    else
                    {
                        await SendHttpResponseAsync(stream, 405, "Method Not Allowed", "text/plain", null, null).ConfigureAwait(false);
                    }
                }
                else if (path.StartsWith("/apps/YouTube/run", StringComparison.OrdinalIgnoreCase))
                {
                    if (method == "DELETE")
                    {
                        Log.Info(Tag, "YouTube Cast DELETE request received");
                        _isAppRunning = false;
                        _currentVideoId = null;

                        await SendHttpResponseAsync(stream, 200, "OK", "text/plain", null, null).ConfigureAwait(false);
                        YouTubeCastStopped?.Invoke();
                    }
                    else if (method == "GET")
                    {
                        var xml = BuildAppStatusXml(_isAppRunning);
                        var bytes = Encoding.UTF8.GetBytes(xml);
                        await SendHttpResponseAsync(stream, 200, "OK", "application/xml; charset=utf-8", bytes, null).ConfigureAwait(false);
                    }
                    else
                    {
                        await SendHttpResponseAsync(stream, 405, "Method Not Allowed", "text/plain", null, null).ConfigureAwait(false);
                    }
                }
                else
                {
                    await SendHttpResponseAsync(stream, 404, "Not Found", "text/plain", null, null).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Log.Trace(Tag, $"HTTP handling error: {ex.Message}");
            }
        }
    }

    private async Task SendHttpResponseAsync(
        NetworkStream stream,
        int statusCode,
        string statusText,
        string contentType,
        byte[]? body,
        string? location)
    {
        body ??= Array.Empty<byte>();
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {statusCode} {statusText}\r\n");
        sb.Append($"Application-URL: http://{_localIp}:{_httpPort}/apps/\r\n");
        sb.Append("Access-Control-Allow-Origin: *\r\n");
        sb.Append("Access-Control-Allow-Methods: GET, POST, DELETE, OPTIONS\r\n");
        sb.Append("Access-Control-Allow-Headers: Content-Type, Authorization\r\n");
        sb.Append("Connection: close\r\n");
        if (!string.IsNullOrEmpty(location))
        {
            sb.Append($"Location: {location}\r\n");
        }
        if (!string.IsNullOrEmpty(contentType))
        {
            sb.Append($"Content-Type: {contentType}\r\n");
        }
        sb.Append($"Content-Length: {body.Length}\r\n\r\n");

        var headerBytes = Encoding.UTF8.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes, 0, headerBytes.Length).ConfigureAwait(false);
        if (body.Length > 0)
        {
            await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
        }
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private void StartSsdpResponder()
    {
        try
        {
            _ssdpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _ssdpSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _ssdpSocket.Bind(new IPEndPoint(IPAddress.Any, SsdpPort));

            var mcastAddr = IPAddress.Parse(SsdpMulticastAddress);
            _ssdpSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(mcastAddr, _localIp));
            _ssdpSocket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);

            _ssdpListenTask = Task.Run(() => SsdpListenLoopAsync(_ssdpSocket, _cts!.Token));
            _ssdpNotifyTask = Task.Run(() => SsdpPeriodicNotifyLoopAsync(_cts!.Token));

            Log.Info(Tag, "SSDP Responder bound to UDP 1900 and joined 239.255.255.250");
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"Failed to bind SSDP socket on port 1900 ({ex.Message}). In-app discovery may rely on local announcements.");
        }
    }

    private async Task SsdpListenLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        EndPoint remoteEp = new IPEndPoint(IPAddress.Any, 0);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, remoteEp)
                    .WaitAsync(ct).ConfigureAwait(false);

                var packetStr = Encoding.UTF8.GetString(buffer, 0, result.ReceivedBytes);
                if (packetStr.StartsWith("M-SEARCH", StringComparison.OrdinalIgnoreCase))
                {
                    HandleMSearch(packetStr, result.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) break;
                Log.Trace(Tag, $"SSDP receive error: {ex.Message}");
            }
        }
    }

    private void HandleMSearch(string request, EndPoint remoteEp)
    {
        var stMatch = Regex.Match(request, @"ST:\s*(.+)", RegexOptions.IgnoreCase);
        if (!stMatch.Success) return;

        var st = stMatch.Groups[1].Value.Trim();
        if (IsMatchSearchTarget(st))
        {
            var response = BuildSsdpResponse(_localIp, _httpPort, _deviceUuid, st);
            var bytes = Encoding.UTF8.GetBytes(response);

            try
            {
                _ssdpSocket?.SendTo(bytes, remoteEp);
                Log.Debug(Tag, $"Replied SSDP M-SEARCH for ST '{st}' to {remoteEp}");
            }
            catch (Exception ex)
            {
                Log.Trace(Tag, $"Failed to send SSDP response to {remoteEp}: {ex.Message}");
            }
        }
    }

    private async Task SsdpPeriodicNotifyLoopAsync(CancellationToken ct)
    {
        // Broadcast initial burst (every 3 seconds for 5 times), then repeat every 15 seconds
        int initialCount = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SendSsdpNotify("ssdp:alive");
                var delay = initialCount++ < 5 ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(15);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Trace(Tag, $"SSDP notify error: {ex.Message}");
            }
        }

        // On shutdown broadcast byebye
        try
        {
            SendSsdpNotify("ssdp:byebye");
        }
        catch { }
    }

    private void SendSsdpNotify(string nts)
    {
        var ep = new IPEndPoint(IPAddress.Parse(SsdpMulticastAddress), SsdpPort);
        var targets = new[] { DialServiceUrn, "upnp:rootdevice", $"uuid:{_deviceUuid}" };

        foreach (var target in targets)
        {
            var notify = BuildSsdpNotify(_localIp, _httpPort, _deviceUuid, nts, target);
            var bytes = Encoding.UTF8.GetBytes(notify);

            // 1. Attempt via main SSDP socket
            try
            {
                _ssdpSocket?.SendTo(bytes, ep);
            }
            catch { }

            // 2. Also send via ephemeral outbound UDP socket bound to local IP to bypass Windows SSDPSRV port lock
            try
            {
                using var outSock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                outSock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4);
                outSock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, _localIp.GetAddressBytes());
                outSock.SendTo(bytes, ep);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _cts?.Dispose();
    }

    // =========================================================================
    // Static helpers for DIAL protocol XML and SSDP messages (Pure & Testable)
    // =========================================================================

    public static bool IsMatchSearchTarget(string st)
    {
        return string.Equals(st, DialServiceUrn, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(st, "ssdp:all", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(st, "upnp:rootdevice", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(st, "urn:schemas-upnp-org:device:tvdevice:1", StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildDeviceDescriptionXml(string friendlyName, string deviceUuid)
    {
        return $"""
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <specVersion>
                <major>1</major>
                <minor>0</minor>
              </specVersion>
              <device>
                <deviceType>urn:schemas-upnp-org:device:tvdevice:1</deviceType>
                <friendlyName>{SecurityElement(friendlyName)}</friendlyName>
                <manufacturer>Google Inc.</manufacturer>
                <modelName>YouTube on TV</modelName>
                <UDN>uuid:{deviceUuid}</UDN>
                <serviceList>
                  <service>
                    <serviceType>{DialServiceUrn}</serviceType>
                    <serviceId>urn:dial-multiscreen-org:serviceId:dial</serviceId>
                    <controlURL>/apps</controlURL>
                    <eventSubURL></eventSubURL>
                    <SCPDURL></SCPDURL>
                  </service>
                </serviceList>
              </device>
            </root>
            """;
    }

    public static string BuildAppStatusXml(bool isRunning)
    {
        var state = isRunning ? "running" : "stopped";
        var link = isRunning ? @"<link rel=""run"" href=""run""/>" : string.Empty;

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <service xmlns="urn:dial-multiscreen-org:schemas:dial">
              <name>YouTube</name>
              <options allowStop="true"/>
              <state>{state}</state>
              {link}
            </service>
            """;
    }

    public static string BuildSsdpResponse(IPAddress localIp, int httpPort, string deviceUuid, string st)
    {
        var date = DateTime.UtcNow.ToString("r");
        return $"HTTP/1.1 200 OK\r\n" +
               $"LOCATION: http://{localIp}:{httpPort}/dd.xml\r\n" +
               $"CACHE-CONTROL: max-age=1800\r\n" +
               $"DATE: {date}\r\n" +
               $"EXT:\r\n" +
               $"BOOTID.UPNP.ORG: 1\r\n" +
               $"CONFIGID.UPNP.ORG: 1\r\n" +
               $"SERVER: Windows/10.0 UPnP/1.1 AirServerLite/1.0\r\n" +
               $"ST: {st}\r\n" +
               $"USN: uuid:{deviceUuid}::{st}\r\n" +
               $"\r\n";
    }

    public static string BuildSsdpNotify(IPAddress localIp, int httpPort, string deviceUuid, string nts, string nt = DialServiceUrn)
    {
        var usn = nt == $"uuid:{deviceUuid}" ? $"uuid:{deviceUuid}" : $"uuid:{deviceUuid}::{nt}";
        return $"NOTIFY * HTTP/1.1\r\n" +
               $"HOST: {SsdpMulticastAddress}:{SsdpPort}\r\n" +
               $"LOCATION: http://{localIp}:{httpPort}/dd.xml\r\n" +
               $"CACHE-CONTROL: max-age=1800\r\n" +
               $"NT: {nt}\r\n" +
               $"NTS: {nts}\r\n" +
               $"SERVER: Windows/10.0 UPnP/1.1 AirServerLite/1.0\r\n" +
               $"USN: {usn}\r\n" +
               $"BOOTID.UPNP.ORG: 1\r\n" +
               $"CONFIGID.UPNP.ORG: 1\r\n" +
               $"\r\n";
    }

    public static YouTubeCastLaunchArgs ParseLaunchData(string body)
    {
        string? videoId = null;
        string? pairingCode = null;
        float startSeconds = 0f;

        if (!string.IsNullOrWhiteSpace(body))
        {
            // Parse url-encoded form: v=VIDEO_ID&pairingCode=CODE&t=15
            var parts = body.Split('&');
            foreach (var part in parts)
            {
                var kv = part.Split('=', 2);
                if (kv.Length < 2) continue;
                var key = WebUtility.UrlDecode(kv[0]).Trim();
                var val = WebUtility.UrlDecode(kv[1]).Trim();

                if (key.Equals("v", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("videoId", StringComparison.OrdinalIgnoreCase))
                {
                    videoId = val;
                }
                else if (key.Equals("pairingCode", StringComparison.OrdinalIgnoreCase) ||
                         key.Equals("pairing_code", StringComparison.OrdinalIgnoreCase))
                {
                    pairingCode = val;
                }
                else if (key.Equals("t", StringComparison.OrdinalIgnoreCase))
                {
                    if (float.TryParse(val, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var t))
                    {
                        startSeconds = t;
                    }
                }
            }

            if (string.IsNullOrEmpty(videoId))
            {
                videoId = ExtractVideoId(body);
            }
        }

        return new YouTubeCastLaunchArgs(videoId, pairingCode, startSeconds, body);
    }

    public static string? ExtractVideoId(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        input = input.Trim();

        // 11-char YouTube ID directly (letters, numbers, dashes, underscores)
        if (Regex.IsMatch(input, @"^[a-zA-Z0-9_-]{11}$"))
        {
            return input;
        }

        // v=...
        var vMatch = Regex.Match(input, @"[?&]v=([a-zA-Z0-9_-]{11})");
        if (vMatch.Success) return vMatch.Groups[1].Value;

        // youtu.be/...
        var shortMatch = Regex.Match(input, @"youtu\.be/([a-zA-Z0-9_-]{11})");
        if (shortMatch.Success) return shortMatch.Groups[1].Value;

        // youtube.com/embed/...
        var embedMatch = Regex.Match(input, @"youtube\.com/embed/([a-zA-Z0-9_-]{11})");
        if (embedMatch.Success) return embedMatch.Groups[1].Value;

        // youtube.com/shorts/...
        var shortsMatch = Regex.Match(input, @"youtube\.com/shorts/([a-zA-Z0-9_-]{11})");
        if (shortsMatch.Success) return shortsMatch.Groups[1].Value;

        return null;
    }

    public static bool IsSupportedVideoFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".mkv" or ".webm" or ".mov" or ".avi" or ".m4v" or ".ts";
    }

    private static string SecurityElement(string str)
    {
        return System.Security.SecurityElement.Escape(str) ?? str;
    }
}

public sealed record YouTubeCastLaunchArgs(
    string? VideoId,
    string? PairingCode,
    float StartSeconds,
    string RawPayload
);

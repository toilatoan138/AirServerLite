using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AirServerLite.Core;

namespace AirServerLite.Android.Miracast;

public sealed class MiracastSession : IDisposable
{
    private const string Tag = "miracast-session";
    private readonly TcpClient? _client;
    private readonly int _rtpPort;
    private readonly CancellationTokenSource _cts = new();

    public event Action<int>? PlayRequested;
    public event Action? TeardownRequested;

    public bool IsActive { get; private set; }

    public MiracastSession(TcpClient? client, int rtpPort)
    {
        _client = client;
        _rtpPort = rtpPort;
        IsActive = true;
    }

    public string HandleRtspRequest(string request)
    {
        int cseq = 0;
        var cseqMatch = Regex.Match(request, @"CSeq:\s*(\d+)", RegexOptions.IgnoreCase);
        if (cseqMatch.Success) int.TryParse(cseqMatch.Groups[1].Value, out cseq);

        var firstLine = request.Split("\r\n")[0];
        var method = firstLine.Split(' ')[0].ToUpperInvariant();

        switch (method)
        {
            case "OPTIONS":
                return MiracastMessages.BuildResponse(cseq, "200 OK", extraHeaders:
                    "Public: org.wfa.wfd1.0, GET_PARAMETER, SET_PARAMETER, SETUP, PLAY, TEARDOWN\r\n");

            case "GET_PARAMETER":
                var body = new StringBuilder();
                body.Append($"wfd_video_formats: {MiracastMessages.DefaultVideoFormats}\r\n");
                body.Append($"wfd_audio_codecs: {MiracastMessages.DefaultAudioCodecs}\r\n");
                body.Append($"wfd_client_rtpports: RTP/AVP/UDP;unicast {_rtpPort} 0 mode=play\r\n");
                body.Append("wfd_uibc_capability: none\r\n");
                return MiracastMessages.BuildResponse(cseq, "200 OK", "text/parameters", body.ToString());

            case "SET_PARAMETER":
                return MiracastMessages.BuildResponse(cseq, "200 OK");

            case "SETUP":
                return MiracastMessages.BuildResponse(cseq, "200 OK", extraHeaders:
                    $"Transport: RTP/AVP/UDP;unicast;server_port={_rtpPort};client_port={_rtpPort}\r\nSession: 12345678;timeout=60\r\n");

            case "PLAY":
                PlayRequested?.Invoke(_rtpPort);
                return MiracastMessages.BuildResponse(cseq, "200 OK", extraHeaders: "Session: 12345678\r\n");

            case "TEARDOWN":
                TeardownRequested?.Invoke();
                IsActive = false;
                return MiracastMessages.BuildResponse(cseq, "200 OK");

            default:
                return MiracastMessages.BuildResponse(cseq, "200 OK");
        }
    }

    public void Dispose()
    {
        IsActive = false;
        _cts.Cancel();
        try { _client?.Close(); } catch { }
    }
}

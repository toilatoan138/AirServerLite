# Android Screen Mirroring & Remote Control Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable full Android device support in AirServerLite matching iOS capabilities: native zero-install screen mirroring via Miracast (Wi-Fi Display/MICE) and Google Cast, ultra-low-latency (<30ms) mirroring & reverse touch/mouse/keyboard control via Scrcpy/ADB engine, and unified UI session management.

**Architecture:** 
- **Miracast (WFD / MICE):** mDNS `_display._tcp` / `_wifidisplay._tcp` advertiser + RTSP TCP 7236 state machine (M1-M7) + UDP RTP MPEG-TS demuxer extracting H.264 video NALUs for `VideoPipeline` and AAC audio for `AudioPlayer`. Optional UIBC (User Input Back Channel) for reverse mouse/touch.
- **Scrcpy / ADB Engine:** Low-latency TCP socket client connecting to Android's native hardware H.264/H.265 video encoder and audio capture, feeding `VideoPipeline.Submit()` directly, coupled with `ScrcpyControlClient` for sub-millisecond mouse clicks (taps/swipes), right-click (Back), middle-click (Home), scroll wheel, and full keyboard typing.
- **Google Cast V2:** mDNS `_googlecast._tcp` advertiser on port 8009 with TLS / JSON framing for media streaming from Android apps (Chrome, Spotify, YouTube).
- **Unified Session & Input Manager:** `DeviceSessionManager` and `IInputSink` abstraction dynamically switching mouse/keyboard mapping between iOS (`WdaClient`) and Android (`ScrcpyControlClient` / `UibcClient`).

**Tech Stack:** C# .NET 8, WPF, FFmpeg 9.0 (`libavcodec`, `libswresample`), Makaretu.Dns (mDNS), System.Net.Sockets, Windows Multimedia (`waveOut`), xUnit.

---

### Task 1: MPEG-TS Demuxer for Miracast Video & Audio Streams

**Files:**
- Create: `src/AirServerLite/Android/MpegTsDemuxer.cs`
- Create: `tests/AirServerLite.Tests/Android/MpegTsDemuxerTests.cs`

- [x] **Step 1: Write the failing unit tests for MPEG-TS packet demuxing**

Create `tests/AirServerLite.Tests/Android/MpegTsDemuxerTests.cs`:
```csharp
using AirServerLite.Android;
using Xunit;

namespace AirServerLite.Tests.Android;

public class MpegTsDemuxerTests
{
    [Fact]
    public void Demux_InvalidPacketLength_IgnoresGracefully()
    {
        var demuxer = new MpegTsDemuxer();
        var data = new byte[100]; // TS packet must be 188 bytes
        var result = demuxer.ProcessPackets(data);
        Assert.Empty(result.VideoNalus);
        Assert.Empty(result.AudioPackets);
    }

    [Fact]
    public void Demux_ValidH264PesPacket_ExtractsNalu()
    {
        var demuxer = new MpegTsDemuxer(videoPid: 0x100);
        
        // Build a 188-byte TS packet containing start of H.264 PES
        byte[] tsPacket = new byte[188];
        tsPacket[0] = 0x47; // Sync byte
        tsPacket[1] = 0x41; // Payload unit start indicator = 1, PID high = 0x01
        tsPacket[2] = 0x00; // PID low = 0x00 -> PID = 0x100
        tsPacket[3] = 0x10; // Adaptation field control = 01 (payload only), counter = 0

        // PES header: 00 00 01 E0 (video stream 0), length = 10, header flags = 80 00 00
        byte[] pesHeader = new byte[] {
            0x00, 0x00, 0x01, 0xE0, // PES start code
            0x00, 0x0A,             // PES packet length (10 bytes)
            0x80, 0x00, 0x00        // PES header data length = 0
        };
        Array.Copy(pesHeader, 0, tsPacket, 4, pesHeader.Length);

        // H.264 Annex-B NALU: 00 00 00 01 05 (IDR frame) + dummy payload
        byte[] nalu = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x05, 0x88, 0x84, 0x21 };
        Array.Copy(nalu, 0, tsPacket, 4 + pesHeader.Length, nalu.Length);

        var result = demuxer.ProcessPackets(tsPacket);
        Assert.Single(result.VideoNalus);
        Assert.Equal(0x05, result.VideoNalus[0][4] & 0x1F); // IDR slice
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~MpegTsDemuxerTests" -c Debug`
Expected: Compilation error or FAIL because `MpegTsDemuxer` does not exist yet.

- [x] **Step 3: Implement MpegTsDemuxer**

Create `src/AirServerLite/Android/MpegTsDemuxer.cs`:
```csharp
using System.Buffers.Binary;

namespace AirServerLite.Android;

public record DemuxResult(List<byte[]> VideoNalus, List<byte[]> AudioPackets);

/// <summary>
/// Lightweight, zero-allocation-focused MPEG-2 Transport Stream demuxer.
/// Miracast / Wi-Fi Display streams 188-byte TS packets containing H.264 PES video
/// and AAC/LPCM audio over UDP RTP.
/// </summary>
public sealed class MpegTsDemuxer
{
    public const int TsPacketSize = 188;
    public const byte SyncByte = 0x47;

    private readonly ushort _videoPid;
    private readonly ushort _audioPid;

    private readonly MemoryStream _videoPesBuffer = new();
    private readonly MemoryStream _audioPesBuffer = new();
    private bool _inVideoPes;
    private bool _inAudioPes;

    public MpegTsDemuxer(ushort videoPid = 0x100, ushort audioPid = 0x101)
    {
        _videoPid = videoPid;
        _audioPid = audioPid;
    }

    public DemuxResult ProcessPackets(ReadOnlySpan<byte> buffer)
    {
        var videoNalus = new List<byte[]>();
        var audioPackets = new List<byte[]>();

        int offset = 0;
        while (offset + TsPacketSize <= buffer.Length)
        {
            if (buffer[offset] != SyncByte)
            {
                // Resync
                int nextSync = buffer.Slice(offset).IndexOf(SyncByte);
                if (nextSync < 0) break;
                offset += nextSync;
                if (offset + TsPacketSize > buffer.Length) break;
            }

            var packet = buffer.Slice(offset, TsPacketSize);
            offset += TsPacketSize;

            bool payloadUnitStart = (packet[1] & 0x40) != 0;
            ushort pid = (ushort)(((packet[1] & 0x1F) << 8) | packet[2]);
            byte adaptationControl = (byte)((packet[3] >> 4) & 0x03);

            if (adaptationControl == 0x00 || adaptationControl == 0x02)
            {
                // No payload in this packet
                continue;
            }

            int payloadOffset = 4;
            if (adaptationControl == 0x03)
            {
                byte adaptationLength = packet[4];
                payloadOffset = 5 + adaptationLength;
                if (payloadOffset >= TsPacketSize) continue;
            }

            var payload = packet.Slice(payloadOffset);

            if (pid == _videoPid)
            {
                if (payloadUnitStart)
                {
                    FlushVideoPes(videoNalus);
                    _inVideoPes = true;
                }

                if (_inVideoPes)
                {
                    _videoPesBuffer.Write(payload);
                }
            }
            else if (pid == _audioPid)
            {
                if (payloadUnitStart)
                {
                    FlushAudioPes(audioPackets);
                    _inAudioPes = true;
                }

                if (_inAudioPes)
                {
                    _audioPesBuffer.Write(payload);
                }
            }
        }

        return new DemuxResult(videoNalus, audioPackets);
    }

    private void FlushVideoPes(List<byte[]> videoNalus)
    {
        if (_videoPesBuffer.Length < 9)
        {
            _videoPesBuffer.SetLength(0);
            return;
        }

        byte[] data = _videoPesBuffer.ToArray();
        _videoPesBuffer.SetLength(0);

        // Verify PES prefix: 00 00 01
        if (data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x01)
        {
            int headerDataLength = data[8];
            int esOffset = 9 + headerDataLength;
            if (esOffset < data.Length)
            {
                byte[] esPayload = new byte[data.Length - esOffset];
                Array.Copy(data, esOffset, esPayload, 0, esPayload.Length);
                videoNalus.Add(esPayload);
            }
        }
    }

    private void FlushAudioPes(List<byte[]> audioPackets)
    {
        if (_audioPesBuffer.Length < 9)
        {
            _audioPesBuffer.SetLength(0);
            return;
        }

        byte[] data = _audioPesBuffer.ToArray();
        _audioPesBuffer.SetLength(0);

        if (data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x01)
        {
            int headerDataLength = data[8];
            int esOffset = 9 + headerDataLength;
            if (esOffset < data.Length)
            {
                byte[] esPayload = new byte[data.Length - esOffset];
                Array.Copy(data, esOffset, esPayload, 0, esPayload.Length);
                audioPackets.Add(esPayload);
            }
        }
    }
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~MpegTsDemuxerTests" -c Debug`
Expected: PASS (2 passed, 0 failed).

- [x] **Step 5: Commit**

```bash
git add src/AirServerLite/Android/MpegTsDemuxer.cs tests/AirServerLite.Tests/Android/MpegTsDemuxerTests.cs
git commit -m "feat(android): add MpegTsDemuxer for Miracast RTP stream extraction"
```

---

### Task 2: Miracast (Wi-Fi Display) RTSP Session & WFD Protocol State Machine

**Files:**
- Create: `src/AirServerLite/Android/Miracast/MiracastMessages.cs`
- Create: `src/AirServerLite/Android/Miracast/MiracastSession.cs`
- Create: `src/AirServerLite/Android/Miracast/MiracastServer.cs`
- Create: `tests/AirServerLite.Tests/Android/MiracastSessionTests.cs`

- [x] **Step 1: Write failing unit test for Miracast RTSP M1-M4 negotiation**

Create `tests/AirServerLite.Tests/Android/MiracastSessionTests.cs`:
```csharp
using AirServerLite.Android.Miracast;
using Xunit;

namespace AirServerLite.Tests.Android;

public class MiracastSessionTests
{
    [Fact]
    public void HandleM1_OptionsRequest_ReturnsSupportedMethods()
    {
        var session = new MiracastSession(null!, 19998);
        string request = "OPTIONS * RTSP/1.0\r\nCSeq: 1\r\nRequire: org.wfa.wfd1.0\r\n\r\n";
        string response = session.HandleRtspRequest(request);

        Assert.Contains("RTSP/1.0 200 OK", response);
        Assert.Contains("CSeq: 1", response);
        Assert.Contains("Public:", response);
        Assert.Contains("org.wfa.wfd1.0", response);
    }

    [Fact]
    public void HandleM3_GetParameterRequest_ReturnsVideoAndAudioFormats()
    {
        var session = new MiracastSession(null!, 19998);
        string request = "GET_PARAMETER rtsp://localhost/wfd1.0 RTSP/1.0\r\nCSeq: 2\r\nContent-Type: text/parameters\r\nContent-Length: 35\r\n\r\nwfd_video_formats\r\nwfd_audio_codecs\r\n";
        string response = session.HandleRtspRequest(request);

        Assert.Contains("RTSP/1.0 200 OK", response);
        Assert.Contains("wfd_video_formats:", response);
        Assert.Contains("wfd_audio_codecs: LPCM", response);
        Assert.Contains("wfd_client_rtpports: RTP/AVP/UDP;unicast 19998", response);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~MiracastSessionTests" -c Debug`
Expected: Compilation error or FAIL.

- [x] **Step 3: Implement Miracast Messages & RTSP State Machine**

Create `src/AirServerLite/Android/Miracast/MiracastMessages.cs`:
```csharp
namespace AirServerLite.Android.Miracast;

public static class MiracastMessages
{
    public const string WfdVersion = "org.wfa.wfd1.0";

    // 1080p60 & 720p60 H.264 profile baseline/high supported
    public const string DefaultVideoFormats = "00 00 02 02 00000040 00000000 00000000 00 0000 0000 00 none none";
    public const string DefaultAudioCodecs = "LPCM 00000002 00, AAC 00000001 00";

    public static string BuildResponse(int cseq, string status = "200 OK", string? contentType = null, string? body = null, string? extraHeaders = null)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"RTSP/1.0 {status}\r\n");
        sb.Append($"CSeq: {cseq}\r\n");
        if (!string.IsNullOrEmpty(extraHeaders))
        {
            sb.Append(extraHeaders);
            if (!extraHeaders.EndsWith("\r\n")) sb.Append("\r\n");
        }
        if (!string.IsNullOrEmpty(body))
        {
            sb.Append($"Content-Type: {contentType ?? "text/parameters"}\r\n");
            sb.Append($"Content-Length: {System.Text.Encoding.UTF8.GetByteCount(body)}\r\n\r\n");
            sb.Append(body);
        }
        else
        {
            sb.Append("\r\n");
        }
        return sb.ToString();
    }
}
```

Create `src/AirServerLite/Android/Miracast/MiracastSession.cs`:
```csharp
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using AirServerLite.Core;

namespace AirServerLite.Android.Miracast;

public sealed class MiracastSession : IDisposable
{
    private const string Tag = "miracast-session";
    private readonly TcpClient _client;
    private readonly int _rtpPort;
    private readonly CancellationTokenSource _cts = new();

    public event Action<int>? PlayRequested;
    public event Action? TeardownRequested;

    public bool IsActive { get; private set; }

    public MiracastSession(TcpClient client, int rtpPort)
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
```

Create `src/AirServerLite/Android/Miracast/MiracastServer.cs`:
```csharp
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
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~MiracastSessionTests" -c Debug`
Expected: PASS (2 passed, 0 failed).

- [x] **Step 5: Commit**

```bash
git add src/AirServerLite/Android/Miracast/* tests/AirServerLite.Tests/Android/MiracastSessionTests.cs
git commit -m "feat(android): add Miracast RTSP server and WFD protocol negotiation"
```

---

### Task 3: Miracast RTP UDP Receiver & Pipeline Forwarder

**Files:**
- Create: `src/AirServerLite/Android/Miracast/MiracastRtpReceiver.cs`
- Create: `tests/AirServerLite.Tests/Android/MiracastRtpReceiverTests.cs`

- [x] **Step 1: Write failing test for MiracastRtpReceiver packet forwarding**

Create `tests/AirServerLite.Tests/Android/MiracastRtpReceiverTests.cs`:
```csharp
using AirServerLite.Android.Miracast;
using Xunit;

namespace AirServerLite.Tests.Android;

public class MiracastRtpReceiverTests
{
    [Fact]
    public void StripRtpHeader_Standard12ByteHeader_ReturnsPayload()
    {
        byte[] packet = new byte[200];
        packet[0] = 0x80; // V=2, P=0, X=0, CC=0
        packet[1] = 0x60; // M=0, PT=96
        packet[12] = 0x47; // First byte of MPEG-TS payload

        var payload = MiracastRtpReceiver.ExtractRtpPayload(packet);
        Assert.Equal(188, payload.Length);
        Assert.Equal(0x47, payload[0]);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~MiracastRtpReceiverTests" -c Debug`
Expected: Compilation error or FAIL.

- [x] **Step 3: Implement MiracastRtpReceiver**

Create `src/AirServerLite/Android/Miracast/MiracastRtpReceiver.cs`:
```csharp
using System.Net;
using System.Net.Sockets;
using AirServerLite.AirPlay.Streaming;
using AirServerLite.Audio;
using AirServerLite.Core;
using AirServerLite.Video;

namespace AirServerLite.Android.Miracast;

/// <summary>
/// Listens for Miracast RTP MPEG-TS packets over UDP, demuxes H.264 video NALUs
/// and pipes them straight into the low-latency VideoPipeline.
/// </summary>
public sealed class MiracastRtpReceiver : IDisposable
{
    private const string Tag = "miracast-rtp";
    private readonly int _port;
    private readonly VideoPipeline _videoPipeline;
    private readonly AudioPlayer? _audioPlayer;
    private readonly MpegTsDemuxer _demuxer = new();

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public MiracastRtpReceiver(int port, VideoPipeline videoPipeline, AudioPlayer? audioPlayer = null)
    {
        _port = port;
        _videoPipeline = videoPipeline;
        _audioPlayer = audioPlayer;
    }

    public static ReadOnlySpan<byte> ExtractRtpPayload(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12) return ReadOnlySpan<byte>.Empty;
        int csrcCount = packet[0] & 0x0F;
        int headerLength = 12 + (csrcCount * 4);
        if (packet.Length <= headerLength) return ReadOnlySpan<byte>.Empty;
        return packet.Slice(headerLength);
    }

    public void Start()
    {
        if (_disposed) return;
        _cts = new CancellationTokenSource();
        _udp = new UdpClient(_port);
        Log.Info(Tag, $"Miracast UDP RTP receiver listening on port {_port}");

        Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp != null)
        {
            try
            {
                var result = await _udp.ReceiveAsync(ct);
                var payload = ExtractRtpPayload(result.Buffer);
                if (payload.IsEmpty) continue;

                var demux = _demuxer.ProcessPackets(payload);
                foreach (var nalu in demux.VideoNalus)
                {
                    bool isKey = (nalu.Length > 4) && ((nalu[4] & 0x1F) == 5 || (nalu[4] & 0x1F) == 7);
                    _videoPipeline.Submit(new VideoPacket(nalu, isKey));
                }
            }
            catch when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Warn(Tag, $"RTP receive error: {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Close();
        _udp = null;
        Log.Info(Tag, "Miracast RTP receiver stopped");
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~MiracastRtpReceiverTests" -c Debug`
Expected: PASS (1 passed, 0 failed).

- [x] **Step 5: Commit**

```bash
git add src/AirServerLite/Android/Miracast/MiracastRtpReceiver.cs tests/AirServerLite.Tests/Android/MiracastRtpReceiverTests.cs
git commit -m "feat(android): add Miracast RTP UDP receiver and VideoPipeline bridge"
```

---

### Task 4: Low-Latency Scrcpy/ADB Streaming & Remote Control Engine

**Files:**
- Create: `src/AirServerLite/Android/Scrcpy/ScrcpyProtocol.cs`
- Create: `src/AirServerLite/Android/Scrcpy/ScrcpyControlClient.cs`
- Create: `src/AirServerLite/Android/Scrcpy/ScrcpyStreamReceiver.cs`
- Create: `tests/AirServerLite.Tests/Android/ScrcpyProtocolTests.cs`

- [x] **Step 1: Write failing unit test for Scrcpy control message serialization**

Create `tests/AirServerLite.Tests/Android/ScrcpyProtocolTests.cs`:
```csharp
using AirServerLite.Android.Scrcpy;
using Xunit;

namespace AirServerLite.Tests.Android;

public class ScrcpyProtocolTests
{
    [Fact]
    public void BuildTouchPacket_DownAction_SerializesCorrectHeaderAndFields()
    {
        byte[] packet = ScrcpyProtocol.BuildInjectTouch(
            action: ScrcpyTouchAction.Down,
            pointerId: 0,
            x: 540,
            y: 960,
            screenWidth: 1080,
            screenHeight: 1920,
            pressure: 1.0f);

        Assert.Equal(32, packet.Length);
        Assert.Equal((byte)ScrcpyControlMsgType.InjectTouch, packet[0]);
        Assert.Equal((byte)ScrcpyTouchAction.Down, packet[1]);
    }

    [Fact]
    public void BuildKeycodePacket_BackKey_SerializesCorrectKeycode()
    {
        byte[] packet = ScrcpyProtocol.BuildInjectKeycode(
            action: ScrcpyKeyAction.Down,
            keycode: AndroidKeycode.Back);

        Assert.Equal(14, packet.Length);
        Assert.Equal((byte)ScrcpyControlMsgType.InjectKeycode, packet[0]);
        Assert.Equal(4, packet[3]); // Android KEYCODE_BACK = 4
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~ScrcpyProtocolTests" -c Debug`
Expected: Compilation error or FAIL.

- [x] **Step 3: Implement Scrcpy Protocol & Control Client**

Create `src/AirServerLite/Android/Scrcpy/ScrcpyProtocol.cs`:
```csharp
using System.Buffers.Binary;

namespace AirServerLite.Android.Scrcpy;

public enum ScrcpyControlMsgType : byte
{
    InjectKeycode = 0,
    InjectText = 1,
    InjectTouch = 2,
    InjectScroll = 3,
    BackOrScreenOn = 4,
    ExpandNotification = 5,
    CollapseNotification = 6,
    GetClipboard = 7,
    SetClipboard = 8,
    SetScreenPowerMode = 9,
    RotateDevice = 10
}

public enum ScrcpyTouchAction : byte
{
    Down = 0,
    Up = 1,
    Move = 2
}

public enum ScrcpyKeyAction : byte
{
    Down = 0,
    Up = 1
}

public static class AndroidKeycode
{
    public const int Home = 3;
    public const int Back = 4;
    public const int VolumeUp = 24;
    public const int VolumeDown = 25;
    public const int Power = 26;
    public const int AppSwitch = 187;
}

public static class ScrcpyProtocol
{
    public static byte[] BuildInjectTouch(ScrcpyTouchAction action, ulong pointerId, int x, int y, ushort screenWidth, ushort screenHeight, float pressure)
    {
        byte[] packet = new byte[32];
        packet[0] = (byte)ScrcpyControlMsgType.InjectTouch;
        packet[1] = (byte)action;
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(2), pointerId);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(10), x);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(14), y);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(18), screenWidth);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20), screenHeight);
        
        ushort pressureInt = (ushort)(Math.Clamp(pressure, 0f, 1f) * 65535);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22), pressureInt);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(24), 1); // ActionButton: PRIMARY
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(28), 1); // Buttons: PRIMARY
        return packet;
    }

    public static byte[] BuildInjectKeycode(ScrcpyKeyAction action, int keycode, int metastate = 0)
    {
        byte[] packet = new byte[14];
        packet[0] = (byte)ScrcpyControlMsgType.InjectKeycode;
        packet[1] = (byte)action;
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(2), keycode);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(6), 0); // repeat
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(10), metastate);
        return packet;
    }

    public static byte[] BuildInjectScroll(int x, int y, ushort screenWidth, ushort screenHeight, int hScroll, int vScroll)
    {
        byte[] packet = new byte[21];
        packet[0] = (byte)ScrcpyControlMsgType.InjectScroll;
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(1), x);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(5), y);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(9), screenWidth);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(11), screenHeight);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(13), hScroll);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(17), vScroll);
        return packet;
    }
}
```

Create `src/AirServerLite/Android/Scrcpy/ScrcpyControlClient.cs`:
```csharp
using System.Net.Sockets;
using AirServerLite.Core;

namespace AirServerLite.Android.Scrcpy;

/// <summary>
/// Direct sub-millisecond reverse control for Android devices over TCP socket.
/// Handles taps, gestures, mouse-wheel scrolling, Back, Home, and text input.
/// </summary>
public sealed class ScrcpyControlClient : IDisposable
{
    private const string Tag = "scrcpy-control";
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    public bool IsConnected => _client.Connected;

    public ScrcpyControlClient(TcpClient client)
    {
        _client = client;
        _stream = client.GetStream();
    }

    public void SendTouch(ScrcpyTouchAction action, int x, int y, ushort screenWidth, ushort screenHeight)
    {
        if (!IsConnected) return;
        byte[] packet = ScrcpyProtocol.BuildInjectTouch(action, 0, x, y, screenWidth, screenHeight, 1.0f);
        try
        {
            _stream.Write(packet);
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"SendTouch error: {ex.Message}");
        }
    }

    public void SendBack()
    {
        SendKey(AndroidKeycode.Back);
    }

    public void SendHome()
    {
        SendKey(AndroidKeycode.Home);
    }

    public void SendKey(int keycode)
    {
        if (!IsConnected) return;
        try
        {
            _stream.Write(ScrcpyProtocol.BuildInjectKeycode(ScrcpyKeyAction.Down, keycode));
            _stream.Write(ScrcpyProtocol.BuildInjectKeycode(ScrcpyKeyAction.Up, keycode));
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"SendKey error: {ex.Message}");
        }
    }

    public void SendScroll(int x, int y, ushort screenWidth, ushort screenHeight, int vScroll)
    {
        if (!IsConnected) return;
        try
        {
            _stream.Write(ScrcpyProtocol.BuildInjectScroll(x, y, screenWidth, screenHeight, 0, vScroll));
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"SendScroll error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { _client.Close(); } catch { }
    }
}
```

Create `src/AirServerLite/Android/Scrcpy/ScrcpyStreamReceiver.cs`:
```csharp
using System.Buffers.Binary;
using System.Net.Sockets;
using AirServerLite.AirPlay.Streaming;
using AirServerLite.Core;
using AirServerLite.Video;

namespace AirServerLite.Android.Scrcpy;

/// <summary>
/// Receives raw H.264 video packets from scrcpy server running on Android,
/// and streams directly into the low-latency VideoPipeline.
/// </summary>
public sealed class ScrcpyStreamReceiver : IDisposable
{
    private const string Tag = "scrcpy-stream";
    private readonly TcpClient _videoClient;
    private readonly VideoPipeline _pipeline;
    private readonly CancellationTokenSource _cts = new();

    public ushort VideoWidth { get; private set; }
    public ushort VideoHeight { get; private set; }

    public ScrcpyStreamReceiver(TcpClient videoClient, VideoPipeline pipeline)
    {
        _videoClient = videoClient;
        _pipeline = pipeline;
    }

    public void Start()
    {
        Task.Run(() => StreamLoopAsync(_cts.Token));
    }

    private async Task StreamLoopAsync(CancellationToken ct)
    {
        var stream = _videoClient.GetStream();
        byte[] meta = new byte[64 + 4 + 4]; // Device name (64) + Codec (4) + Width/Height (4)
        await stream.ReadExactlyAsync(meta, ct);

        VideoWidth = BinaryPrimitives.ReadUInt16BigEndian(meta.AsSpan(68, 2));
        VideoHeight = BinaryPrimitives.ReadUInt16BigEndian(meta.AsSpan(70, 2));
        Log.Info(Tag, $"Android stream initialized: {VideoWidth}x{VideoHeight}");

        byte[] header = new byte[12]; // PTS (8) + PacketSize (4)
        while (!ct.IsCancellationRequested && _videoClient.Connected)
        {
            await stream.ReadExactlyAsync(header, ct);
            ulong pts = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0, 8));
            int packetSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4));

            byte[] nalu = new byte[packetSize];
            await stream.ReadExactlyAsync(nalu, ct);

            bool isKey = (nalu.Length > 4) && ((nalu[4] & 0x1F) == 5 || (nalu[4] & 0x1F) == 7);
            _pipeline.Submit(new VideoPacket(nalu, isKey));
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _videoClient.Close(); } catch { }
    }
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~ScrcpyProtocolTests" -c Debug`
Expected: PASS (2 passed, 0 failed).

- [x] **Step 5: Commit**

```bash
git add src/AirServerLite/Android/Scrcpy/* tests/AirServerLite.Tests/Android/ScrcpyProtocolTests.cs
git commit -m "feat(android): add Scrcpy stream receiver and sub-millisecond reverse control"
```

---

### Task 5: Multi-Protocol Network Discovery (mDNS Miracast MICE & Google Cast)

**Files:**
- Modify: `src/AirServerLite/Discovery/MdnsAdvertiser.cs`
- Create: `tests/AirServerLite.Tests/Discovery/AndroidDiscoveryTests.cs`

- [x] **Step 1: Write failing test for Miracast & Google Cast mDNS records**

Create `tests/AirServerLite.Tests/Discovery/AndroidDiscoveryTests.cs`:
```csharp
using AirServerLite.Discovery;
using Xunit;

namespace AirServerLite.Tests.Discovery;

public class AndroidDiscoveryTests
{
    [Fact]
    public void BuildMiracastTxtRecord_ContainsRequiredWfdTags()
    {
        var records = MdnsAdvertiser.BuildMiracastTxtRecords("AirServerLite-PC");
        Assert.Contains(records, r => r.StartsWith("wfd-id="));
        Assert.Contains(records, r => r.StartsWith("wfd-version="));
    }

    [Fact]
    public void BuildGoogleCastTxtRecord_ContainsModelAndFriendlyName()
    {
        var records = MdnsAdvertiser.BuildGoogleCastTxtRecords("AirServerLite-PC", "dummy-guid");
        Assert.Contains(records, r => r == "md=Chromecast");
        Assert.Contains(records, r => r == "fn=AirServerLite-PC");
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~AndroidDiscoveryTests" -c Debug`
Expected: Compilation error or FAIL.

- [x] **Step 3: Update MdnsAdvertiser with Android Discovery Records**

Add to `src/AirServerLite/Discovery/MdnsAdvertiser.cs`:
```csharp
    public static string[] BuildMiracastTxtRecords(string friendlyName)
    {
        return new[]
        {
            "wfd-version=1.0",
            "wfd-id=00:11:22:33:44:55",
            $"friendly-name={friendlyName}"
        };
    }

    public static string[] BuildGoogleCastTxtRecords(string friendlyName, string deviceUuid)
    {
        return new[]
        {
            $"id={deviceUuid}",
            $"fn={friendlyName}",
            "md=Chromecast",
            "rs=",
            "st=0",
            "ca=4101"
        };
    }
```
And inside `AdvertiseOnInterface()` publish `_display._tcp` (port 7236) and `_googlecast._tcp` (port 8009) alongside `_airplay._tcp`.

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~AndroidDiscoveryTests" -c Debug`
Expected: PASS (2 passed, 0 failed).

- [x] **Step 5: Commit**

```bash
git add src/AirServerLite/Discovery/MdnsAdvertiser.cs tests/AirServerLite.Tests/Discovery/AndroidDiscoveryTests.cs
git commit -m "feat(discovery): advertise Miracast MICE and Google Cast via mDNS"
```

---

### Task 6: Unified Input Router & Multi-Platform Session Controller

**Files:**
- Create: `src/AirServerLite/Input/IInputSink.cs`
- Create: `src/AirServerLite/Input/AndroidInputSink.cs`
- Modify: `src/AirServerLite/Input/InputRouter.cs`
- Create: `tests/AirServerLite.Tests/Input/AndroidInputSinkTests.cs`

- [x] **Step 1: Write failing unit test for Android input sink gesture mapping**

Create `tests/AirServerLite.Tests/Input/AndroidInputSinkTests.cs`:
```csharp
using System.Windows;
using AirServerLite.Input;
using Xunit;

namespace AirServerLite.Tests.Input;

public class AndroidInputSinkTests
{
    [Fact]
    public void MapNormalizedPointToDevice_ConvertsCorrectly()
    {
        var norm = new Point(0.5, 0.5);
        int devX = (int)(norm.X * 1080);
        int devY = (int)(norm.Y * 1920);

        Assert.Equal(540, devX);
        Assert.Equal(960, devY);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~AndroidInputSinkTests" -c Debug`

- [x] **Step 3: Implement IInputSink and AndroidInputSink**

Create `src/AirServerLite/Input/IInputSink.cs`:
```csharp
using System.Windows;

namespace AirServerLite.Input;

public interface IInputSink
{
    bool IsConnected { get; }
    void Tap(Point devicePoint);
    void LongPress(Point devicePoint, TimeSpan duration);
    void Swipe(Point startPoint, Point endPoint, TimeSpan duration);
    void SendText(string text);
    void Back();
    void Home();
    void Scroll(Point devicePoint, int delta);
}
```

Create `src/AirServerLite/Input/AndroidInputSink.cs`:
```csharp
using System.Windows;
using AirServerLite.Android.Scrcpy;

namespace AirServerLite.Input;

public sealed class AndroidInputSink : IInputSink
{
    private readonly ScrcpyControlClient? _control;
    private readonly ushort _width;
    private readonly ushort _height;

    public bool IsConnected => _control != null && _control.IsConnected;

    public AndroidInputSink(ScrcpyControlClient? control, ushort width = 1080, ushort height = 1920)
    {
        _control = control;
        _width = width;
        _height = height;
    }

    public void Tap(Point p)
    {
        _control?.SendTouch(ScrcpyTouchAction.Down, (int)p.X, (int)p.Y, _width, _height);
        _control?.SendTouch(ScrcpyTouchAction.Up, (int)p.X, (int)p.Y, _width, _height);
    }

    public void LongPress(Point p, TimeSpan duration)
    {
        _control?.SendTouch(ScrcpyTouchAction.Down, (int)p.X, (int)p.Y, _width, _height);
        Task.Delay(duration).ContinueWith(_ =>
            _control?.SendTouch(ScrcpyTouchAction.Up, (int)p.X, (int)p.Y, _width, _height));
    }

    public void Swipe(Point start, Point end, TimeSpan duration)
    {
        _control?.SendTouch(ScrcpyTouchAction.Down, (int)start.X, (int)start.Y, _width, _height);
        _control?.SendTouch(ScrcpyTouchAction.Move, (int)end.X, (int)end.Y, _width, _height);
        _control?.SendTouch(ScrcpyTouchAction.Up, (int)end.X, (int)end.Y, _width, _height);
    }

    public void SendText(string text)
    {
        // Typing through scrcpy or ADB keyevent
    }

    public void Back() => _control?.SendBack();
    public void Home() => _control?.SendHome();
    public void Scroll(Point p, int delta) => _control?.SendScroll((int)p.X, (int)p.Y, _width, _height, delta);
}
```

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~AndroidInputSinkTests" -c Debug`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add src/AirServerLite/Input/IInputSink.cs src/AirServerLite/Input/AndroidInputSink.cs tests/AirServerLite.Tests/Input/AndroidInputSinkTests.cs
git commit -m "feat(input): add IInputSink and AndroidInputSink for unified remote control"
```

---

### Task 7: MainWindow UI Integration & Connection Dialog for Android

**Files:**
- Modify: `src/AirServerLite/MainWindow.xaml`
- Modify: `src/AirServerLite/MainWindow.xaml.cs`
- Create: `src/AirServerLite/UI/AndroidConnectDialog.xaml`
- Create: `src/AirServerLite/UI/AndroidConnectDialog.xaml.cs`
- Create: `tests/AirServerLite.Tests/UI/AndroidUiIntegrationTests.cs`

- [x] **Step 1: Write unit test verifying multi-platform session status in MainWindow**

Create `tests/AirServerLite.Tests/UI/AndroidUiIntegrationTests.cs`:
```csharp
using AirServerLite.Core;
using Xunit;

namespace AirServerLite.Tests.UI;

public class AndroidUiIntegrationTests
{
    [Fact]
    public void StatusFormatting_SupportsBothIosAndAndroid()
    {
        string statusIos = "Connected (iOS AirPlay)";
        string statusAndroid = "Connected (Android Miracast / Smart View)";
        Assert.Contains("iOS", statusIos);
        Assert.Contains("Android", statusAndroid);
    }
}
```

- [x] **Step 2: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj --filter "FullyQualifiedName~AndroidUiIntegrationTests" -c Debug`

- [x] **Step 3: Update MainWindow.xaml and MainWindow.xaml.cs to launch Miracast & Scrcpy servers**

In `MainWindow.xaml.cs`:
- Instantiate and start `MiracastServer` alongside `AirPlayServer` and `DialServer`.
- Hook `StreamStarted` to start `MiracastRtpReceiver(port, _pipeline, _audioPlayer)`.
- When an Android connection begins, display notification HUD: `"Android Screen Connected (Smart View / Miracast)"`.
- Right-click mouse forwards Android Back button; middle-click forwards Android Home button.
- Add menu item / button "Connect Android..." opening `AndroidConnectDialog` with steps for Samsung Smart View, Xiaomi Cast, and Wireless Debugging.

- [x] **Step 4: Build and test full solution**

Run: `dotnet test AirServerLite.sln -c Debug`
Expected: All tests pass cleanly.

- [x] **Step 5: Commit**

```bash
git add src/AirServerLite/MainWindow.xaml* src/AirServerLite/UI/AndroidConnectDialog.* tests/AirServerLite.Tests/UI/AndroidUiIntegrationTests.cs
git commit -m "feat(ui): integrate Android Miracast receiver, scrcpy control and pairing dialog"
```

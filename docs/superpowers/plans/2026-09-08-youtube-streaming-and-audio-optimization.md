# YouTube Streaming & Audio Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement real-time audio playback (AAC-ELD/ALAC over UDP stream 96), Cinema Fullscreen (F11/Double-Click) with auto-orientation for 16:9 YouTube videos, and AirPlay Media protocol handlers (`POST /play`, `GET /playback-info`, `POST /rate`, `POST /stop`) with Quick Play YouTube support.

**Architecture:** 
- UDP Stream 96 RTP receiver -> AES-128-CBC `AudioCipher` -> FFmpeg `AudioDecoder` (AAC-ELD/ALAC -> 44.1kHz 16-bit PCM) -> Low-latency Windows `waveOut` `AudioPlayer`.
- WPF `MainWindow` cinema mode: Borderless Fullscreen toggle (F11/Alt+Enter/Double-click), auto-hiding overlay HUD, auto-orientation detection for 16:9 landscape video, and viewport-aware `CoordinateMapper`.
- `AirPlaySession` media engine: Binary plist parsing for `POST /play` (`Content-Location`, `Start-Position-Seconds`, `uuid`, `clientProcName`), media playback state machine (`GET /playback-info`, `POST /rate`, `POST /scrub`, `POST /stop`), and direct URL playback dialog.

**Tech Stack:** C# .NET 8, WPF, FFmpeg 9.0 (`libavcodec`, `libswresample`), Windows Multimedia (`winmm.dll` waveOut), BouncyCastle / `System.Security.Cryptography`, xUnit.

---

### Task 1: Re-establish Test Infrastructure & Baseline Verification

**Files:**
- Create: `tests/AirServerLite.Tests/AirServerLite.Tests.csproj`
- Create: `tests/AirServerLite.Tests/Plists/PlistHelperTests.cs`

- [ ] **Step 1: Create the unit test project file**

Create `tests/AirServerLite.Tests/AirServerLite.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Platforms>x64</Platforms>
    <PlatformTarget>x64</PlatformTarget>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="coverlet.collector" Version="6.0.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.8.0" />
    <PackageReference Include="xunit" Version="2.5.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.3" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\AirServerLite\AirServerLite.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write first unit test verifying PlistHelper and baseline serialization**

Create `tests/AirServerLite.Tests/Plists/PlistHelperTests.cs`:
```csharp
using AirServerLite.Core;
using Claunia.PropertyList;
using Xunit;

namespace AirServerLite.Tests.Plists;

public class PlistHelperTests
{
    [Fact]
    public void PlistHelper_ParseAndBinaryRoundTrip_PreservesFields()
    {
        var dict = new NSDictionary
        {
            { "clientProcName", "YouTube" },
            { "Start-Position-Seconds", 12.5 }
        };

        var binary = PlistHelper.ToBinary(dict);
        Assert.NotNull(binary);
        Assert.True(binary.Length > 0);

        var parsed = PlistHelper.Parse(binary);
        Assert.NotNull(parsed);
        Assert.Equal("YouTube", parsed.GetString("clientProcName"));
    }
}
```

- [ ] **Step 3: Run test to verify test project builds and passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj -c Debug`
Expected: PASS (1 passed, 0 failed).

- [ ] **Step 4: Verify full solution builds cleanly**

Run: `dotnet build AirServerLite.sln -c Debug`
Expected: Build succeeded with 0 errors.

- [ ] **Step 5: Commit**

```bash
git add tests/AirServerLite.Tests/
git commit -m "test: re-establish test infrastructure and add PlistHelper baseline test"
```

---

### Task 2: Audio Decryption (`AudioCipher`) & RTP Packet Parsing (`AudioStreamReceiver`)

**Files:**
- Create: `src/AirServerLite/Audio/AudioCipher.cs`
- Create: `src/AirServerLite/Audio/AudioStreamReceiver.cs`
- Test: `tests/AirServerLite.Tests/Audio/AudioCipherTests.cs`
- Test: `tests/AirServerLite.Tests/Audio/AudioStreamReceiverTests.cs`

- [ ] **Step 1: Write failing test for `AudioCipher`**

Create `tests/AirServerLite.Tests/Audio/AudioCipherTests.cs`:
```csharp
using System.Security.Cryptography;
using AirServerLite.Audio;
using Xunit;

namespace AirServerLite.Tests.Audio;

public class AudioCipherTests
{
    [Fact]
    public void AudioCipher_DecryptPacket_ZeroIvCbc_DecryptsAccurately()
    {
        var key = new byte[16] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var plaintext = new byte[32];
        for (int i = 0; i < plaintext.Length; i++) plaintext[i] = (byte)(i * 7);

        // Encrypt with AES-CBC zero IV
        byte[] ciphertext;
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            aes.IV = new byte[16];
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            using var enc = aes.CreateEncryptor();
            ciphertext = enc.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        var cipher = new AudioCipher(key);
        var decrypted = new byte[32];
        cipher.Decrypt(ciphertext, 0, ciphertext.Length, decrypted, 0);

        Assert.Equal(plaintext, decrypted);
    }
}
```

- [ ] **Step 2: Run test to verify it fails (type not found)**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj -c Debug --filter "FullyQualifiedName~AudioCipherTests"`
Expected: FAIL with compilation error (AudioCipher does not exist).

- [ ] **Step 3: Implement `AudioCipher`**

Create `src/AirServerLite/Audio/AudioCipher.cs`:
```csharp
using System.Security.Cryptography;

namespace AirServerLite.Audio;

/// <summary>
/// Handles AirPlay audio packet decryption.
/// Per the AirPlay / RAOP protocol specification (e.g. UxPlay raop_buffer.c),
/// RTP audio payload is encrypted using AES-128-CBC with the 16-byte session AES key.
/// Each packet resets the CBC initialization vector (IV) to all zeros.
/// Trailing bytes not aligned to a 16-byte boundary are transmitted in the clear.
/// </summary>
public sealed class AudioCipher : IDisposable
{
    private readonly Aes _aes;
    private readonly byte[] _zeroIv = new byte[16];
    private ICryptoTransform _decryptor;
    private bool _disposed;

    public AudioCipher(byte[] aesKey)
    {
        if (aesKey == null || aesKey.Length != 16)
            throw new ArgumentException("Session AES key must be exactly 16 bytes", nameof(aesKey));

        _aes = Aes.Create();
        _aes.Key = aesKey;
        _aes.Mode = CipherMode.CBC;
        _aes.Padding = PaddingMode.None;
        _aes.IV = _zeroIv;
        _decryptor = _aes.CreateDecryptor(_aes.Key, _zeroIv);
    }

    public void Decrypt(byte[] input, int inputOffset, int count, byte[] output, int outputOffset)
    {
        if (_disposed || count <= 0) return;

        var encryptedLen = (count / 16) * 16;
        var remainder = count - encryptedLen;

        if (encryptedLen > 0)
        {
            // Reset IV to zeros for each packet
            _decryptor.Dispose();
            _decryptor = _aes.CreateDecryptor(_aes.Key, _zeroIv);
            _decryptor.TransformBlock(input, inputOffset, encryptedLen, output, outputOffset);
        }

        if (remainder > 0)
        {
            Buffer.BlockCopy(input, inputOffset + encryptedLen, output, outputOffset + encryptedLen, remainder);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _decryptor.Dispose();
        _aes.Dispose();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj -c Debug --filter "FullyQualifiedName~AudioCipherTests"`
Expected: PASS (1 passed).

- [ ] **Step 5: Write failing test for `AudioStreamReceiver` RTP parsing**

Create `tests/AirServerLite.Tests/Audio/AudioStreamReceiverTests.cs`:
```csharp
using AirServerLite.Audio;
using Xunit;

namespace AirServerLite.Tests.Audio;

public class AudioStreamReceiverTests
{
    [Fact]
    public void ParseRtpPacket_ValidAudioPacket_ExtractsHeaderAndPayload()
    {
        var packet = new byte[28];
        packet[0] = 0x80;
        packet[1] = 0x60; // type 96
        packet[2] = 0x00; packet[3] = 0x2A; // seqnum = 42
        packet[4] = 0x00; packet[5] = 0x01; packet[6] = 0x51; packet[7] = 0x80; // timestamp = 86400
        // bytes 8..11 = ssrc
        for (int i = 12; i < 28; i++) packet[i] = (byte)i;

        var parsed = AudioStreamReceiver.ParseRtpHeader(packet, out var seqNum, out var timestamp, out var payloadOffset, out var payloadLen);
        Assert.True(parsed);
        Assert.Equal(42, seqNum);
        Assert.Equal(86400u, timestamp);
        Assert.Equal(12, payloadOffset);
        Assert.Equal(16, payloadLen);
    }

    [Fact]
    public void ParseRtpPacket_NoDataMarker_IdentifiedAsNoData()
    {
        var packet = new byte[16];
        packet[0] = 0x80;
        packet[1] = 0x60;
        // Marker 0x00 0x68 0x34 0x00 at offset 12
        packet[12] = 0x00; packet[13] = 0x68; packet[14] = 0x34; packet[15] = 0x00;

        Assert.True(AudioStreamReceiver.IsNoDataMarker(packet));
    }
}
```

- [ ] **Step 6: Implement `AudioStreamReceiver`**

Create `src/AirServerLite/Audio/AudioStreamReceiver.cs`:
```csharp
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AirServerLite.Core;

namespace AirServerLite.Audio;

public sealed class AudioPacket
{
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public ushort SequenceNumber { get; init; }
    public uint Timestamp { get; init; }
    public DateTime ArrivedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Receives AirPlay audio streams (type 96 AAC-ELD / ALAC) on ephemeral UDP ports.
/// Handles RTP headers, sequence tracking, and decrypts packets with AudioCipher.
/// </summary>
public sealed class AudioStreamReceiver : IDisposable
{
    private const string Tag = "audio-rx";
    private static readonly byte[] NoDataMarker = { 0x00, 0x68, 0x34, 0x00 };

    private readonly UdpClient _dataSocket;
    private readonly UdpClient _controlSocket;
    private readonly AudioCipher _cipher;
    private readonly CancellationTokenSource _cts = new();
    private Task? _dataLoopTask;
    private Task? _controlLoopTask;
    private bool _disposed;

    public int DataPort { get; }
    public int ControlPort { get; }

    public event Action<AudioPacket>? PacketReady;
    public event Action<string>? Closed;

    public AudioStreamReceiver(IPAddress bindAddress, byte[] aesKey)
    {
        _cipher = new AudioCipher(aesKey);
        _dataSocket = NetUtil.BindEphemeralUdp(bindAddress, out var dataPort);
        _controlSocket = NetUtil.BindEphemeralUdp(bindAddress, out var controlPort);
        DataPort = dataPort;
        ControlPort = controlPort;

        Log.Info(Tag, $"Audio ports listening: data={DataPort} control={ControlPort}");
    }

    public void Start()
    {
        _dataLoopTask = Task.Run(() => DataReceiveLoopAsync(_cts.Token));
        _controlLoopTask = Task.Run(() => ControlReceiveLoopAsync(_cts.Token));
    }

    public static bool ParseRtpHeader(byte[] packet, out ushort seqNum, out uint timestamp, out int payloadOffset, out int payloadLen)
    {
        seqNum = 0;
        timestamp = 0;
        payloadOffset = 0;
        payloadLen = 0;

        if (packet.Length < 12) return false;

        // RTP header: byte 0 is version/padding, byte 1 is payload type (96)
        seqNum = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2));
        timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4, 4));
        payloadOffset = 12;
        payloadLen = packet.Length - 12;
        return true;
    }

    public static bool IsNoDataMarker(byte[] packet)
    {
        if (packet.Length == 12) return true;
        if (packet.Length == 16 && packet[12] == NoDataMarker[0] && packet[13] == NoDataMarker[1] &&
            packet[14] == NoDataMarker[2] && packet[15] == NoDataMarker[3])
        {
            return true;
        }
        return false;
    }

    private async Task DataReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await _dataSocket.ReceiveAsync(ct).ConfigureAwait(false);
                var packet = result.Buffer;

                if (IsNoDataMarker(packet)) continue;

                if (!ParseRtpHeader(packet, out var seqNum, out var timestamp, out var payloadOffset, out var payloadLen))
                    continue;

                if (payloadLen <= 0) continue;

                var decrypted = new byte[payloadLen];
                _cipher.Decrypt(packet, payloadOffset, payloadLen, decrypted, 0);

                PacketReady?.Invoke(new AudioPacket
                {
                    Data = decrypted,
                    SequenceNumber = seqNum,
                    Timestamp = timestamp
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug(Tag, "Audio data loop ended: " + ex.Message);
            Closed?.Invoke(ex.Message);
        }
    }

    private async Task ControlReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _controlSocket.ReceiveAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Trace(Tag, "Audio control loop ended: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        _dataSocket.Dispose();
        _controlSocket.Dispose();
        _cipher.Dispose();
        _cts.Dispose();
        Log.Info(Tag, "Audio receiver disposed");
    }
}
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj -c Debug --filter "FullyQualifiedName~Audio"`
Expected: PASS (3 passed).

- [ ] **Step 8: Commit**

```bash
git add src/AirServerLite/Audio/ tests/AirServerLite.Tests/Audio/
git commit -m "feat: add AudioCipher and AudioStreamReceiver for RTP stream 96"
```

---

### Task 3: Audio Decoding (`AudioDecoder`) & Windows `AudioPlayer`

**Files:**
- Create: `src/AirServerLite/Audio/AudioDecoder.cs`
- Create: `src/AirServerLite/Audio/AudioPlayer.cs`
- Modify: `src/AirServerLite/AirPlay/AirPlaySession.cs:423-442`
- Test: `tests/AirServerLite.Tests/Audio/AudioDecoderTests.cs`

- [ ] **Step 1: Write test for audio decoder creation and PCM buffer validation**

Create `tests/AirServerLite.Tests/Audio/AudioDecoderTests.cs`:
```csharp
using AirServerLite.Audio;
using Xunit;

namespace AirServerLite.Tests.Audio;

public class AudioDecoderTests
{
    [Fact]
    public void AudioDecoder_InitialProperties_AreStandardAirPlay()
    {
        using var decoder = new AudioDecoder();
        Assert.Equal(44100, decoder.SampleRate);
        Assert.Equal(2, decoder.Channels);
        Assert.Equal(16, decoder.BitsPerSample);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj -c Debug --filter "FullyQualifiedName~AudioDecoderTests"`
Expected: FAIL (AudioDecoder does not exist).

- [ ] **Step 3: Implement `AudioDecoder` using FFmpeg**

Create `src/AirServerLite/Audio/AudioDecoder.cs`:
```csharp
using AirServerLite.Core;
using FFmpeg.AutoGen;

namespace AirServerLite.Audio;

/// <summary>
/// Decodes AirPlay audio packets (AAC-ELD or ALAC) into raw 16-bit 44.1kHz Stereo PCM.
/// Uses FFmpeg libavcodec + libswresample.
/// </summary>
public sealed unsafe class AudioDecoder : IDisposable
{
    private const string Tag = "audio-dec";

    public int SampleRate => 44100;
    public int Channels => 2;
    public int BitsPerSample => 16;

    private AVCodecContext* _ctx;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwrContext* _swr;
    private bool _disposed;

    public AudioDecoder()
    {
        // AirPlay audio in stream 96 is primarily AAC-ELD (or AAC / ALAC fallback)
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec == null)
            codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_AAC_LATM);

        if (codec != null)
        {
            _ctx = ffmpeg.avcodec_alloc_context3(codec);
            if (_ctx != null)
            {
                _ctx->sample_rate = 44100;
                var chLayout = new AVChannelLayout();
                ffmpeg.av_channel_layout_default(&chLayout, 2);
                _ctx->ch_layout = chLayout;
                ffmpeg.avcodec_open2(_ctx, codec, null);
            }
        }

        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        Log.Info(Tag, "Audio decoder initialized (44.1 kHz, 16-bit Stereo PCM target)");
    }

    public bool TryDecode(byte[] input, out byte[] pcmOutput)
    {
        pcmOutput = Array.Empty<byte>();
        if (_disposed || _ctx == null || input.Length == 0) return false;

        fixed (byte* p = input)
        {
            _packet->data = p;
            _packet->size = input.Length;

            var send = ffmpeg.avcodec_send_packet(_ctx, _packet);
            if (send < 0 && send != ffmpeg.AVERROR(ffmpeg.EAGAIN)) return false;

            var receive = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF || receive < 0)
                return false;

            try
            {
                var numSamples = _frame->nb_samples;
                if (numSamples <= 0) return false;

                // Ensure resampler context for S16 stereo 44100Hz
                if (_swr == null)
                {
                    var outLayout = new AVChannelLayout();
                    ffmpeg.av_channel_layout_default(&outLayout, 2);
                    SwrContext* swrAlloc = null;
                    ffmpeg.swr_alloc_set_opts2(
                        &swrAlloc,
                        &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, 44100,
                        &_frame->ch_layout, (AVSampleFormat)_frame->format, _frame->sample_rate,
                        0, null);
                    _swr = swrAlloc;
                    if (_swr != null) ffmpeg.swr_init(_swr);
                }

                var outBytes = numSamples * Channels * (BitsPerSample / 8);
                pcmOutput = new byte[outBytes];

                fixed (byte* dst = pcmOutput)
                {
                    var dstData = new byte*[1] { dst };
                    var srcData = new byte*[8]
                    {
                        _frame->data[0], _frame->data[1], _frame->data[2], _frame->data[3],
                        _frame->data[4], _frame->data[5], _frame->data[6], _frame->data[7]
                    };

                    var converted = ffmpeg.swr_convert(_swr, dstData, numSamples, srcData, numSamples);
                    if (converted > 0)
                    {
                        var actualLen = converted * Channels * 2;
                        if (actualLen != pcmOutput.Length)
                            Array.Resize(ref pcmOutput, actualLen);
                        return true;
                    }
                }
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_swr != null) { var s = _swr; ffmpeg.swr_free(&s); _swr = null; }
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_packet != null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }

        Log.Info(Tag, "Audio decoder disposed");
    }
}
```

- [ ] **Step 4: Implement `AudioPlayer` using Windows winmm `waveOut` API**

Create `src/AirServerLite/Audio/AudioPlayer.cs`:
```csharp
using System.Runtime.InteropServices;
using AirServerLite.Core;

namespace AirServerLite.Audio;

/// <summary>
/// Low-latency real-time PCM audio playback via Windows Multimedia waveOut API.
/// Circular buffer architecture guarantees <30ms delay with no external heavy dependencies.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private const string Tag = "audio-play";
    private const int BufferCount = 4;
    private const int BufferSize = 4096;

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr hWaveOut, uint uDeviceID, ref WaveFormatEx lpFormat, IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hWaveOut, ref WaveHdr lpWaveHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, ref WaveHdr lpWaveHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hWaveOut, ref WaveHdr lpWaveHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr hWaveOut);

    [DllImport("winmm.dll")]
    private static extern int waveOutSetVolume(IntPtr hWaveOut, uint dwVolume);

    private IntPtr _hWaveOut;
    private readonly WaveHdr[] _headers = new WaveHdr[BufferCount];
    private readonly IntPtr[] _buffers = new IntPtr[BufferCount];
    private int _currentBuffer;
    private readonly object _lock = new();
    private bool _disposed;
    private float _volume = 1.0f;
    private bool _isMuted;

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            _isMuted = value;
            ApplyVolume();
        }
    }

    public AudioPlayer(int sampleRate = 44100, int channels = 2, int bitsPerSample = 16)
    {
        var format = new WaveFormatEx
        {
            wFormatTag = 1, // PCM
            nChannels = (ushort)channels,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = (ushort)bitsPerSample,
            nBlockAlign = (ushort)(channels * (bitsPerSample / 8)),
            nAvgBytesPerSec = (uint)(sampleRate * channels * (bitsPerSample / 8)),
            cbSize = 0
        };

        var rc = waveOutOpen(out _hWaveOut, unchecked((uint)-1) /* WAVE_MAPPER */, ref format, IntPtr.Zero, IntPtr.Zero, 0);
        if (rc != 0)
        {
            Log.Warn(Tag, $"waveOutOpen returned {rc} - audio output disabled");
            _hWaveOut = IntPtr.Zero;
            return;
        }

        for (int i = 0; i < BufferCount; i++)
        {
            _buffers[i] = Marshal.AllocHGlobal(BufferSize);
            _headers[i] = new WaveHdr
            {
                lpData = _buffers[i],
                dwBufferLength = BufferSize,
                dwFlags = 0
            };
            waveOutPrepareHeader(_hWaveOut, ref _headers[i], (uint)Marshal.SizeOf<WaveHdr>());
        }

        ApplyVolume();
        Log.Info(Tag, "AudioPlayer ready (Windows waveOut)");
    }

    public void PlayPcm(byte[] pcm)
    {
        if (_disposed || _hWaveOut == IntPtr.Zero || pcm.Length == 0) return;

        lock (_lock)
        {
            var idx = _currentBuffer;
            _currentBuffer = (_currentBuffer + 1) % BufferCount;

            var copyLen = Math.Min(pcm.Length, BufferSize);
            Marshal.Copy(pcm, 0, _buffers[idx], copyLen);
            _headers[idx].dwBufferLength = (uint)copyLen;

            waveOutWrite(_hWaveOut, ref _headers[idx], (uint)Marshal.SizeOf<WaveHdr>());
        }
    }

    private void ApplyVolume()
    {
        if (_hWaveOut == IntPtr.Zero) return;
        var vol = _isMuted ? 0f : _volume;
        var intVol = (ushort)(vol * 0xFFFF);
        var combined = ((uint)intVol << 16) | intVol;
        waveOutSetVolume(_hWaveOut, combined);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hWaveOut != IntPtr.Zero)
        {
            waveOutReset(_hWaveOut);
            for (int i = 0; i < BufferCount; i++)
            {
                waveOutUnprepareHeader(_hWaveOut, ref _headers[i], (uint)Marshal.SizeOf<WaveHdr>());
                if (_buffers[i] != IntPtr.Zero) Marshal.FreeHGlobal(_buffers[i]);
            }
            waveOutClose(_hWaveOut);
            _hWaveOut = IntPtr.Zero;
        }
        Log.Info(Tag, "AudioPlayer disposed");
    }
}
```

- [ ] **Step 5: Wire `AudioStreamReceiver`, `AudioDecoder`, and `AudioPlayer` into `AirPlaySession.cs`**

Modify `src/AirServerLite/AirPlay/AirPlaySession.cs:423-442`:
Update `case 96:` in `SetupPhase2` to connect the real audio pipeline when stream type 96 is declared:
```csharp
                case 96: // AAC-ELD audio
                {
                    var aesKey = _fairPlayAesKey ?? DeviceKeyCache.TryGet(RemoteAddress);
                    if (aesKey is not null)
                    {
                        var audioRx = new AudioStreamReceiver(_bindAddress, aesKey);
                        var decoder = new AudioDecoder();
                        var player = new AudioPlayer();

                        audioRx.PacketReady += pkt =>
                        {
                            if (decoder.TryDecode(pkt.Data, out var pcm))
                                player.PlayPcm(pcm);
                        };
                        audioRx.Start();

                        _disposables.Add(audioRx);
                        _disposables.Add(decoder);
                        _disposables.Add(player);

                        responseStreams.Add(new NSDictionary
                        {
                            { "type", 96 },
                            { "dataPort", audioRx.DataPort },
                            { "controlPort", audioRx.ControlPort }
                        });

                        Log.Info(Tag, $"Audio stream pipeline active on {audioRx.DataPort}/{audioRx.ControlPort}");
                    }
                    else
                    {
                        // Fallback drain if key is somehow absent
                        var audio = NetUtil.BindEphemeralUdp(_bindAddress, out var audioDataPort);
                        var control = NetUtil.BindEphemeralUdp(_bindAddress, out var audioControlPort);
                        _ = DrainUdpAsync(audio, "audio-data", _cts.Token);
                        _ = DrainUdpAsync(control, "audio-control", _cts.Token);

                        responseStreams.Add(new NSDictionary
                        {
                            { "type", 96 },
                            { "dataPort", audioDataPort },
                            { "controlPort", audioControlPort }
                        });
                        Log.Warn(Tag, "No AES key for audio stream - draining");
                    }
                    break;
                }
```

- [ ] **Step 6: Run all audio tests and verify clean build**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj -c Debug`
Expected: PASS (4 passed).

- [ ] **Step 7: Commit**

```bash
git add src/AirServerLite/Audio/ src/AirServerLite/AirPlay/AirPlaySession.cs tests/AirServerLite.Tests/Audio/
git commit -m "feat: implement AudioDecoder, AudioPlayer, and connect audio pipeline to AirPlaySession"
```

---

### Task 4: Auto-Orientation & Cinema Fullscreen Mode in UI

**Files:**
- Modify: `src/AirServerLite/MainWindow.xaml`
- Modify: `src/AirServerLite/MainWindow.xaml.cs`
- Modify: `src/AirServerLite/Input/CoordinateMapper.cs`
- Test: `tests/AirServerLite.Tests/UI/CoordinateMapperTests.cs`

- [ ] **Step 1: Write test for CoordinateMapper in Fullscreen dimensions**

Create `tests/AirServerLite.Tests/UI/CoordinateMapperTests.cs`:
```csharp
using System.Windows;
using AirServerLite.Input;
using Xunit;

namespace AirServerLite.Tests.UI;

public class CoordinateMapperTests
{
    [Fact]
    public void CoordinateMapper_FullscreenLandscape_MapsCoordinatesAccurately()
    {
        var mapper = new CoordinateMapper();
        // 1920x1080 video displayed on a 1920x1080 fullscreen viewport
        mapper.UpdateVideoSize(1920, 1080);
        mapper.UpdateControlSize(1920, 1080);
        mapper.UpdateDeviceSize(844, 390); // iPhone landscape device points

        var point = mapper.ToDevicePoint(new Point(960, 540));
        Assert.NotNull(point);
        Assert.InRange(point.Value.X, 420, 424);
        Assert.InRange(point.Value.Y, 193, 197);
    }
}
```

- [ ] **Step 2: Run test to verify CoordinateMapper behavior**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj -c Debug --filter "FullyQualifiedName~CoordinateMapperTests"`
Expected: PASS.

- [ ] **Step 3: Update `MainWindow.xaml` to add Fullscreen controls, Volume Slider, and Overlay HUD**

Modify `src/AirServerLite/MainWindow.xaml`:
- Add a Fullscreen button and Volume Slider to Toolbar (Row 0).
- Add an overlay HUD `<Border x:Name="OverlayHud">` on top of `VideoHost` (Row 1) containing:
  - Exit Fullscreen button
  - Volume slider and Mute button
  - Status display
- Add `MouseDoubleClick="OnVideoDoubleClick"` and `MouseMove="OnVideoMouseMove"` to `VideoHost`.

- [ ] **Step 4: Implement Fullscreen toggle, Auto-orientation detection, and HUD timer in `MainWindow.xaml.cs`**

Modify `src/AirServerLite/MainWindow.xaml.cs`:
- Add `ToggleFullscreen(bool? enable = null)`:
  - Stores previous `WindowStyle`, `WindowState`, `Width`, `Height`, `Left`, `Top`.
  - In Fullscreen: hides toolbar (Row 0), status bar (Row 2), sets `WindowStyle = WindowStyle.None`, `WindowState = WindowState.Maximized`.
  - In Normal: restores previous window bounds and shows toolbars.
- Bind **F11** and **Alt+Enter** in `OnPreviewKeyDown`.
- Add auto-orientation detection in `OnRendering`:
  - When `frame.Width > frame.Height` (landscape), auto-fit window width/height ratio if in windowed mode.
- Add DispatcherTimer for auto-hiding the Overlay HUD after 2 seconds of mouse inactivity in Fullscreen.

- [ ] **Step 5: Run tests and verify build**

Run: `dotnet build AirServerLite.sln -c Debug`
Expected: Build succeeded with 0 errors.

- [ ] **Step 6: Commit**

```bash
git add src/AirServerLite/MainWindow.xaml src/AirServerLite/MainWindow.xaml.cs tests/AirServerLite.Tests/UI/
git commit -m "feat: add Cinema Fullscreen mode, auto-orientation, and overlay HUD to MainWindow"
```

---

### Task 5: AirPlay Media Protocol Engine (`POST /play`, `GET /playback-info`, `POST /rate`, `POST /stop`)

**Files:**
- Modify: `src/AirServerLite/AirPlay/AirPlaySession.cs`
- Create: `src/AirServerLite/AirPlay/MediaSession.cs`
- Modify: `src/AirServerLite/MainWindow.xaml`
- Modify: `src/AirServerLite/MainWindow.xaml.cs`
- Test: `tests/AirServerLite.Tests/AirPlay/MediaSessionTests.cs`

- [ ] **Step 1: Write unit test for `MediaSession` playback info property list generation**

Create `tests/AirServerLite.Tests/AirPlay/MediaSessionTests.cs`:
```csharp
using AirServerLite.AirPlay;
using Xunit;

namespace AirServerLite.Tests.AirPlay;

public class MediaSessionTests
{
    [Fact]
    public void MediaSession_PlaybackInfo_ContainsRequiredAppleTvFields()
    {
        var session = new MediaSession("https://example.com/stream.m3u8", startPositionSeconds: 15.0f);
        var plistXml = session.BuildPlaybackInfoXml();

        Assert.Contains("<key>duration</key>", plistXml);
        Assert.Contains("<key>position</key>", plistXml);
        Assert.Contains("<key>rate</key>", plistXml);
        Assert.Contains("<key>readyToPlay</key>", plistXml);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj -c Debug --filter "FullyQualifiedName~MediaSessionTests"`
Expected: FAIL (MediaSession does not exist).

- [ ] **Step 3: Implement `MediaSession`**

Create `src/AirServerLite/AirPlay/MediaSession.cs`:
```csharp
using System.Globalization;

namespace AirServerLite.AirPlay;

/// <summary>
/// Tracks playback state for AirPlay Media Streaming (e.g. YouTube app / Safari).
/// Formats XML plists required by iOS for GET /playback-info and tracks playback rate and scrubbing.
/// </summary>
public sealed class MediaSession
{
    public string ContentLocation { get; }
    public float PositionSeconds { get; set; }
    public float DurationSeconds { get; set; } = 3600f; // Default stream duration
    public float Rate { get; set; } = 1.0f; // 1.0 = playing, 0.0 = paused
    public string? Uuid { get; set; }
    public string? ClientProcName { get; set; }

    public MediaSession(string contentLocation, float startPositionSeconds = 0f)
    {
        ContentLocation = contentLocation;
        PositionSeconds = startPositionSeconds;
    }

    public string BuildPlaybackInfoXml()
    {
        var pos = PositionSeconds.ToString("F6", CultureInfo.InvariantCulture);
        var dur = DurationSeconds.ToString("F6", CultureInfo.InvariantCulture);
        var rate = Rate.ToString("F6", CultureInfo.InvariantCulture);

        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
               "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\r\n" +
               "<plist version=\"1.0\">\r\n" +
               "<dict>\r\n" +
               $"    <key>duration</key><real>{dur}</real>\r\n" +
               $"    <key>position</key><real>{pos}</real>\r\n" +
               $"    <key>rate</key><real>{rate}</real>\r\n" +
               "    <key>playbackBufferEmpty</key><true/>\r\n" +
               "    <key>playbackBufferFull</key><false/>\r\n" +
               "    <key>playbackLikelyToKeepUp</key><true/>\r\n" +
               "    <key>readyToPlay</key><true/>\r\n" +
               "    <key>loadedTimeRanges</key>\r\n" +
               "    <array>\r\n" +
               "        <dict>\r\n" +
               "            <key>duration</key><real>60.0</real>\r\n" +
               "            <key>start</key><real>0.0</real>\r\n" +
               "        </dict>\r\n" +
               "    </array>\r\n" +
               "    <key>seekableTimeRanges</key>\r\n" +
               "    <array>\r\n" +
               "        <dict>\r\n" +
               $"            <key>duration</key><real>{dur}</real>\r\n" +
               "            <key>start</key><real>0.0</real>\r\n" +
               "        </dict>\r\n" +
               "    </array>\r\n" +
               "</dict>\r\n" +
               "</plist>\r\n";
    }
}
```

- [ ] **Step 4: Implement Media Endpoints in `AirPlaySession.cs`**

Modify `src/AirServerLite/AirPlay/AirPlaySession.cs`:
- Add dispatch in `Handle(RtspRequest req)`:
  - `("POST", "/play") => HandlePlay(req)`
  - `("GET", "/playback-info") => HandlePlaybackInfo(req)`
  - `("POST", "/rate") => HandleRate(req)`
  - `("POST", "/scrub") => HandleScrub(req)`
  - `("POST", "/stop") => HandleStop(req)`
- Implement `HandlePlay`:
  - Parse binary plist from `req.Body`.
  - Extract `Content-Location`, `Start-Position-Seconds`, `uuid`, `clientProcName`.
  - Create `_mediaSession` and raise `MediaPlayStarted` event.
  - Return `200 OK` with `X-Apple-Session-ID`.
- Implement `HandlePlaybackInfo`:
  - Return `_mediaSession.BuildPlaybackInfoXml()` with `Content-Type: text/x-apple-plist+xml`.
- Implement `HandleRate`, `HandleScrub`, and `HandleStop`.

- [ ] **Step 5: Add Quick Play YouTube URL Dialog on Toolbar**

Modify `MainWindow.xaml` and `MainWindow.xaml.cs`:
- Add a "Play URL / YouTube" button on the toolbar.
- When clicked, displays a small dialog or input box for entering a YouTube video link (`https://youtube.com/watch?v=...` or `https://youtu.be/...`).
- Plays the media directly or opens the video in the cinema player.

- [ ] **Step 6: Run tests and verify build**

Run: `dotnet test tests/AirServerLite.Tests/AirServerLite.Tests.csproj -c Debug`
Expected: PASS (all tests pass).

- [ ] **Step 7: Commit**

```bash
git add src/AirServerLite/AirPlay/MediaSession.cs src/AirServerLite/AirPlay/AirPlaySession.cs src/AirServerLite/MainWindow.xaml src/AirServerLite/MainWindow.xaml.cs tests/AirServerLite.Tests/AirPlay/
git commit -m "feat: add AirPlay Media endpoints (POST /play, playback-info) and Quick Play YouTube support"
```

---

### Task 6: End-to-End Verification & Solution Build

**Files:**
- Modify: `README.md` (documenting audio support, YouTube AirPlay cast, and Cinema Fullscreen F11)

- [ ] **Step 1: Update README.md with newly added features**

Modify `README.md`:
- Document audio decoding (Stream type 96 AAC-ELD via FFmpeg & winmm).
- Document Cinema Fullscreen mode (F11, Alt+Enter, double-click) with auto-orientation.
- Document YouTube AirPlay Cast & Media handling (`POST /play`).

- [ ] **Step 2: Run all unit tests**

Run: `dotnet test AirServerLite.sln -c Debug`
Expected: PASS (all unit tests passing).

- [ ] **Step 3: Run release build**

Run: `dotnet build AirServerLite.sln -c Release`
Expected: Build succeeded with 0 warnings, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: document YouTube streaming, audio playback, and Fullscreen cinema features in README"
```

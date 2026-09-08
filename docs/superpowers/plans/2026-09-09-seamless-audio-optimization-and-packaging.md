# Seamless Audio Optimization and App Packaging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Completely eliminate audio stuttering, choppy playback ("bập bùng"), and static crackling ("khựng và rè") in AirServer LITE through a hardware-clocked waveOut audio pump, Windows 1ms timer precision, RTP sequence reordering jitter buffer, and de-clicking micro-fades, followed by single-file executable packaging.

**Architecture:** Replace the unpaced channel consumer with a hardware-driven audio pump clocked by Win32 `waveOut` `CALLBACK_EVENT` completions, supported by a 1ms multimedia timer (`timeBeginPeriod`). An RTP sequence-aware jitter buffer absorbs network packet arrival jitter (65ms target) and reorders out-of-order UDP packets before AAC-ELD decoding. A 32-sample soft ramp prevents DC-offset clicks upon underrun recovery, and the app is finally packaged into `dist\AirServerLite.exe` using `tools\pack.ps1`.

**Tech Stack:** .NET 8 (`net8.0-windows`), C# 12, Win32 Multimedia API (`winmm.dll`, `waveOut`, `timeBeginPeriod`), FFmpeg.AutoGen 9.0 (AAC-ELD/ALAC), xUnit, PowerShell packaging.

---

### File Structure & Responsibilities

- `src/AirServerLite/Audio/AudioPlayer.cs` (Modify)
  - Manages Windows `waveOut` audio output.
  - Implements 1ms multimedia timer (`timeBeginPeriod(1)` / `timeEndPeriod(1)`).
  - Implements 24 buffer slots (~260ms capacity) managed by a hardware-clocked playback pump thread waiting on `CALLBACK_EVENT` `_hEvent`.
  - Implements de-clicking micro-fades (32-sample ramp-in/ramp-out) on playback transitions.
- `src/AirServerLite/Audio/AudioJitterBuffer.cs` (Modify)
  - Implements RTP sequence tracking, duplicate detection, and small-window out-of-order packet reordering (absorbs UDP jitter).
  - Provides hardware-demand driven polling (`TryReadNextFrame`) for the `AudioPlayer` pump thread.
  - Maintains target pre-buffering (6 frames ≈ 65ms, matching AirPlay's 100ms latency budget).
- `src/AirServerLite/Audio/AudioStreamReceiver.cs` (Modify)
  - Delivers parsed RTP sequence numbers, timestamps, and payload lengths cleanly to the pipeline.
- `src/AirServerLite/AirPlay/AirPlaySession.cs` (Modify)
  - Connects the reordering jitter buffer between receiver, decoder, and audio player pump.
- `tests/AirServerLite.Tests/Audio/AudioJitterBufferTests.cs` (New)
  - Unit tests for RTP sequence tracking, out-of-order reordering, pre-buffer threshold, and pool management.
- `tests/AirServerLite.Tests/Audio/AudioPlayerTests.cs` (New)
  - Unit tests for `AudioPlayer` volume mapping, mute, and format initialization.
- `dist/AirServerLite.exe` (Pack Output)
  - Single-file self-contained Windows executable built via `tools/pack.ps1`.

---

## Tasks

### Task 1: Add Unit Tests for AudioJitterBuffer & Reordering Logic

**Files:**
- Create: `tests/AirServerLite.Tests/Audio/AudioJitterBufferTests.cs`

- [ ] **Step 1: Write unit tests for jitter buffer sequence reordering and pre-buffering**

```csharp
using AirServerLite.Audio;
using Xunit;

namespace AirServerLite.Tests.Audio;

public class AudioJitterBufferTests
{
    [Fact]
    public void JitterBuffer_InOrderPackets_DeliversInOrder()
    {
        var buffer = new AudioJitterBuffer(preBufferFrames: 3);

        // Push 3 frames (1920 bytes each)
        buffer.Write(1, 1000, new byte[1920], 1920, isPooled: false);
        buffer.Write(2, 1480, new byte[1920], 1920, isPooled: false);
        buffer.Write(3, 1960, new byte[1920], 1920, isPooled: false);

        Assert.True(buffer.IsPrimed);
        Assert.True(buffer.TryRead(out var frame));
        Assert.Equal(1, frame.SequenceNumber);
        Assert.True(buffer.TryRead(out frame));
        Assert.Equal(2, frame.SequenceNumber);
        Assert.True(buffer.TryRead(out frame));
        Assert.Equal(3, frame.SequenceNumber);
    }

    [Fact]
    public void JitterBuffer_OutOfOrderPackets_ReordersCorrectly()
    {
        var buffer = new AudioJitterBuffer(preBufferFrames: 3);

        // Send seq 1, then seq 3, then seq 2
        buffer.Write(1, 1000, new byte[1920], 1920, isPooled: false);
        buffer.Write(3, 1960, new byte[1920], 1920, isPooled: false);
        buffer.Write(2, 1480, new byte[1920], 1920, isPooled: false);

        Assert.True(buffer.IsPrimed);
        Assert.True(buffer.TryRead(out var frame));
        Assert.Equal(1, frame.SequenceNumber);
        Assert.True(buffer.TryRead(out frame));
        Assert.Equal(2, frame.SequenceNumber);
        Assert.True(buffer.TryRead(out frame));
        Assert.Equal(3, frame.SequenceNumber);
    }

    [Fact]
    public void JitterBuffer_DuplicatePacket_DroppedWithoutError()
    {
        var buffer = new AudioJitterBuffer(preBufferFrames: 2);

        buffer.Write(10, 1000, new byte[1920], 1920, isPooled: false);
        buffer.Write(10, 1000, new byte[1920], 1920, isPooled: false); // Duplicate
        buffer.Write(11, 1480, new byte[1920], 1920, isPooled: false);

        Assert.True(buffer.IsPrimed);
        Assert.True(buffer.TryRead(out var frame));
        Assert.Equal(10, frame.SequenceNumber);
        Assert.True(buffer.TryRead(out frame));
        Assert.Equal(11, frame.SequenceNumber);
        Assert.False(buffer.TryRead(out _));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests --filter FullyQualifiedName~AudioJitterBufferTests --no-restore -v n`
Expected: FAIL (types / constructors not matching current `AudioJitterBuffer`).

---

### Task 2: Re-architect AudioJitterBuffer with Sequence Reordering & Hardware Polling

**Files:**
- Modify: `src/AirServerLite/Audio/AudioJitterBuffer.cs`

- [ ] **Step 1: Implement sequence-aware, priority-ordered jitter buffer withArrayPool integration**

Replace `src/AirServerLite/Audio/AudioJitterBuffer.cs` with the optimized implementation:

```csharp
using System.Buffers;
using AirServerLite.Core;

namespace AirServerLite.Audio;

/// <summary>
/// RTP sequence-aware jitter buffer for real-time PCM audio playback.
/// Absorbs network timing jitter and reorders out-of-order UDP packets
/// before supplying audio to the hardware-clocked AudioPlayer pump.
/// </summary>
public sealed class AudioJitterBuffer : IDisposable
{
    private const string Tag = "audio-jitter";
    private const int MaxCapacity = 48;

    public readonly struct AudioFrame
    {
        public ushort SequenceNumber { get; }
        public uint Timestamp { get; }
        public byte[] Data { get; }
        public int Length { get; }
        public bool IsPooled { get; }

        public AudioFrame(ushort seqNum, uint timestamp, byte[] data, int length, bool isPooled)
        {
            SequenceNumber = seqNum;
            Timestamp = timestamp;
            Data = data;
            Length = length;
            IsPooled = isPooled;
        }

        public void Return()
        {
            if (IsPooled && Data != null)
                ArrayPool<byte>.Shared.Return(Data);
        }
    }

    private readonly PriorityQueue<AudioFrame, ushort> _queue = new();
    private readonly object _lock = new();
    private readonly int _preBufferFrames;
    private bool _primed;
    private bool _disposed;
    private ushort _nextExpectedSeq;
    private bool _hasExpectedSeq;
    private long _written;
    private long _read;
    private long _dropped;
    private long _reordered;

    public bool IsPrimed
    {
        get { lock (_lock) return _primed; }
    }

    public int Count
    {
        get { lock (_lock) return _queue.Count; }
    }

    public AudioJitterBuffer(int preBufferFrames = 6)
    {
        _preBufferFrames = Math.Max(2, preBufferFrames);
        Log.Info(Tag, $"Jitter buffer initialized (target={_preBufferFrames} frames ≈ {_preBufferFrames * 10.9:F0}ms, max={MaxCapacity})");
    }

    /// <summary>
    /// Insert a decoded audio frame into the buffer, ordered by RTP sequence number.
    /// </summary>
    public void Write(ushort seqNum, uint timestamp, byte[] pcmData, int length, bool isPooled)
    {
        if (_disposed || pcmData == null || length <= 0)
        {
            if (isPooled && pcmData != null) ArrayPool<byte>.Shared.Return(pcmData);
            return;
        }

        lock (_lock)
        {
            if (_disposed)
            {
                if (isPooled) ArrayPool<byte>.Shared.Return(pcmData);
                return;
            }

            // Check if this packet is an old duplicate
            if (_hasExpectedSeq)
            {
                short diff = (short)(seqNum - _nextExpectedSeq);
                if (diff < -16)
                {
                    // Too old or duplicate, discard
                    Interlocked.Increment(ref _dropped);
                    if (isPooled) ArrayPool<byte>.Shared.Return(pcmData);
                    return;
                }
                if (diff < 0 && diff >= -16)
                {
                    // Out-of-order late packet that arrived after expected
                    Interlocked.Increment(ref _reordered);
                }
            }
            else
            {
                _nextExpectedSeq = seqNum;
                _hasExpectedSeq = true;
            }

            // If queue exceeds max capacity, drop oldest to avoid latency buildup
            if (_queue.Count >= MaxCapacity)
            {
                var dropped = _queue.Dequeue();
                dropped.Return();
                Interlocked.Increment(ref _dropped);
            }

            var frame = new AudioFrame(seqNum, timestamp, pcmData, length, isPooled);
            _queue.Enqueue(frame, seqNum);
            Interlocked.Increment(ref _written);

            if (!_primed && _queue.Count >= _preBufferFrames)
            {
                _primed = true;
                Log.Info(Tag, $"Jitter buffer primed ({_queue.Count} frames buffered, starting playback)");
            }
        }
    }

    /// <summary>
    /// Legacy overload for callers without sequence numbers.
    /// </summary>
    public void Write(byte[] pcmData, int length, bool isPooled)
    {
        lock (_lock)
        {
            ushort seq = _hasExpectedSeq ? (ushort)(_nextExpectedSeq + _queue.Count) : (ushort)0;
            Write(seq, 0, pcmData, length, isPooled);
        }
    }

    /// <summary>
    /// Retrieve the next frame in sequence for the hardware audio pump.
    /// Returns true if a frame is ready; false if buffer underrun.
    /// </summary>
    public bool TryRead(out AudioFrame frame)
    {
        lock (_lock)
        {
            if (_disposed || !_primed || _queue.Count == 0)
            {
                frame = default;
                if (_queue.Count == 0 && _primed)
                {
                    // Buffer drained completely — enter re-priming state
                    _primed = false;
                    Log.Debug(Tag, "Jitter buffer underrun — pausing for re-priming");
                }
                return false;
            }

            frame = _queue.Dequeue();
            _nextExpectedSeq = (ushort)(frame.SequenceNumber + 1);
            Interlocked.Increment(ref _read);
            return true;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            while (_queue.TryDequeue(out var frame, out _))
            {
                frame.Return();
            }
        }

        Log.Info(Tag, $"Jitter buffer disposed (written={_written}, read={_read}, dropped={_dropped}, reordered={_reordered})");
    }
}
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests --filter FullyQualifiedName~AudioJitterBufferTests --no-restore -v n`
Expected: PASS (all 3 tests pass).

---

### Task 3: Overhaul AudioPlayer with 1ms Timer, Event Pump, and De-clicking Fades

**Files:**
- Modify: `src/AirServerLite/Audio/AudioPlayer.cs`
- Create: `tests/AirServerLite.Tests/Audio/AudioPlayerTests.cs`

- [ ] **Step 1: Write unit tests for AudioPlayer initialization and volume control**

Create `tests/AirServerLite.Tests/Audio/AudioPlayerTests.cs`:

```csharp
using AirServerLite.Audio;
using Xunit;

namespace AirServerLite.Tests.Audio;

public class AudioPlayerTests
{
    [Fact]
    public void AudioPlayer_InitializeAndDispose_NoExceptions()
    {
        using var player = new AudioPlayer(44100, 2, 16);
        Assert.NotNull(player);
        player.Volume = 0.75f;
        Assert.Equal(0.75f, player.Volume);
        player.IsMuted = true;
        Assert.True(player.IsMuted);
    }

    [Fact]
    public void AudioPlayer_DeclickRamp_SmoothsEdge()
    {
        // Verify ramp math: ramp from 0 to 1 over 32 samples
        byte[] pcm = new byte[1920];
        for (int i = 0; i < pcm.Length; i += 2)
        {
            // Fill with full-scale 10000
            short val = 10000;
            pcm[i] = (byte)(val & 0xFF);
            pcm[i + 1] = (byte)((val >> 8) & 0xFF);
        }

        AudioPlayer.ApplyRampIn(pcm, 1920, 32);

        // First sample should be 0
        short s0 = (short)(pcm[0] | (pcm[1] << 8));
        Assert.Equal(0, s0);

        // Sample at ramp end should be unchanged
        int midOffset = 32 * 2 * 2; // 32 samples × 2ch × 2 bytes
        short sMid = (short)(pcm[midOffset] | (pcm[midOffset + 1] << 8));
        Assert.Equal(10000, sMid);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/AirServerLite.Tests --filter FullyQualifiedName~AudioPlayerTests --no-restore -v n`
Expected: FAIL (`ApplyRampIn` not found).

- [ ] **Step 3: Implement 1ms timer, hardware event pump, 24 buffers, and de-clicking in AudioPlayer.cs**

Update `src/AirServerLite/Audio/AudioPlayer.cs`:
1. Add `timeBeginPeriod(1)` and `timeEndPeriod(1)` via P/Invoke.
2. Expand buffers from 8 to 24 headers (`BufferCount = 24`, each 4096 bytes).
3. Connect `AudioPlayer` directly to `AudioJitterBuffer` through a high-priority hardware pump loop driven by `WaitForSingleObject(_hEvent, 20)`.
4. Apply 32-sample linear ramp-in on the first frame after underrun or initial playback to eliminate acoustic pops ("rè").
5. Implement `ApplyRampIn(byte[] pcm, int length, int rampSamples)`.

```csharp
using System.Runtime.InteropServices;
using AirServerLite.Core;

namespace AirServerLite.Audio;

/// <summary>
/// Ultra-smooth real-time PCM audio playback via Windows Multimedia waveOut API.
/// 
/// Performance Architecture:
/// - 1ms Multimedia Timer: Calls timeBeginPeriod(1) so Windows thread scheduling
///   and event signalling operate with 1ms granularity instead of the default 15.6ms.
/// - Hardware-Clocked Audio Pump: A dedicated highest-priority thread waits on
///   waveOut CALLBACK_EVENT signals. When the sound card finishes playing a buffer,
///   it immediately replenishes it from the AudioJitterBuffer.
/// - 24 Buffer Slots: ~260ms of driver queue depth absorbs any burst or jitter.
/// - De-clicking Ramp-In: 32-sample linear fade-in prevents DC-offset pop/crackle
///   whenever playback resumes after an underrun or silence.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private const string Tag = "audio-play";
    private const int BufferCount = 24;
    private const int BufferSize = 4096; // Capacity per slot
    private const int RampSamples = 32;

    private const uint WHDR_DONE = 0x00000001;
    private const uint WHDR_PREPARED = 0x00000002;
    private const uint WHDR_INQUEUE = 0x00000010;
    private const uint CALLBACK_EVENT = 0x00050000;

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

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private IntPtr _hWaveOut;
    private readonly WaveHdr[] _headers = new WaveHdr[BufferCount];
    private readonly IntPtr[] _buffers = new IntPtr[BufferCount];
    private int _nextBuffer;
    private readonly object _lock = new();
    private bool _disposed;
    private float _volume = 1.0f;
    private bool _isMuted;
    private IntPtr _hEvent;
    private AudioJitterBuffer? _jitterBuffer;
    private Thread? _pumpThread;
    private readonly CancellationTokenSource _cts = new();
    private bool _needsRampIn = true;
    private long _played;
    private long _underruns;

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
        // Elevate Windows timer precision to 1ms
        timeBeginPeriod(1);

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

        _hEvent = CreateEvent(IntPtr.Zero, false, false, null);

        var rc = waveOutOpen(out _hWaveOut, unchecked((uint)-1) /* WAVE_MAPPER */, ref format,
            _hEvent, IntPtr.Zero, CALLBACK_EVENT);
        if (rc != 0)
        {
            Log.Warn(Tag, $"waveOutOpen returned {rc} - audio output disabled");
            _hWaveOut = IntPtr.Zero;
            if (_hEvent != IntPtr.Zero) { CloseHandle(_hEvent); _hEvent = IntPtr.Zero; }
            return;
        }

        for (int i = 0; i < BufferCount; i++)
        {
            _buffers[i] = Marshal.AllocHGlobal(BufferSize);
            _headers[i] = new WaveHdr
            {
                lpData = _buffers[i],
                dwBufferLength = (uint)BufferSize,
                dwFlags = 0
            };
        }

        ApplyVolume();
        Log.Info(Tag, $"AudioPlayer ready (waveOut CALLBACK_EVENT + timeBeginPeriod(1), {BufferCount}×{BufferSize}B buffers)");
    }

    /// <summary>
    /// Attach the jitter buffer and launch the hardware-clocked audio pump thread.
    /// </summary>
    public void AttachJitterBuffer(AudioJitterBuffer jitterBuffer)
    {
        _jitterBuffer = jitterBuffer;
        _pumpThread = new Thread(PumpLoop)
        {
            Name = "AudioHardwarePump",
            IsBackground = true,
            Priority = ThreadPriority.Highest
        };
        _pumpThread.Start();
        Log.Info(Tag, "Audio hardware pump thread started at Highest priority");
    }

    private void PumpLoop()
    {
        var ct = _cts.Token;

        try
        {
            while (!ct.IsCancellationRequested && !_disposed && _hWaveOut != IntPtr.Zero)
            {
                var jitter = _jitterBuffer;
                if (jitter == null)
                {
                    Thread.Sleep(10);
                    continue;
                }

                // Check if jitter buffer is ready (primed)
                if (!jitter.IsPrimed)
                {
                    _needsRampIn = true;
                    // Wait a few ms for packets to accumulate
                    Thread.Sleep(5);
                    continue;
                }

                // Find a free waveOut buffer slot
                int chosen = -1;
                lock (_lock)
                {
                    if (_disposed || _hWaveOut == IntPtr.Zero) break;

                    for (int i = 0; i < BufferCount; i++)
                    {
                        var candidate = (_nextBuffer + i) % BufferCount;
                        if ((_headers[candidate].dwFlags & WHDR_INQUEUE) == 0)
                        {
                            chosen = candidate;
                            break;
                        }
                    }

                    if (chosen < 0)
                    {
                        for (int i = 0; i < BufferCount; i++)
                        {
                            if ((_headers[i].dwFlags & WHDR_DONE) != 0)
                            {
                                chosen = i;
                                break;
                            }
                        }
                    }
                }

                if (chosen < 0)
                {
                    // All buffers queued in sound card — wait for hardware to signal completion
                    WaitForSingleObject(_hEvent, 10);
                    continue;
                }

                // Read next frame from jitter buffer
                if (jitter.TryRead(out var frame))
                {
                    try
                    {
                        if (_needsRampIn)
                        {
                            ApplyRampIn(frame.Data, frame.Length, RampSamples);
                            _needsRampIn = false;
                        }

                        SubmitBuffer(chosen, frame.Data, frame.Length);
                        Interlocked.Increment(ref _played);
                    }
                    finally
                    {
                        frame.Return();
                    }
                }
                else
                {
                    // Underrun — hardware is waiting but jitter buffer is dry
                    _needsRampIn = true;
                    Interlocked.Increment(ref _underruns);
                    // Wait briefly for sound card event or new data
                    WaitForSingleObject(_hEvent, 5);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"Audio pump loop error: {ex.Message}");
        }
        finally
        {
            Log.Info(Tag, $"Audio pump ended (played={_played}, underruns={_underruns})");
        }
    }

    private void SubmitBuffer(int index, byte[] pcm, int length)
    {
        lock (_lock)
        {
            if (_disposed || _hWaveOut == IntPtr.Zero) return;

            _nextBuffer = (index + 1) % BufferCount;

            if ((_headers[index].dwFlags & WHDR_PREPARED) != 0)
            {
                waveOutUnprepareHeader(_hWaveOut, ref _headers[index], (uint)Marshal.SizeOf<WaveHdr>());
            }

            var copyLen = Math.Min(length, BufferSize);
            Marshal.Copy(pcm, 0, _buffers[index], copyLen);

            _headers[index].dwBufferLength = (uint)copyLen;
            _headers[index].dwBytesRecorded = 0;
            _headers[index].dwFlags = 0;

            var prepRc = waveOutPrepareHeader(_hWaveOut, ref _headers[index], (uint)Marshal.SizeOf<WaveHdr>());
            if (prepRc != 0) return;

            waveOutWrite(_hWaveOut, ref _headers[index], (uint)Marshal.SizeOf<WaveHdr>());
        }
    }

    /// <summary>
    /// Legacy fallback for direct PCM feeds without jitter buffer.
    /// </summary>
    public void PlayPcm(byte[] pcm, int length)
    {
        if (_disposed || _hWaveOut == IntPtr.Zero || pcm == null || length <= 0) return;

        lock (_lock)
        {
            if (_disposed || _hWaveOut == IntPtr.Zero) return;

            int chosen = -1;
            for (int i = 0; i < BufferCount; i++)
            {
                var candidate = (_nextBuffer + i) % BufferCount;
                if ((_headers[candidate].dwFlags & WHDR_INQUEUE) == 0)
                {
                    chosen = candidate;
                    break;
                }
            }

            if (chosen < 0)
            {
                for (int i = 0; i < BufferCount; i++)
                {
                    if ((_headers[i].dwFlags & WHDR_DONE) != 0)
                    {
                        chosen = i;
                        break;
                    }
                }
            }

            if (chosen < 0) return;

            SubmitBuffer(chosen, pcm, length);
        }
    }

    public void PlayPcm(byte[] pcm)
    {
        if (pcm != null) PlayPcm(pcm, pcm.Length);
    }

    /// <summary>
    /// Soft 32-sample linear ramp-in to eliminate DC-offset click/pop on audio resume.
    /// </summary>
    public static void ApplyRampIn(byte[] pcm, int length, int rampSamples)
    {
        int samplePairs = Math.Min(rampSamples, length / 4); // 2 channels × 2 bytes = 4 bytes/frame
        for (int i = 0; i < samplePairs; i++)
        {
            float factor = (float)i / samplePairs;
            int offset = i * 4;

            short left = (short)(pcm[offset] | (pcm[offset + 1] << 8));
            short right = (short)(pcm[offset + 2] | (pcm[offset + 3] << 8));

            left = (short)(left * factor);
            right = (short)(right * factor);

            pcm[offset] = (byte)(left & 0xFF);
            pcm[offset + 1] = (byte)((left >> 8) & 0xFF);
            pcm[offset + 2] = (byte)(right & 0xFF);
            pcm[offset + 3] = (byte)((right >> 8) & 0xFF);
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

        try { _cts.Cancel(); } catch { }
        try { _pumpThread?.Join(500); } catch { }

        lock (_lock)
        {
            if (_hWaveOut != IntPtr.Zero)
            {
                waveOutReset(_hWaveOut);
                for (int i = 0; i < BufferCount; i++)
                {
                    if ((_headers[i].dwFlags & WHDR_PREPARED) != 0)
                    {
                        waveOutUnprepareHeader(_hWaveOut, ref _headers[i], (uint)Marshal.SizeOf<WaveHdr>());
                    }
                    if (_buffers[i] != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(_buffers[i]);
                        _buffers[i] = IntPtr.Zero;
                    }
                }
                waveOutClose(_hWaveOut);
                _hWaveOut = IntPtr.Zero;
            }

            if (_hEvent != IntPtr.Zero)
            {
                CloseHandle(_hEvent);
                _hEvent = IntPtr.Zero;
            }
        }

        // Restore system timer resolution
        timeEndPeriod(1);
        _cts.Dispose();
        Log.Info(Tag, $"AudioPlayer disposed (played={_played}, underruns={_underruns})");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/AirServerLite.Tests --filter FullyQualifiedName~AudioPlayerTests --no-restore -v n`
Expected: PASS (all tests pass).

---

### Task 4: Connect Audio Pipeline in AirPlaySession

**Files:**
- Modify: `src/AirServerLite/AirPlay/AirPlaySession.cs:698-725`

- [ ] **Step 1: Wire the receiver sequence numbers to decoder and jitter buffer**

In `src/AirServerLite/AirPlay/AirPlaySession.cs`:
Attach the `AudioJitterBuffer` to `AudioPlayer.AttachJitterBuffer(_audioJitterBuffer)`.
In the `_audioReceiver.PacketReady` callback:
Decode with `_audioDecoder.TryDecode(pkt.Data, pkt.DataLength, ...)`, and write to `_audioJitterBuffer.Write(pkt.SequenceNumber, pkt.Timestamp, pcm, pcmLen, isPooled)`.

```csharp
_audioReceiver = new AudioStreamReceiver(_bindAddress, aesKey, audioIv);
_audioDecoder = new AudioDecoder(codecType, sr, 2, spf);
_audioPlayer = new AudioPlayer();
_audioPlayer.Volume = _volume;
_audioPlayer.IsMuted = _isMuted;

// Jitter buffer absorbs WiFi timing variance and reorders packets
_audioJitterBuffer = new AudioJitterBuffer(preBufferFrames: 6);
_audioPlayer.AttachJitterBuffer(_audioJitterBuffer);

_audioReceiver.PacketReady += pkt =>
{
    var dec = _audioDecoder;
    var jitter = _audioJitterBuffer;
    if (dec != null && jitter != null &&
        dec.TryDecode(pkt.Data, pkt.DataLength, out var pcm, out var pcmLen, out var isPooled))
    {
        jitter.Write(pkt.SequenceNumber, pkt.Timestamp, pcm, pcmLen, isPooled);
    }
    pkt.ReturnBuffer(); // Always return the pooled input buffer
};
_audioReceiver.Start();
```

- [ ] **Step 2: Run all unit tests across the whole test suite**

Run: `dotnet test tests/AirServerLite.Tests --no-restore -v n`
Expected: All tests pass (>= 48/48).

---

### Task 5: Build and Package Single-File Distributable

**Files:**
- Output: `dist/AirServerLite.exe`

- [ ] **Step 1: Execute single-file packaging via tools/pack.ps1**

Run command:
`powershell -ExecutionPolicy Bypass -File tools\pack.ps1 -SkipPlayfair`
Expected:
Outputs `dist\AirServerLite.exe` (~160MB single file containing .NET 8 runtime, playfair.dll, and FFmpeg DLLs).

- [ ] **Step 2: Verify packaged binary exists and check size**

Verify `dist\AirServerLite.exe` is updated with current timestamp and non-zero size.

- [ ] **Step 3: Commit all changes**

```bash
git add src/AirServerLite/Audio/ src/AirServerLite/AirPlay/ tests/AirServerLite.Tests/Audio/
git commit -m "feat(audio): hardware-clocked waveOut pump, 1ms timer, RTP jitter reordering, and single-file packaging"
```

---

## Verification Plan

### Automated Tests
- Run `dotnet test tests/AirServerLite.Tests --no-restore -v n`
- Verify AudioJitterBuffer tests (sequence reordering, duplicate rejection, priming).
- Verify AudioPlayer tests (initialization, volume, 32-sample ramp-in calculation).

### Manual Verification
1. Launch `dist\AirServerLite.exe` on the PC.
2. Mirror iPhone screen to AirServer-LITE over WiFi.
3. Open YouTube / Music app on iPhone, play a music video or track.
4. Listen for:
   - Zero "bập bùng" (continuous, steady playback).
   - Zero "khựng" (no micro-pauses or stutter).
   - Zero "rè" (no static, popping, or crackling at song start or during stream).
5. Inspect `dist\logs\airserver-*.log` to verify `underruns=0` or negligible, zero dropped frames in audio pump.

using System.Runtime.InteropServices;
using AirServerLite.Core;

namespace AirServerLite.Audio;

/// <summary>
/// Ultra-smooth real-time PCM audio playback via Windows Multimedia waveOut API.
/// 
/// Performance Architecture:
/// - Unmanaged WAVEHDR & Data Buffers: Allocated via Marshal.AllocHGlobal and passed by raw pointer
///   (IntPtr) so the Windows sound card driver can asynchronously write WHDR_DONE and clear WHDR_INQUEUE
///   directly in memory, without being severed by C# managed struct copying.
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
    private const int RampSamples = 32; // ~0.7ms de-click fade, applied only when resuming after a real gap

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

    private static readonly int HeaderSize = Marshal.SizeOf<WaveHdr>();
    private static readonly int FlagsOffset = Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwFlags)).ToInt32();
    private static readonly int BufferLengthOffset = Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwBufferLength)).ToInt32();
    private static readonly int BytesRecordedOffset = Marshal.OffsetOf<WaveHdr>(nameof(WaveHdr.dwBytesRecorded)).ToInt32();

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr hWaveOut, uint uDeviceID, ref WaveFormatEx lpFormat, IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveHdr, uint uSize);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveHdr, uint uSize);

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
    private readonly IntPtr[] _headerPtrs = new IntPtr[BufferCount];
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

    // Packet-loss concealment: a copy of the last real frame played, faded into the start of
    // a gap so a dropout tails off instead of cutting to hard silence. Touched only by the
    // pump thread.
    private readonly byte[] _lastRealFrame = new byte[BufferSize];
    private readonly byte[] _concealmentBuf = new byte[BufferSize];
    private int _lastRealFrameLen;
    private bool _haveLastRealFrame;
    private bool _concealmentUsedThisGap;

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

    /// <summary>Total number of audio buffers submitted to the driver.</summary>
    public long PlayedCount => Interlocked.Read(ref _played);

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

        // Allocate all WAVEHDR structs and data buffers in unmanaged memory
        for (int i = 0; i < BufferCount; i++)
        {
            _headerPtrs[i] = Marshal.AllocHGlobal(HeaderSize);
            _buffers[i] = Marshal.AllocHGlobal(BufferSize);

            // Zero header
            for (int b = 0; b < HeaderSize; b++)
                Marshal.WriteByte(_headerPtrs[i], b, 0);

            Marshal.WriteIntPtr(_headerPtrs[i], 0, _buffers[i]);
            Marshal.WriteInt32(_headerPtrs[i], BufferLengthOffset, BufferSize);
        }

        ApplyVolume();
        Log.Info(Tag, $"AudioPlayer ready (unmanaged waveOut CALLBACK_EVENT + timeBeginPeriod(1), {BufferCount}×{BufferSize}B buffers)");
    }

    /// <summary>
    /// Attach the jitter buffer and launch the hardware-clocked audio pump thread.
    /// </summary>
    public void AttachJitterBuffer(AudioJitterBuffer jitterBuffer)
    {
        _jitterBuffer = jitterBuffer;
        if (_pumpThread == null)
        {
            _pumpThread = new Thread(PumpLoop)
            {
                Name = "AudioHardwarePump",
                IsBackground = true,
                Priority = ThreadPriority.Highest
            };
            _pumpThread.Start();
            Log.Info(Tag, "Audio hardware pump thread started at Highest priority");
        }
    }

    // Ceiling on waveOut's own queue depth (~65ms). Kept deliberately shallow so audio does
    // not drift behind the low-latency video path; smoothness is bought with concealment on
    // the rare real gap, not with a deeper buffer.
    private const int TargetInFlightBuffers = 6;
    private static readonly byte[] SilenceFrame = new byte[1920]; // 10.9ms comfort silence for underruns

    private int CountInFlightBuffers()
    {
        int inFlight = 0;
        for (int i = 0; i < BufferCount; i++)
        {
            if (_headerPtrs[i] != IntPtr.Zero)
            {
                uint flags = (uint)Marshal.ReadInt32(_headerPtrs[i], FlagsOffset);
                if ((flags & WHDR_INQUEUE) != 0)
                    inFlight++;
            }
        }
        return inFlight;
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
                    // Keep just enough silence in the driver that its clock never fully stops
                    // (a drained waveOut device restarts with a click). Two frames ≈ 22ms - the
                    // minimum cushion, so re-prime adds almost nothing to resume latency.
                    int deficit;
                    lock (_lock)
                        deficit = (_disposed || _hWaveOut == IntPtr.Zero) ? 0 : 2 - CountInFlightBuffers();
                    if (deficit > 0) SubmitSilenceBuffers(deficit);

                    _needsRampIn = true;
                    // Wait briefly for packets to accumulate
                    Thread.Sleep(5);
                    continue;
                }

                // Check how many buffers are currently in the sound card driver queue
                int inFlight;
                int chosen = -1;
                lock (_lock)
                {
                    if (_disposed || _hWaveOut == IntPtr.Zero) break;

                    inFlight = CountInFlightBuffers();

                    // Only take a buffer slot if driver queue is below target depth
                    if (inFlight < TargetInFlightBuffers)
                    {
                        for (int i = 0; i < BufferCount; i++)
                        {
                            var candidate = (_nextBuffer + i) % BufferCount;
                            if (_headerPtrs[candidate] != IntPtr.Zero)
                            {
                                uint flags = (uint)Marshal.ReadInt32(_headerPtrs[candidate], FlagsOffset);
                                if ((flags & WHDR_INQUEUE) == 0)
                                {
                                    chosen = candidate;
                                    break;
                                }
                            }
                        }

                        if (chosen < 0)
                        {
                            for (int i = 0; i < BufferCount; i++)
                            {
                                if (_headerPtrs[i] != IntPtr.Zero)
                                {
                                    uint flags = (uint)Marshal.ReadInt32(_headerPtrs[i], FlagsOffset);
                                    if ((flags & WHDR_DONE) != 0)
                                    {
                                        chosen = i;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

                // If driver queue has enough audio ahead, wait for hardware clock tick (_hEvent)
                if (chosen < 0 || inFlight >= TargetInFlightBuffers)
                {
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

                        // Keep this frame for concealment if the stream stalls next.
                        int keep = Math.Min(frame.Length, BufferSize);
                        Buffer.BlockCopy(frame.Data, 0, _lastRealFrame, 0, keep);
                        _lastRealFrameLen = keep;
                        _haveLastRealFrame = true;
                        _concealmentUsedThisGap = false;
                    }
                    finally
                    {
                        frame.Return();
                    }
                }
                else
                {
                    // The jitter buffer is momentarily dry. As long as the driver still has
                    // audio queued the listener hears nothing wrong, so do NOT arm a fade-in
                    // here - re-ramping on every little Wi-Fi ripple is exactly what makes the
                    // sound throb ("bập bùng").
                    if (inFlight == 0)
                    {
                        // A real gap the listener would hear. Tail the last real frame down to
                        // silence once, then hold on comfort silence - a faded repeat masks a
                        // one-packet dropout far better than an abrupt cut, and costs no latency.
                        if (_haveLastRealFrame && _lastRealFrameLen > 0 && !_concealmentUsedThisGap)
                        {
                            BuildConcealmentFrame();
                            SubmitBuffer(chosen, _concealmentBuf, _lastRealFrameLen);
                            _concealmentUsedThisGap = true;
                            SubmitSilenceBuffers(1);
                        }
                        else
                        {
                            SubmitSilenceBuffers(2);
                        }
                        _needsRampIn = true;
                        Interlocked.Increment(ref _underruns);
                    }

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

    /// <summary>
    /// Push up to <paramref name="count"/> frames of digital silence straight into the driver
    /// queue to bridge a real underrun. A stopped waveOut device resumes with a click, so the
    /// clock must never actually run out.
    /// </summary>
    private int SubmitSilenceBuffers(int count)
    {
        int submitted = 0;
        for (int n = 0; n < count; n++)
        {
            int slot = -1;
            lock (_lock)
            {
                if (_disposed || _hWaveOut == IntPtr.Zero) break;
                for (int i = 0; i < BufferCount; i++)
                {
                    var candidate = (_nextBuffer + i) % BufferCount;
                    if (_headerPtrs[candidate] == IntPtr.Zero) continue;
                    uint flags = (uint)Marshal.ReadInt32(_headerPtrs[candidate], FlagsOffset);
                    if ((flags & WHDR_INQUEUE) == 0) { slot = candidate; break; }
                }
            }
            if (slot < 0) break;
            SubmitBuffer(slot, SilenceFrame, SilenceFrame.Length);
            submitted++;
        }
        return submitted;
    }

    /// <summary>
    /// Fill <see cref="_concealmentBuf"/> with the last real frame ramped from ~60% down to
    /// silence across its length, so a dropout fades out instead of cutting.
    /// </summary>
    private void BuildConcealmentFrame()
    {
        int pairs = _lastRealFrameLen / 4; // 16-bit stereo
        if (pairs <= 0) { _lastRealFrameLen = 0; return; }

        for (int i = 0; i < pairs; i++)
        {
            float gain = 0.6f * (1f - (float)i / pairs);
            int o = i * 4;

            short l = (short)(_lastRealFrame[o] | (_lastRealFrame[o + 1] << 8));
            short r = (short)(_lastRealFrame[o + 2] | (_lastRealFrame[o + 3] << 8));
            l = (short)(l * gain);
            r = (short)(r * gain);

            _concealmentBuf[o] = (byte)(l & 0xFF);
            _concealmentBuf[o + 1] = (byte)((l >> 8) & 0xFF);
            _concealmentBuf[o + 2] = (byte)(r & 0xFF);
            _concealmentBuf[o + 3] = (byte)((r >> 8) & 0xFF);
        }
    }

    private void SubmitBuffer(int index, byte[] pcm, int length)
    {
        lock (_lock)
        {
            if (_disposed || _hWaveOut == IntPtr.Zero) return;

            var headerPtr = _headerPtrs[index];
            var dataPtr = _buffers[index];
            if (headerPtr == IntPtr.Zero || dataPtr == IntPtr.Zero) return;

            _nextBuffer = (index + 1) % BufferCount;

            uint flags = (uint)Marshal.ReadInt32(headerPtr, FlagsOffset);
            if ((flags & WHDR_PREPARED) != 0)
            {
                waveOutUnprepareHeader(_hWaveOut, headerPtr, (uint)HeaderSize);
            }

            var copyLen = Math.Min(length, BufferSize);
            Marshal.Copy(pcm, 0, dataPtr, copyLen);

            Marshal.WriteIntPtr(headerPtr, 0, dataPtr);
            Marshal.WriteInt32(headerPtr, BufferLengthOffset, copyLen);
            Marshal.WriteInt32(headerPtr, BytesRecordedOffset, 0);
            Marshal.WriteInt32(headerPtr, FlagsOffset, 0);

            var prepRc = waveOutPrepareHeader(_hWaveOut, headerPtr, (uint)HeaderSize);
            if (prepRc != 0) return;

            waveOutWrite(_hWaveOut, headerPtr, (uint)HeaderSize);
        }
    }

    /// <summary>
    /// Fallback for direct PCM feeds without jitter buffer.
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
                if (_headerPtrs[candidate] != IntPtr.Zero)
                {
                    uint flags = (uint)Marshal.ReadInt32(_headerPtrs[candidate], FlagsOffset);
                    if ((flags & WHDR_INQUEUE) == 0)
                    {
                        chosen = candidate;
                        break;
                    }
                }
            }

            if (chosen < 0)
            {
                for (int i = 0; i < BufferCount; i++)
                {
                    if (_headerPtrs[i] != IntPtr.Zero)
                    {
                        uint flags = (uint)Marshal.ReadInt32(_headerPtrs[i], FlagsOffset);
                        if ((flags & WHDR_DONE) != 0)
                        {
                            chosen = i;
                            break;
                        }
                    }
                }
            }

            if (chosen < 0) chosen = _nextBuffer;

            SubmitBuffer(chosen, pcm, length);
            Interlocked.Increment(ref _played);
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

    /// <summary>
    /// Smooth raised-cosine ramp-out over the final samples of an underrun buffer
    /// to eliminate DC-offset step pops and speaker cracking.
    /// </summary>
    public static void ApplySmoothRampOut(byte[] pcm, int length, int rampSamples)
    {
        int samplePairs = Math.Min(rampSamples, length / 4);
        int startIndex = (length / 4) - samplePairs;
        for (int i = 0; i < samplePairs; i++)
        {
            float factor = 0.5f * (1.0f + (float)Math.Cos(Math.PI * (i + 1) / samplePairs));
            int offset = (startIndex + i) * 4;

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
                    if (_headerPtrs[i] != IntPtr.Zero)
                    {
                        uint flags = (uint)Marshal.ReadInt32(_headerPtrs[i], FlagsOffset);
                        if ((flags & WHDR_PREPARED) != 0)
                        {
                            waveOutUnprepareHeader(_hWaveOut, _headerPtrs[i], (uint)HeaderSize);
                        }
                        Marshal.FreeHGlobal(_headerPtrs[i]);
                        _headerPtrs[i] = IntPtr.Zero;
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

        AudioEnhancer.Reset();
        // Restore system timer resolution
        timeEndPeriod(1);
        _cts.Dispose();
        Log.Info(Tag, $"AudioPlayer disposed (played={_played}, underruns={_underruns})");
    }
}

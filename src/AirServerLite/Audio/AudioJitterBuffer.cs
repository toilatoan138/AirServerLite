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

    private readonly PriorityQueue<AudioFrame, long> _queue = new();
    private readonly object _lock = new();
    private readonly int _preBufferFrames;

    // The first prime waits for the full pre-buffer; a re-prime after a transient gap only
    // waits for this many, so a hiccup does not turn into a much longer silence. No effect on
    // steady-state latency - once primed, reads are served the moment a frame is available.
    private readonly int _rePrimeFrames;
    private bool _hasEverPrimed;
    private long _underrunResyncs;

    private bool _primed;
    private bool _disposed;
    private ushort _nextExpectedSeq;
    private bool _hasExpectedSeq;
    private long _lastUnwrappedSeq;
    private ushort _lastRawSeq;
    private bool _hasLastRawSeq;
    private ushort _maxSeq;
    private ulong _seenMask;
    private bool _hasMaxSeq;
    private long _written;
    private long _read;
    private long _dropped;

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
        _rePrimeFrames = Math.Clamp(_preBufferFrames / 2, 2, _preBufferFrames);
        Log.Info(Tag, $"Jitter buffer initialized (prime={_preBufferFrames} frames ≈ {_preBufferFrames * 10.9:F0}ms, " +
                      $"re-prime={_rePrimeFrames}, max={MaxCapacity})");
    }

    /// <summary>Times the buffer has run dry since it was created.</summary>
    public long UnderrunResyncs => Interlocked.Read(ref _underrunResyncs);

    /// <summary>
    /// Legacy constructor accepting an AudioPlayer for backward compatibility.
    /// </summary>
    public AudioJitterBuffer(AudioPlayer player, int preBufferFrames = 6) : this(preBufferFrames)
    {
        player?.AttachJitterBuffer(this);
    }

    /// <summary>
    /// Legacy method to start playback thread if needed.
    /// </summary>
    public void Start() { }

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

            // Check if packet was already played past
            if (_hasExpectedSeq)
            {
                short playedDiff = (short)(seqNum - _nextExpectedSeq);
                if (playedDiff < 0)
                {
                    Interlocked.Increment(ref _dropped);
                    if (isPooled) ArrayPool<byte>.Shared.Return(pcmData);
                    return;
                }
            }
            else
            {
                _nextExpectedSeq = seqNum;
                _hasExpectedSeq = true;
            }

            // Sliding 64-packet window duplicate detection (RFC 2401 / RFC 3550 standard)
            if (!_hasMaxSeq)
            {
                _maxSeq = seqNum;
                _seenMask = 1UL;
                _hasMaxSeq = true;
            }
            else
            {
                short diff = (short)(seqNum - _maxSeq);
                if (diff > 0)
                {
                    if (diff >= 64)
                        _seenMask = 1UL;
                    else
                        _seenMask = (_seenMask << diff) | 1UL;
                    _maxSeq = seqNum;
                }
                else
                {
                    int offset = -diff;
                    if (offset >= 64)
                    {
                        // Too old (>64 frames ≈ 700ms), discard
                        Interlocked.Increment(ref _dropped);
                        if (isPooled) ArrayPool<byte>.Shared.Return(pcmData);
                        return;
                    }
                    if ((_seenMask & (1UL << offset)) != 0)
                    {
                        // Duplicate packet, discard
                        Interlocked.Increment(ref _dropped);
                        if (isPooled) ArrayPool<byte>.Shared.Return(pcmData);
                        return;
                    }
                    _seenMask |= (1UL << offset);
                }
            }

            long unwrappedSeq;
            if (!_hasLastRawSeq)
            {
                unwrappedSeq = seqNum;
                _lastUnwrappedSeq = seqNum;
                _lastRawSeq = seqNum;
                _hasLastRawSeq = true;
            }
            else
            {
                short delta = (short)(seqNum - _lastRawSeq);
                unwrappedSeq = _lastUnwrappedSeq + delta;
                _lastUnwrappedSeq = unwrappedSeq;
                _lastRawSeq = seqNum;
            }

            // If queue exceeds max capacity, drop oldest to avoid latency buildup
            if (_queue.Count >= MaxCapacity)
            {
                var dropped = _queue.Dequeue();
                dropped.Return();
                Interlocked.Increment(ref _dropped);
            }

            var frame = new AudioFrame(seqNum, timestamp, pcmData, length, isPooled);
            _queue.Enqueue(frame, unwrappedSeq);
            Interlocked.Increment(ref _written);

            int primeAt = _hasEverPrimed ? _rePrimeFrames : _preBufferFrames;
            if (!_primed && _queue.Count >= primeAt)
            {
                _primed = true;
                _hasEverPrimed = true;
                Log.Info(Tag, $"Jitter buffer primed ({_queue.Count} frames buffered, starting playback)");
            }
        }
    }

    /// <summary>
    /// Overload for callers without sequence numbers.
    /// </summary>
    public void Write(byte[] pcmData, int length, bool isPooled)
    {
        lock (_lock)
        {
            ushort seq = _hasExpectedSeq ? (ushort)(_nextExpectedSeq + _queue.Count) : (ushort)0;
            Write(seq, 0, pcmData, length, isPooled);
        }
    }

    public void Write(byte[] pcmData)
    {
        if (pcmData != null)
            Write(pcmData, pcmData.Length, isPooled: false);
    }

    private DateTime _lastReadTime = DateTime.UtcNow;
    private const int SustainedSilenceMs = 250;

    /// <summary>
    /// Retrieve the next frame in sequence for the hardware audio pump.
    /// Returns true if a frame is ready; false if buffer underrun.
    /// </summary>
    public bool TryRead(out AudioFrame frame)
    {
        lock (_lock)
        {
            if (_disposed || !_primed)
            {
                frame = default;
                return false;
            }

            if (_queue.Count == 0)
            {
                frame = default;
                Interlocked.Increment(ref _underrunResyncs);

                // Only unprime on genuine sustained silence (>250ms), not a momentary
                // 5-10ms WiFi packet-arrival gap.
                if ((DateTime.UtcNow - _lastReadTime).TotalMilliseconds > SustainedSilenceMs)
                {
                    _primed = false;
                    Log.Debug(Tag, $"Sustained audio gap >{SustainedSilenceMs}ms — re-priming to {_rePrimeFrames} frames");
                }
                return false;
            }

            frame = _queue.Dequeue();
            _nextExpectedSeq = (ushort)(frame.SequenceNumber + 1);
            _lastReadTime = DateTime.UtcNow;
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

            _seenMask = 0;
            _hasMaxSeq = false;
            while (_queue.TryDequeue(out var frame, out _))
            {
                frame.Return();
            }
        }

        Log.Info(Tag, $"Jitter buffer disposed (written={_written}, read={_read}, dropped={_dropped})");
    }
}

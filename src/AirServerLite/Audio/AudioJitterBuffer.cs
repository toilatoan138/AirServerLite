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
    private readonly HashSet<ushort> _recentSeqs = new();
    private readonly object _lock = new();
    private readonly int _preBufferFrames;
    private bool _primed;
    private bool _disposed;
    private ushort _nextExpectedSeq;
    private bool _hasExpectedSeq;
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
        Log.Info(Tag, $"Jitter buffer initialized (target={_preBufferFrames} frames ≈ {_preBufferFrames * 10.9:F0}ms, max={MaxCapacity})");
    }

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

            // Check if this packet is an old duplicate or already queued
            if (_hasExpectedSeq)
            {
                short diff = (short)(seqNum - _nextExpectedSeq);
                if (diff < 0 || _recentSeqs.Contains(seqNum))
                {
                    // Already played past or duplicate in queue, discard
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

            // If queue exceeds max capacity, drop oldest to avoid latency buildup
            if (_queue.Count >= MaxCapacity)
            {
                var dropped = _queue.Dequeue();
                _recentSeqs.Remove(dropped.SequenceNumber);
                dropped.Return();
                Interlocked.Increment(ref _dropped);
            }

            _recentSeqs.Add(seqNum);
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
                // Only unprime if there has been genuine sustained silence (>250ms),
                // not a momentary 5-10ms WiFi packet arrival gap
                if ((DateTime.UtcNow - _lastReadTime).TotalMilliseconds > SustainedSilenceMs)
                {
                    _primed = false;
                    Log.Debug(Tag, "Sustained audio gap >250ms — pausing for re-priming");
                }
                return false;
            }

            frame = _queue.Dequeue();
            _recentSeqs.Remove((ushort)(frame.SequenceNumber - 32));
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

            _recentSeqs.Clear();
            while (_queue.TryDequeue(out var frame, out _))
            {
                frame.Return();
            }
        }

        Log.Info(Tag, $"Jitter buffer disposed (written={_written}, read={_read}, dropped={_dropped})");
    }
}

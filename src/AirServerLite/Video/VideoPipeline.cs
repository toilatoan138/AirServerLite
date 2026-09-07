using System.Collections.Concurrent;
using System.Diagnostics;
using AirServerLite.AirPlay.Streaming;
using AirServerLite.Core;

namespace AirServerLite.Video;

/// <summary>
/// Network thread -> decode thread -> UI thread, with an explicit latency policy at each hop.
///
/// The rule that keeps end-to-end delay under control is: never queue. A mirroring stream has
/// no value in old frames. When the decoder or the UI falls behind we drop, we do not buffer -
/// buffering just converts a transient CPU spike into permanent added latency that never
/// recovers.
///
///   - Input queue is bounded (MaxQueuedFrames). On overflow the OLDEST packet is discarded,
///     except parameter sets, which are always kept: losing SPS/PPS costs the whole stream.
///   - Output is a single slot. If the UI has not picked up the previous frame, it is
///     overwritten.
/// </summary>
public sealed class VideoPipeline : IDisposable
{
    private const string Tag = "pipeline";

    private readonly BlockingCollection<VideoPacket> _queue;
    private readonly int _maxQueued;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _decodeThread;

    private readonly object _slotLock = new();
    private BgraFrame? _pendingFrame;
    private BgraFrame _frontBuffer = new();
    private BgraFrame _backBuffer = new();

    private long _packetsDropped;
    private long _framesPresented;
    private readonly Stopwatch _statsClock = Stopwatch.StartNew();

    private bool _disposed;

    /// <summary>Raised on the decode thread when a new frame is available in the slot.</summary>
    public event Action? FrameAvailable;

    /// <summary>Raised on the decode thread with a human-readable stats line, ~1 Hz.</summary>
    public event Action<string>? StatsUpdated;

    public int Width { get; private set; }
    public int Height { get; private set; }

    public VideoPipeline(int maxQueuedFrames)
    {
        _maxQueued = Math.Max(1, maxQueuedFrames);
        _queue = new BlockingCollection<VideoPacket>(new ConcurrentQueue<VideoPacket>(), _maxQueued * 4);
    }

    public void Start()
    {
        _decodeThread = new Thread(DecodeLoop)
        {
            IsBackground = true,
            Name = "h264-decode",
            // Above normal keeps the decoder ahead of UI work without starving the input
            // thread the way Highest would.
            Priority = ThreadPriority.AboveNormal
        };
        _decodeThread.Start();
        Log.Info(Tag, "Decode thread started");
    }

    /// <summary>Called from the network thread. Never blocks.</summary>
    public void Submit(VideoPacket packet)
    {
        if (_disposed) return;

        if (!_queue.TryAdd(packet))
        {
            // Full: make room by dropping the oldest non-parameter-set packet.
            if (_queue.TryTake(out var dropped))
            {
                Interlocked.Increment(ref _packetsDropped);
                if (dropped.IsParameterSet)
                {
                    // Never lose SPS/PPS - put it back ahead of the new packet.
                    _queue.TryAdd(dropped);
                    return;
                }
            }
            _queue.TryAdd(packet);
        }
    }

    private void DecodeLoop()
    {
        H264Decoder? decoder = null;
        var lastStats = Stopwatch.StartNew();
        long decodedSinceReport = 0;
        double latencySum = 0;
        long latencySamples = 0;

        try
        {
            decoder = new H264Decoder();

            foreach (var packet in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    if (!decoder.TryDecode(packet.Data, packet.Timestamp, _backBuffer, out var frame))
                        continue;

                    if (frame is null) continue;

                    Width = frame.Width;
                    Height = frame.Height;

                    lock (_slotLock)
                    {
                        // Swap: the freshly-decoded buffer becomes pending, and whatever the
                        // UI is not currently holding becomes the next decode target.
                        (_backBuffer, _pendingFrame) = (_pendingFrame ?? _frontBuffer, frame);
                    }

                    decodedSinceReport++;
                    var age = (DateTime.UtcNow - packet.ArrivedUtc).TotalMilliseconds;
                    latencySum += age;
                    latencySamples++;

                    FrameAvailable?.Invoke();
                }
                catch (Exception ex)
                {
                    Log.Error(Tag, "Decode iteration failed - continuing", ex);
                }

                if (lastStats.ElapsedMilliseconds >= 1000)
                {
                    var secs = lastStats.Elapsed.TotalSeconds;
                    var fps = decodedSinceReport / secs;
                    var avgLatency = latencySamples > 0 ? latencySum / latencySamples : 0;

                    var line = $"{Width}x{Height}  {fps:F1} fps  decode+queue {avgLatency:F1} ms  " +
                               $"queue {_queue.Count}  dropped {Interlocked.Read(ref _packetsDropped)}";
                    StatsUpdated?.Invoke(line);
                    Log.Debug(Tag, line);

                    decodedSinceReport = 0;
                    latencySum = 0;
                    latencySamples = 0;
                    lastStats.Restart();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(Tag, "Decode thread died", ex);
            StatsUpdated?.Invoke("Decoder stopped: " + ex.Message);
        }
        finally
        {
            decoder?.Dispose();
            Log.Info(Tag, "Decode thread exited");
        }
    }

    /// <summary>
    /// Called from the UI thread. Returns the newest decoded frame, or null if nothing new
    /// has arrived since the last call.
    /// </summary>
    public BgraFrame? AcquireFrame()
    {
        lock (_slotLock)
        {
            if (_pendingFrame is null) return null;
            _frontBuffer = _pendingFrame;
            _pendingFrame = null;
            _framesPresented++;
            return _frontBuffer;
        }
    }

    public void Reset()
    {
        while (_queue.TryTake(out _)) { }
        lock (_slotLock) _pendingFrame = null;
        Log.Info(Tag, "Pipeline reset");
    }

    /// <summary>Wire this pipeline to a mirror receiver.</summary>
    public void Attach(MirrorStreamReceiver receiver)
    {
        receiver.PacketReady += Submit;
        receiver.Closed += reason =>
        {
            Log.Info(Tag, "Mirror stream closed: " + reason);
            Reset();
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        try { _queue.CompleteAdding(); } catch { }
        try { _decodeThread?.Join(TimeSpan.FromSeconds(2)); } catch { }

        _queue.Dispose();
        _cts.Dispose();
        Log.Info(Tag, $"Pipeline disposed ({_framesPresented} frames presented, " +
                      $"{_packetsDropped} packets dropped)");
    }
}

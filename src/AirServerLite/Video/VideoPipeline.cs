using System.Collections.Concurrent;
using System.Diagnostics;
using AirServerLite.AirPlay.Streaming;
using AirServerLite.Core;

namespace AirServerLite.Video;

/// <summary>
/// Network thread -> decode thread -> UI thread, with an explicit latency policy at each hop.
///
/// The rule that keeps end-to-end delay under control is: never queue. A mirroring stream has
/// no value in old frames. When the UI falls behind we drop, we do not buffer - buffering just
/// converts a transient CPU spike into permanent added latency that never recovers.
///
/// Two things make that harder than it sounds, and both are handled here.
///
/// H.264 is not drop-tolerant. Discarding individual packets to make room tears the reference
/// chain, and every later P-frame then predicts from data the decoder never saw. That does not
/// look like a dropped frame; it looks like green and smeared macroblocks that persist until
/// the next IDR. So when the backlog overflows we discard all of it and idle until a keyframe
/// gives us a clean restart - a brief freeze instead of prolonged garbage.
///
/// The backlog itself is the symptom, not the disease. Every optional enhancement pass (NIS,
/// vibrance, MEMC) runs on the decode thread, so once they exceed the frame budget the queue
/// can only grow. <see cref="ShedLevel"/> therefore sheds those passes as the backlog builds:
/// keeping the stream intact matters more than sharpening it. At 1080p nothing is shed; at 2K
/// the expensive passes bow out and the picture stays clean.
///
/// Buffer ownership: the pipeline owns every BgraFrame it hands around, recycled through
/// <see cref="_pool"/>. Nothing outside may keep one past the next AcquireFrame.
/// </summary>
public sealed class VideoPipeline : IDisposable
{
    private const string Tag = "pipeline";

    /// <summary>Backlog at which MEMC stops - it costs a whole extra frame plus a copy.</summary>
    private const int MemcShedBacklog = 3;
    /// <summary>Backlog at which the per-pixel NIS and vibrance passes stop too.</summary>
    private const int FilterShedBacklog = 10;
    /// <summary>Backlog we must fall back to before restoring the passes, so modes cannot flap.</summary>
    private const int RestoreBacklog = 1;

    /// <summary>Buffers held for reuse. Four covers decode + ready + present + one in flight.</summary>
    private const int PoolLimit = 6;

    private readonly BlockingCollection<VideoPacket> _queue;
    private readonly int _maxQueued;
    private readonly CancellationTokenSource _cts = new();
    private Thread? _decodeThread;

    private readonly object _slotLock = new();
    private readonly Stack<BgraFrame> _pool = new();
    private BgraFrame? _readyBuffer;
    private BgraFrame? _presentBuffer;
    private int _width;
    private int _height;

    private long _packetsDropped;
    private long _framesPresented;

    /// <summary>Set by the network thread on overflow, cleared by the decode thread on the next IDR.</summary>
    private volatile bool _awaitingKeyframe;
    private volatile bool _resetRequested;

    private bool _disposed;

    /// <summary>Raised on the decode thread when a new frame is available in the slot.</summary>
    public event Action? FrameAvailable;

    /// <summary>Raised on the decode thread with a human-readable stats line, ~1 Hz.</summary>
    public event Action<string>? StatsUpdated;

    public int Width { get { lock (_slotLock) return _width; } }
    public int Height { get { lock (_slotLock) return _height; } }
    public long PacketsDropped => Interlocked.Read(ref _packetsDropped);

    public RtxFidelitySettings RtxSettings { get; }
    private readonly Video.Processing.NvidiaImageScaler? _nis;
    private readonly Video.Processing.RtxColorEnhancer? _colorEnhancer;
    private readonly Video.Processing.MotionFrameSynthesizer? _synthesizer;

    /// <summary>Previous decoded frame, kept for MEMC. Decode thread only; never pooled.</summary>
    private readonly BgraFrame _prevFrame = new();
    private bool _prevValid;

    public VideoPipeline(int maxQueuedFrames, RtxFidelitySettings? rtxSettings = null)
    {
        _maxQueued = Math.Max(1, maxQueuedFrames);
        RtxSettings = rtxSettings ?? RtxFidelitySettings.CreateRtxUltra();

        if (RtxSettings.EnableNvidiaImageScaling)
            _nis = new Video.Processing.NvidiaImageScaler(RtxSettings.Sharpness);

        if (RtxSettings.EnableRtxDigitalVibrance)
            _colorEnhancer = new Video.Processing.RtxColorEnhancer(RtxSettings.VibranceBoost);

        if (RtxSettings.EnableMotionInterpolation)
            _synthesizer = new Video.Processing.MotionFrameSynthesizer(RtxSettings.CpuThreadCount);

        // Deep enough to absorb a Wi-Fi A-MPDU burst without overflowing. It should never sit
        // full: load shedding keeps the steady-state backlog near zero, and reaching this cap
        // is treated as a fault that triggers a keyframe resync.
        _queue = new BlockingCollection<VideoPacket>(new ConcurrentQueue<VideoPacket>(), Math.Max(128, _maxQueued * 16));
    }

    public void Start()
    {
        _decodeThread = new Thread(DecodeLoop)
        {
            IsBackground = true,
            Name = "h264-decode",
            Priority = ThreadPriority.Highest
        };
        _decodeThread.Start();
        Log.Info(Tag, "Decode thread started");
    }

    /// <summary>Called from the network thread. Never blocks.</summary>
    public void Submit(VideoPacket packet)
    {
        if (_disposed) return;

        if (_queue.TryAdd(packet)) return;

        // Full. Dropping individual packets to make room would leave the decoder predicting
        // from frames it never received, so drop the entire backlog and wait for an IDR
        // instead. Parameter sets are the one thing worth carrying across: losing SPS/PPS
        // costs the whole stream, not just the frames between here and the next keyframe.
        List<VideoPacket>? parameterSets = null;
        long discarded = 0;

        while (_queue.TryTake(out var stale))
        {
            if (stale.IsParameterSet) (parameterSets ??= new List<VideoPacket>()).Add(stale);
            else discarded++;
        }

        if (parameterSets != null)
            foreach (var ps in parameterSets) _queue.TryAdd(ps);

        Interlocked.Add(ref _packetsDropped, discarded);
        _awaitingKeyframe = true;
        _queue.TryAdd(packet);

        Log.Warn(Tag, $"Input backlog overflowed - discarded {discarded} packets, " +
                      "waiting for the next keyframe to resynchronise");
    }

    /// <summary>
    /// How much optional work the decode thread is currently skipping. Rises with the backlog
    /// and only falls once the queue has genuinely drained, so the pipeline cannot oscillate
    /// between modes every frame.
    /// </summary>
    private enum ShedLevel
    {
        None = 0,      // everything on
        NoMemc = 1,    // frame synthesis off
        FiltersOff = 2 // synthesis, NIS and vibrance all off
    }

    private static ShedLevel NextShedLevel(ShedLevel current, int backlog)
    {
        if (backlog >= FilterShedBacklog) return ShedLevel.FiltersOff;
        if (backlog >= MemcShedBacklog) return current > ShedLevel.NoMemc ? current : ShedLevel.NoMemc;
        if (backlog <= RestoreBacklog) return ShedLevel.None;
        return current;
    }

    private void DecodeLoop()
    {
        H264Decoder? decoder = null;
        var lastStats = Stopwatch.StartNew();
        var frameClock = Stopwatch.StartNew();
        long decodedSinceReport = 0;
        long synthesizedSinceReport = 0;
        double latencySum = 0;
        long latencySamples = 0;
        double frameIntervalMs = 16.7;
        var shed = ShedLevel.None;

        try
        {
            decoder = new H264Decoder { UpscaleMode = RtxSettings.UpscaleMode };

            foreach (var packet in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    if (_resetRequested)
                    {
                        _resetRequested = false;
                        decoder.Flush();
                        _prevValid = false;
                    }

                    // After an overflow the decoder's reference frames are stale. Feeding it
                    // mid-GOP packets would only produce garbage, so idle - parameter sets
                    // excepted - until an IDR arrives with SPS/PPS attached.
                    if (_awaitingKeyframe)
                    {
                        if (packet.IsKeyframe)
                        {
                            _awaitingKeyframe = false;
                            decoder.Flush();
                            _prevValid = false;
                            Log.Info(Tag, "Keyframe received - stream resynchronised");
                        }
                        else if (!packet.IsParameterSet)
                        {
                            continue;
                        }
                    }

                    var target = Rent();
                    if (!decoder.TryDecode(packet.Data, packet.Timestamp, target, out var frame) || frame is null)
                    {
                        Recycle(target);
                        continue;
                    }

                    int backlog = _queue.Count;
                    shed = NextShedLevel(shed, backlog);
                    bool filtersOn = shed < ShedLevel.FiltersOff;
                    bool memcOn = shed < ShedLevel.NoMemc;

                    // NIS writes into a second buffer and we present that one, rather than
                    // filtering into scratch and copying back over the frame. At 2K that copy
                    // alone was ~15 MB per frame on the critical path.
                    if (filtersOn && _nis != null)
                    {
                        var scaled = Rent();
                        PrepareLike(scaled, frame);
                        _nis.Process(frame.Pixels, scaled.Pixels, frame.Width, frame.Height, frame.Stride);
                        Recycle(frame);
                        frame = scaled;
                    }

                    if (filtersOn) _colorEnhancer?.Enhance(frame.Pixels, frame.Width, frame.Height, frame.Stride);

                    // Synthesize the in-between frame first, then pace the real one behind it.
                    // Without that gap the UI - which keeps only the newest frame - would never
                    // observe the synthesized one, and MEMC would cost a full frame of work for
                    // no visible effect. We only spend that pause when there is nothing waiting.
                    if (memcOn && _synthesizer != null && _prevValid)
                    {
                        var synth = Rent();
                        if (_synthesizer.Synthesize(_prevFrame, frame, synth))
                        {
                            DeliverFrame(synth);
                            synthesizedSinceReport++;

                            if (backlog == 0)
                            {
                                int pause = (int)Math.Clamp(frameIntervalMs / 2.0, 1, 8);
                                Thread.Sleep(pause);
                            }
                        }
                        else
                        {
                            Recycle(synth);
                        }
                    }

                    if (_synthesizer != null)
                    {
                        PrepareLike(_prevFrame, frame);
                        Array.Copy(frame.Pixels, _prevFrame.Pixels, frame.Pixels.Length);
                        _prevFrame.Timestamp = frame.Timestamp;
                        _prevValid = true;
                    }

                    DeliverFrame(frame);

                    decodedSinceReport++;
                    frameIntervalMs = frameClock.Elapsed.TotalMilliseconds;
                    frameClock.Restart();

                    var age = (DateTime.UtcNow - packet.ArrivedUtc).TotalMilliseconds;
                    latencySum += age;
                    latencySamples++;
                }
                catch (Exception ex)
                {
                    Log.Error(Tag, "Decode iteration failed - continuing", ex);
                }

                if (lastStats.ElapsedMilliseconds >= 1000)
                {
                    var secs = lastStats.Elapsed.TotalSeconds;
                    var decodeFps = decodedSinceReport / secs;
                    var presentedFps = (decodedSinceReport + synthesizedSinceReport) / secs;
                    var avgLatency = latencySamples > 0 ? latencySum / latencySamples : 0;
                    var memcTag = synthesizedSinceReport > 0 ? " [120Hz MEMC]" : "";
                    var shedTag = shed switch
                    {
                        ShedLevel.FiltersOff => "  shed: MEMC+NIS",
                        ShedLevel.NoMemc => "  shed: MEMC",
                        _ => ""
                    };

                    // The HUD splits this on double spaces and reads field 1 as the frame rate,
                    // so keep the leading "WxH", "<n> fps" shape intact.
                    var line = $"{Width}x{Height}  {presentedFps:F1} fps{memcTag}  [{decoder.AccelerationMode}]  " +
                               $"NIS: {(RtxSettings.EnableNvidiaImageScaling && shed < ShedLevel.FiltersOff ? "ON" : "OFF")}  " +
                               $"queue {_queue.Count}  dropped {Interlocked.Read(ref _packetsDropped)}{shedTag}";
                    StatsUpdated?.Invoke(line);
                    Log.Debug(Tag, $"{line}  decode {decodeFps:F1} fps  latency {avgLatency:F1} ms");

                    decodedSinceReport = 0;
                    synthesizedSinceReport = 0;
                    latencySum = 0;
                    latencySamples = 0;
                    lastStats.Restart();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(Tag, "Decode thread fatal error", ex);
        }
        finally
        {
            decoder?.Dispose();
            Log.Info(Tag, "Decode thread exiting");
        }
    }

    /// <summary>Size <paramref name="dst"/> to match <paramref name="src"/>'s surface.</summary>
    private static void PrepareLike(BgraFrame dst, BgraFrame src)
    {
        if (dst.Pixels.Length != src.Pixels.Length) dst.Pixels = new byte[src.Pixels.Length];
        dst.Width = src.Width;
        dst.Height = src.Height;
        dst.Stride = src.Stride;
        dst.Timestamp = src.Timestamp;
        dst.DecodedUtc = src.DecodedUtc;
    }

    private BgraFrame Rent()
    {
        lock (_slotLock)
        {
            if (_pool.Count > 0) return _pool.Pop();
        }
        return new BgraFrame();
    }

    private void Recycle(BgraFrame frame)
    {
        lock (_slotLock)
        {
            if (_pool.Count < PoolLimit) _pool.Push(frame);
        }
    }

    private void DeliverFrame(BgraFrame frame)
    {
        lock (_slotLock)
        {
            _width = frame.Width;
            _height = frame.Height;

            // Single slot: if the UI has not collected the previous frame it is already stale,
            // so reclaim it rather than queueing behind it.
            if (_readyBuffer != null && _pool.Count < PoolLimit) _pool.Push(_readyBuffer);
            _readyBuffer = frame;
        }

        FrameAvailable?.Invoke();
    }

    /// <summary>
    /// Called from the UI thread. Returns the newest decoded frame, or null if nothing new
    /// has arrived since the last call. The returned frame stays valid until the next call.
    /// </summary>
    public BgraFrame? AcquireFrame()
    {
        lock (_slotLock)
        {
            if (_readyBuffer is null) return null;

            if (_presentBuffer != null && _pool.Count < PoolLimit) _pool.Push(_presentBuffer);
            _presentBuffer = _readyBuffer;
            _readyBuffer = null;
            _framesPresented++;
            return _presentBuffer;
        }
    }

    public void Reset()
    {
        while (_queue.TryTake(out _)) { }
        lock (_slotLock)
        {
            if (_readyBuffer != null && _pool.Count < PoolLimit) _pool.Push(_readyBuffer);
            _readyBuffer = null;
        }
        _awaitingKeyframe = true;
        _resetRequested = true;
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

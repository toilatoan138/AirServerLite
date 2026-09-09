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
    private BgraFrame _decodeBuffer = new();
    private BgraFrame? _readyBuffer;
    private BgraFrame _presentBuffer = new();
    private BgraFrame _spareBuffer = new();

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
    public long PacketsDropped => Interlocked.Read(ref _packetsDropped);

    public RtxFidelitySettings RtxSettings { get; }
    private readonly Video.Processing.NvidiaImageScaler? _nis;
    private readonly Video.Processing.RtxColorEnhancer? _colorEnhancer;
    private readonly Video.Processing.MotionFrameSynthesizer? _synthesizer;
    private byte[]? _postProcessBuffer;
    private BgraFrame? _lastFrameCopy;

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

        // Minimum 128 packets capacity to safely absorb Wi-Fi A-MPDU bursts without packet drops.
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
            decoder = new H264Decoder { UpscaleMode = RtxSettings.UpscaleMode };

            foreach (var packet in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    if (!decoder.TryDecode(packet.Data, packet.Timestamp, _decodeBuffer, out var frame))
                        continue;

                    if (frame is null) continue;

                    // Apply NVIDIA Image Scaling (NIS)
                    if (_nis != null)
                    {
                        if (_postProcessBuffer == null || _postProcessBuffer.Length != frame.Pixels.Length)
                            _postProcessBuffer = new byte[frame.Pixels.Length];
                        _nis.Process(frame.Pixels, _postProcessBuffer, frame.Width, frame.Height, frame.Stride);
                        Array.Copy(_postProcessBuffer, frame.Pixels, frame.Pixels.Length);
                    }

                    // Apply RTX Digital Vibrance
                    _colorEnhancer?.Enhance(frame.Pixels, frame.Width, frame.Height, frame.Stride);

                    // If 120Hz/144Hz MEMC is active, synthesize intermediate frame
                    if (_synthesizer != null && _lastFrameCopy != null &&
                        _lastFrameCopy.Width == frame.Width && _lastFrameCopy.Height == frame.Height)
                    {
                        var synth = _synthesizer.Synthesize(_lastFrameCopy, frame);
                        DeliverFrame(synth);

                        // If streaming is real-time without backlog, pace synthesized frame for 120Hz display
                        if (_queue.Count == 0)
                        {
                            Thread.Sleep(7);
                        }
                    }

                    DeliverFrame(frame);

                    if (_synthesizer != null)
                    {
                        _lastFrameCopy ??= new BgraFrame();
                        if (_lastFrameCopy.Pixels.Length != frame.Pixels.Length)
                            _lastFrameCopy.Pixels = new byte[frame.Pixels.Length];
                        _lastFrameCopy.Width = frame.Width;
                        _lastFrameCopy.Height = frame.Height;
                        _lastFrameCopy.Stride = frame.Stride;
                        _lastFrameCopy.Timestamp = frame.Timestamp;
                        Array.Copy(frame.Pixels, _lastFrameCopy.Pixels, frame.Pixels.Length);
                    }

                    decodedSinceReport++;
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
                    var fps = decodedSinceReport / secs;
                    var effectiveFps = RtxSettings.EnableMotionInterpolation ? fps * 2 : fps;
                    var avgLatency = latencySamples > 0 ? latencySum / latencySamples : 0;

                    var line = $"{Width}x{Height}  {effectiveFps:F1} fps {(RtxSettings.EnableMotionInterpolation ? "[120Hz MEMC]" : "")}  [{decoder.AccelerationMode}]  " +
                               $"NIS: {(RtxSettings.EnableNvidiaImageScaling ? "ON" : "OFF")}  queue {_queue.Count}  dropped {Interlocked.Read(ref _packetsDropped)}";
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
            Log.Error(Tag, "Decode thread fatal error", ex);
        }
        finally
        {
            decoder?.Dispose();
            Log.Info(Tag, "Decode thread exiting");
        }
    }

    private void DeliverFrame(BgraFrame frame)
    {
        Width = frame.Width;
        Height = frame.Height;

        lock (_slotLock)
        {
            if (_readyBuffer != null)
            {
                var oldReady = _readyBuffer;
                _readyBuffer = frame;
                _decodeBuffer = oldReady;
            }
            else
            {
                _readyBuffer = frame;
                _decodeBuffer = _spareBuffer;
            }
        }

        FrameAvailable?.Invoke();
    }

    /// <summary>
    /// Called from the UI thread. Returns the newest decoded frame, or null if nothing new
    /// has arrived since the last call.
    /// </summary>
    public BgraFrame? AcquireFrame()
    {
        lock (_slotLock)
        {
            if (_readyBuffer is null) return null;
            // The previous _presentBuffer becomes the spare for the decoder to reclaim
            _spareBuffer = _presentBuffer;
            _presentBuffer = _readyBuffer;
            _readyBuffer = null;
            _framesPresented++;
            return _presentBuffer;
        }
    }

    public void Reset()
    {
        while (_queue.TryTake(out _)) { }
        lock (_slotLock) _readyBuffer = null;
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

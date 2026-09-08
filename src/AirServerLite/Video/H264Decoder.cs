using AirServerLite.Core;
using FFmpeg.AutoGen;

namespace AirServerLite.Video;

/// <summary>A decoded frame as tightly-packed BGRA32, ready for a WriteableBitmap.</summary>
public sealed class BgraFrame
{
    public byte[] Pixels = Array.Empty<byte>();
    public int Width;
    public int Height;
    public int Stride;
    public ulong Timestamp;
    public DateTime DecodedUtc;
}

/// <summary>
/// Minimal-latency H.264 decoder.
///
/// The configuration here is deliberately conservative about latency rather than throughput:
///   - AV_CODEC_FLAG_LOW_DELAY tells the decoder to emit each frame as soon as it is complete.
///   - Slice threading only. Frame threading would buy throughput at the cost of holding
///     several frames in flight, which is exactly the latency we are trying to avoid.
///   - No reordering: mirroring streams carry no B-frames, so output order == input order.
///
/// Not thread-safe; owned by the single decode thread in <see cref="VideoPipeline"/>.
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    private const string Tag = "h264";

    private AVCodecContext* _ctx;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwsContext* _sws;

    private int _swsWidth, _swsHeight;
    private AVPixelFormat _swsFormat = AVPixelFormat.AV_PIX_FMT_NONE;

    private bool _disposed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public long FramesDecoded { get; private set; }

    public H264Decoder()
    {
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec == null)
            throw new InvalidOperationException("This FFmpeg build has no H.264 decoder");

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (_ctx == null)
            throw new InvalidOperationException("avcodec_alloc_context3 failed");

        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _ctx->flags2 |= ffmpeg.AV_CODEC_FLAG2_FAST;
        _ctx->thread_type = ffmpeg.FF_THREAD_SLICE;
        _ctx->thread_count = Math.Clamp(Environment.ProcessorCount, 2, 8);
        _ctx->err_recognition = 0;

        var rc = ffmpeg.avcodec_open2(_ctx, codec, null);
        if (rc < 0)
            throw new InvalidOperationException("avcodec_open2 failed: " + ErrorText(rc));

        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();

        Log.Info(Tag, $"Decoder ready (slice threads={_ctx->thread_count}, low-delay)");
    }

    /// <summary>
    /// Feed one Annex-B access unit. Returns each decoded frame converted to BGRA.
    /// The returned array is reused between calls - copy it if you need to keep it.
    /// </summary>
    public bool TryDecode(byte[] annexB, ulong timestamp, BgraFrame reuse, out BgraFrame? output)
    {
        output = null;
        if (_disposed || annexB.Length == 0) return false;

        fixed (byte* p = annexB)
        {
            _packet->data = p;
            _packet->size = annexB.Length;
            _packet->pts = ffmpeg.AV_NOPTS_VALUE;

            var rc = ffmpeg.avcodec_send_packet(_ctx, _packet);
            if (rc < 0)
            {
                // EAGAIN means the decoder still has output pending; anything else on a
                // mirroring stream is a corrupt packet we can skip without tearing down.
                if (rc != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                    Log.Debug(Tag, "avcodec_send_packet: " + ErrorText(rc));
            }
        }

        var receive = ffmpeg.avcodec_receive_frame(_ctx, _frame);
        if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
            return false;

        if (receive < 0)
        {
            Log.Debug(Tag, "avcodec_receive_frame: " + ErrorText(receive));
            return false;
        }

        try
        {
            output = Convert(reuse, timestamp);
            FramesDecoded++;
            return output is not null;
        }
        finally
        {
            ffmpeg.av_frame_unref(_frame);
        }
    }

    private BgraFrame? Convert(BgraFrame reuse, ulong timestamp)
    {
        var w = _frame->width;
        var h = _frame->height;
        if (w <= 0 || h <= 0) return null;

        var format = (AVPixelFormat)_frame->format;

        if (_sws == null || w != _swsWidth || h != _swsHeight || format != _swsFormat)
        {
            if (_sws != null) ffmpeg.sws_freeContext(_sws);

            _sws = ffmpeg.sws_getContext(
                w, h, format,
                w, h, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);

            if (_sws == null)
            {
                Log.Error(Tag, $"sws_getContext failed for {w}x{h} {format}");
                return null;
            }

            _swsWidth = w;
            _swsHeight = h;
            _swsFormat = format;
            Width = w;
            Height = h;
            Log.Info(Tag, $"Video geometry {w}x{h} {format}");
        }

        var stride = w * 4;
        var needed = stride * h;
        if (reuse.Pixels.Length != needed) reuse.Pixels = new byte[needed];

        fixed (byte* dst = reuse.Pixels)
        {
            var srcData = new byte*[4] { _frame->data[0], _frame->data[1], _frame->data[2], _frame->data[3] };
            var srcStride = new int[4]
            {
                _frame->linesize[0], _frame->linesize[1], _frame->linesize[2], _frame->linesize[3]
            };
            var dstData = new byte*[4] { dst, null, null, null };
            var dstStride = new int[4] { stride, 0, 0, 0 };

            ffmpeg.sws_scale(_sws, srcData, srcStride, 0, h, dstData, dstStride);
        }

        reuse.Width = w;
        reuse.Height = h;
        reuse.Stride = stride;
        reuse.Timestamp = timestamp;
        reuse.DecodedUtc = DateTime.UtcNow;
        return reuse;
    }

    /// <summary>Drop decoder state - call when the phone restarts the stream.</summary>
    public void Flush()
    {
        if (_ctx != null) ffmpeg.avcodec_flush_buffers(_ctx);
    }

    private static string ErrorText(int code)
    {
        const int bufSize = 256;
        var buf = stackalloc byte[bufSize];
        ffmpeg.av_strerror(code, buf, bufSize);
        return System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buf) ?? code.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }

        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_packet != null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }

        Log.Info(Tag, $"Decoder disposed after {FramesDecoded} frames");
    }
}

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using AirServerLite.Core;
using AirServerLite.Video;
using FFmpeg.AutoGen;

namespace AirServerLite.Audio;

public enum AudioCodecType
{
    AacEld,
    Alac
}

/// <summary>
/// Decodes AirPlay audio packets (AAC-ELD, AAC, or ALAC) into raw 16-bit 44.1kHz Stereo PCM.
/// Uses FFmpeg libavcodec + libswresample.
/// </summary>
public sealed unsafe class AudioDecoder : IDisposable
{
    private const string Tag = "audio-dec";

    public AudioCodecType CodecType { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int BitsPerSample => 16;
    public int FramesPerPacket { get; }

    private AVCodecContext* _ctx;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwrContext* _swr;
    private readonly object _lock = new();
    private bool _disposed;

    public AudioDecoder(AudioCodecType codecType = AudioCodecType.AacEld, int sampleRate = 44100, int channels = 2, int framesPerPacket = 480)
    {
        CodecType = codecType;
        SampleRate = sampleRate > 0 ? sampleRate : 44100;
        Channels = channels > 0 ? channels : 2;
        FramesPerPacket = framesPerPacket > 0 ? framesPerPacket : (codecType == AudioCodecType.Alac ? 352 : 480);

        if (FFmpegLoader.IsLoaded)
        {
            AVCodec* codec = null;
            if (codecType == AudioCodecType.Alac)
            {
                codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_ALAC);
            }
            else
            {
                codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_AAC);
                if (codec == null)
                    codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_AAC_LATM);
            }

            if (codec != null)
            {
                _ctx = ffmpeg.avcodec_alloc_context3(codec);
                if (_ctx != null)
                {
                    _ctx->sample_rate = SampleRate;
                    var chLayout = new AVChannelLayout();
                    ffmpeg.av_channel_layout_default(&chLayout, Channels);
                    _ctx->ch_layout = chLayout;
                    _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

                    if (codecType == AudioCodecType.Alac)
                    {
                        // 24-byte ALAC magic cookie for AirPlay audio
                        byte[] cookie = new byte[24];
                        BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(0, 4), (uint)FramesPerPacket); // 352
                        cookie[4] = 0; // compatibleVersion
                        cookie[5] = 16; // bitDepth
                        cookie[6] = 40; // pb
                        cookie[7] = 10; // mb
                        cookie[8] = 14; // kb
                        cookie[9] = (byte)Channels; // 2
                        BinaryPrimitives.WriteUInt16BigEndian(cookie.AsSpan(10, 2), 255); // maxRun
                        BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(12, 4), 0); // maxFrameBytes
                        BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(16, 4), 0); // avgBitRate
                        BinaryPrimitives.WriteUInt32BigEndian(cookie.AsSpan(20, 4), (uint)SampleRate); // 44100

                        _ctx->extradata_size = cookie.Length;
                        _ctx->extradata = (byte*)ffmpeg.av_mallocz((ulong)(cookie.Length + 64));
                        Marshal.Copy(cookie, 0, (IntPtr)_ctx->extradata, cookie.Length);

                        var openRes = ffmpeg.avcodec_open2(_ctx, codec, null);
                        if (openRes < 0)
                            Log.Warn(Tag, $"ALAC avcodec_open2 returned {openRes}");
                        else
                            Log.Info(Tag, $"ALAC audio decoder initialized successfully ({SampleRate} Hz, {Channels} ch, spf={FramesPerPacket})");
                    }
                    else
                    {
                        // Configure AAC-ELD extradata (AudioSpecificConfig) for AirPlay mirror audio
                        byte[] asc = { 0xF8, 0xE8, 0x50, 0x00 };
                        _ctx->extradata_size = asc.Length;
                        _ctx->extradata = (byte*)ffmpeg.av_mallocz((ulong)(asc.Length + 64));
                        Marshal.Copy(asc, 0, (IntPtr)_ctx->extradata, asc.Length);

                        var openRes = ffmpeg.avcodec_open2(_ctx, codec, null);
                        if (openRes < 0)
                            Log.Warn(Tag, $"AAC-ELD avcodec_open2 returned {openRes}");
                        else
                            Log.Info(Tag, "AAC-ELD audio decoder initialized successfully");
                    }
                }
            }

            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
        }
        Log.Info(Tag, $"Audio decoder initialized ({CodecType}, {SampleRate} Hz, 16-bit Stereo PCM target)");
    }

    public bool TryDecode(byte[] input, out byte[] pcmOutput)
    {
        pcmOutput = Array.Empty<byte>();
        if (!TryDecode(input, input.Length, out var pooledPcm, out var pcmLength, out var isPooled))
            return false;

        // Copy to a new array for backward compatibility (non-pooled callers)
        pcmOutput = new byte[pcmLength];
        Buffer.BlockCopy(pooledPcm, 0, pcmOutput, 0, pcmLength);
        if (isPooled)
            ArrayPool<byte>.Shared.Return(pooledPcm);
        return true;
    }

    /// <summary>
    /// Decode audio packet into PCM using pooled buffers (zero-allocation hot path).
    /// Caller MUST return pcmOutput to ArrayPool when isPooled is true.
    /// </summary>
    public bool TryDecode(byte[] input, int inputLength, out byte[] pcmOutput, out int pcmLength, out bool isPooled)
    {
        pcmOutput = Array.Empty<byte>();
        pcmLength = 0;
        isPooled = false;
        if (_disposed || _ctx == null || inputLength == 0) return false;

        lock (_lock)
        {
            if (_disposed || _ctx == null || inputLength == 0) return false;

            fixed (byte* p = input)
            {
                _packet->data = p;
                _packet->size = inputLength;

                var send = ffmpeg.avcodec_send_packet(_ctx, _packet);
                if (send < 0 && send != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                {
                    Log.Debug(Tag, $"avcodec_send_packet error {send} (len={inputLength}, hex={Convert.ToHexString(input.AsSpan(0, Math.Min(inputLength, 16)))})");
                    return false;
                }

                var receive = ffmpeg.avcodec_receive_frame(_ctx, _frame);
                if (receive == ffmpeg.AVERROR(ffmpeg.EAGAIN) || receive == ffmpeg.AVERROR_EOF)
                    return false;
                if (receive < 0)
                {
                    Log.Debug(Tag, $"avcodec_receive_frame error {receive}");
                    return false;
                }

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
                        var inLayout = _frame->ch_layout;
                        if (inLayout.nb_channels == 0)
                            ffmpeg.av_channel_layout_default(&inLayout, 2);

                        var inRate = _frame->sample_rate > 0 ? _frame->sample_rate : 44100;
                        ffmpeg.swr_alloc_set_opts2(
                            &swrAlloc,
                            &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, 44100,
                            &inLayout, (AVSampleFormat)_frame->format, inRate,
                            0, null);
                        _swr = swrAlloc;
                        if (_swr != null) ffmpeg.swr_init(_swr);
                    }

                    if (_swr == null) return false;

                    var outSamples = ffmpeg.swr_get_out_samples(_swr, numSamples);
                    if (outSamples <= 0) outSamples = numSamples;

                    var outBytes = outSamples * Channels * (BitsPerSample / 8);
                    // Use ArrayPool instead of new byte[] to eliminate GC pressure
                    pcmOutput = ArrayPool<byte>.Shared.Rent(outBytes);
                    isPooled = true;

                    fixed (byte* dst = pcmOutput)
                    {
                        var dstData = new byte*[1] { dst };
                        var srcData = new byte*[8]
                        {
                            _frame->data[0], _frame->data[1], _frame->data[2], _frame->data[3],
                            _frame->data[4], _frame->data[5], _frame->data[6], _frame->data[7]
                        };

                        fixed (byte** pDst = dstData)
                        fixed (byte** pSrc = srcData)
                        {
                            var converted = ffmpeg.swr_convert(_swr, pDst, outSamples, pSrc, numSamples);
                            if (converted > 0)
                            {
                                pcmLength = converted * Channels * 2;
                                return true;
                            }
                        }
                    }

                    // Decode failed — return pooled buffer
                    ArrayPool<byte>.Shared.Return(pcmOutput);
                    pcmOutput = Array.Empty<byte>();
                    pcmLength = 0;
                    isPooled = false;
                }
                finally
                {
                    ffmpeg.av_frame_unref(_frame);
                }
            }

            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            if (_swr != null) { var s = _swr; ffmpeg.swr_free(&s); _swr = null; }
            if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
            if (_packet != null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
            if (_ctx != null)
            {
                var c = _ctx;
                ffmpeg.avcodec_free_context(&c);
                _ctx = null;
            }
        }

        Log.Info(Tag, "Audio decoder disposed");
    }
}

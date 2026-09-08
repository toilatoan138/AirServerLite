using AirServerLite.Core;
using AirServerLite.Video;
using FFmpeg.AutoGen;

namespace AirServerLite.Audio;

/// <summary>
/// Decodes AirPlay audio packets (AAC-ELD, AAC, or ALAC) into raw 16-bit 44.1kHz Stereo PCM.
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
        if (FFmpegLoader.IsLoaded)
        {
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
        }
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
                    var inLayout = _frame->ch_layout;
                    ffmpeg.swr_alloc_set_opts2(
                        &swrAlloc,
                        &outLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, 44100,
                        &inLayout, (AVSampleFormat)_frame->format, _frame->sample_rate,
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

                    fixed (byte** pDst = dstData)
                    fixed (byte** pSrc = srcData)
                    {
                        var converted = ffmpeg.swr_convert(_swr, pDst, numSamples, pSrc, numSamples);
                        if (converted > 0)
                        {
                            var actualLen = converted * Channels * 2;
                            if (actualLen != pcmOutput.Length)
                                Array.Resize(ref pcmOutput, actualLen);
                            return true;
                        }
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

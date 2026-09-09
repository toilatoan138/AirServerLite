using System.Runtime.InteropServices;
using AirServerLite.Core;
using FFmpeg.AutoGen;

namespace AirServerLite.Android;

/// <summary>
/// Sub-millisecond latency AAC-LC / AAC-ELD audio decoder for Miracast MPEG-TS streams.
/// Uses native FFmpeg libavcodec to unpack audio PES packets into 16-bit stereo PCM.
/// </summary>
public sealed unsafe class AacAudioDecoder : IDisposable
{
    private const string Tag = "aac-decode";

    private AVCodecContext* _ctx;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private bool _disposed;

    public AacAudioDecoder()
    {
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec == null)
            throw new InvalidOperationException("FFmpeg AAC decoder not available");

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (_ctx == null)
            throw new InvalidOperationException("avcodec_alloc_context3 failed for AAC");

        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;

        var rc = ffmpeg.avcodec_open2(_ctx, codec, null);
        if (rc < 0)
            throw new InvalidOperationException($"avcodec_open2 failed for AAC: {rc}");

        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
    }

    public bool TryDecode(ReadOnlySpan<byte> aacData, out byte[] pcm, out int sampleRate)
    {
        pcm = Array.Empty<byte>();
        sampleRate = 48000;

        if (_disposed || aacData.Length == 0 || _ctx == null || _packet == null || _frame == null)
            return false;

        fixed (byte* pData = aacData)
        {
            _packet->data = pData;
            _packet->size = aacData.Length;

            int sendRc = ffmpeg.avcodec_send_packet(_ctx, _packet);
            if (sendRc < 0) return false;

            int recvRc = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (recvRc < 0) return false;

            sampleRate = _frame->sample_rate > 0 ? _frame->sample_rate : 48000;
            int nbSamples = _frame->nb_samples;
            int channels = 2; // target stereo output

            pcm = new byte[nbSamples * channels * 2]; // 16-bit = 2 bytes per sample

            var format = (AVSampleFormat)_frame->format;

            if (format == AVSampleFormat.AV_SAMPLE_FMT_FLTP)
            {
                // Float planar: data[0] is Left, data[1] is Right
                float* lChan = (float*)_frame->data[0];
                float* rChan = _frame->data[1] != null ? (float*)_frame->data[1] : lChan;

                for (int i = 0; i < nbSamples; i++)
                {
                    short l = (short)Math.Clamp((int)(lChan[i] * 32767f), -32768, 32767);
                    short r = (short)Math.Clamp((int)(rChan[i] * 32767f), -32768, 32767);

                    int offset = i * 4;
                    pcm[offset] = (byte)(l & 0xFF);
                    pcm[offset + 1] = (byte)((l >> 8) & 0xFF);
                    pcm[offset + 2] = (byte)(r & 0xFF);
                    pcm[offset + 3] = (byte)((r >> 8) & 0xFF);
                }
            }
            else if (format == AVSampleFormat.AV_SAMPLE_FMT_S16P)
            {
                // Signed 16-bit planar
                short* lChan = (short*)_frame->data[0];
                short* rChan = _frame->data[1] != null ? (short*)_frame->data[1] : lChan;

                for (int i = 0; i < nbSamples; i++)
                {
                    short l = lChan[i];
                    short r = rChan[i];

                    int offset = i * 4;
                    pcm[offset] = (byte)(l & 0xFF);
                    pcm[offset + 1] = (byte)((l >> 8) & 0xFF);
                    pcm[offset + 2] = (byte)(r & 0xFF);
                    pcm[offset + 3] = (byte)((r >> 8) & 0xFF);
                }
            }
            else if (format == AVSampleFormat.AV_SAMPLE_FMT_S16)
            {
                // Signed 16-bit interleaved
                Marshal.Copy((IntPtr)_frame->data[0], pcm, 0, pcm.Length);
            }
            else
            {
                // Fallback direct copy
                int copyBytes = Math.Min(pcm.Length, _frame->linesize[0]);
                if (copyBytes > 0)
                    Marshal.Copy((IntPtr)_frame->data[0], pcm, 0, copyBytes);
            }

            ffmpeg.av_frame_unref(_frame);
            return pcm.Length > 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_packet != null) { var p = _packet; ffmpeg.av_packet_free(&p); _packet = null; }
        if (_ctx != null) { var c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
    }
}

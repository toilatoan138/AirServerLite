using System.Net;
using System.Net.Sockets;
using AirServerLite.AirPlay.Streaming;
using AirServerLite.Audio;
using AirServerLite.Core;
using AirServerLite.Video;

namespace AirServerLite.Android.Miracast;

/// <summary>
/// Listens for Miracast RTP MPEG-TS packets over UDP, demuxes H.264 video NALUs
/// and pipes them straight into the low-latency VideoPipeline.
/// </summary>
public sealed class MiracastRtpReceiver : IDisposable
{
    private const string Tag = "miracast-rtp";
    private readonly int _port;
    private readonly VideoPipeline _videoPipeline;
    private readonly AudioPlayer? _audioPlayer;
    private readonly MpegTsDemuxer _demuxer = new();
    private AacAudioDecoder? _audioDecoder;

    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public MiracastRtpReceiver(int port, VideoPipeline videoPipeline, AudioPlayer? audioPlayer = null)
    {
        _port = port;
        _videoPipeline = videoPipeline;
        _audioPlayer = audioPlayer;
        if (_audioPlayer != null)
        {
            try { _audioDecoder = new AacAudioDecoder(); } catch { }
        }
    }

    public static byte[] ExtractRtpPayload(byte[] packet)
    {
        if (packet.Length < 12) return Array.Empty<byte>();
        int csrcCount = packet[0] & 0x0F;
        int headerLength = 12 + (csrcCount * 4);
        if (packet.Length <= headerLength) return Array.Empty<byte>();

        byte[] payload = new byte[packet.Length - headerLength];
        Buffer.BlockCopy(packet, headerLength, payload, 0, payload.Length);
        return payload;
    }

    public void Start()
    {
        if (_disposed) return;
        _cts = new CancellationTokenSource();
        _udp = new UdpClient(_port);
        Log.Info(Tag, $"Miracast UDP RTP receiver listening on port {_port}");

        Task.Run(() => ReceiveLoopAsync(_cts.Token));
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _udp != null)
        {
            try
            {
                var result = await _udp.ReceiveAsync(ct);
                byte[] payload = ExtractRtpPayload(result.Buffer);
                if (payload.Length == 0) continue;

                var demux = _demuxer.ProcessPackets(payload);
                foreach (var nalu in demux.VideoNalus)
                {
                    bool isKey = (nalu.Length > 4) && ((nalu[4] & 0x1F) == 5 || (nalu[4] & 0x1F) == 7);
                    _videoPipeline.Submit(new VideoPacket
                    {
                        Data = nalu,
                        IsParameterSet = isKey,
                        IsKeyframe = isKey,
                        Timestamp = (ulong)Environment.TickCount64
                    });
                }

                if (_audioPlayer != null && _audioDecoder != null && demux.AudioPackets.Count > 0)
                {
                    foreach (var audioPkt in demux.AudioPackets)
                    {
                        if (_audioDecoder.TryDecode(audioPkt, out var pcm, out var sampleRate))
                        {
                            if (sampleRate != 44100)
                                pcm = Audio.AudioResampler.ResampleStereo16(pcm, sampleRate, 44100);
                            _audioPlayer.PlayPcm(pcm, pcm.Length);
                        }
                    }
                }
            }
            catch when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Warn(Tag, $"RTP receive error: {ex.Message}");
            }
        }
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Close();
        _udp = null;
        Log.Info(Tag, "Miracast RTP receiver stopped");
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _audioDecoder?.Dispose();
        _audioDecoder = null;
    }
}

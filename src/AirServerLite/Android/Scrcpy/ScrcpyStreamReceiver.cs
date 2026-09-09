using System.Buffers.Binary;
using System.Net.Sockets;
using AirServerLite.AirPlay.Streaming;
using AirServerLite.Core;
using AirServerLite.Video;

namespace AirServerLite.Android.Scrcpy;

/// <summary>
/// Receives raw H.264 video packets from scrcpy server running on Android,
/// and streams directly into the low-latency VideoPipeline.
/// </summary>
public sealed class ScrcpyStreamReceiver : IDisposable
{
    private const string Tag = "scrcpy-stream";
    private readonly TcpClient _videoClient;
    private readonly VideoPipeline _pipeline;
    private readonly CancellationTokenSource _cts = new();

    public ushort VideoWidth { get; private set; }
    public ushort VideoHeight { get; private set; }

    public ScrcpyStreamReceiver(TcpClient videoClient, VideoPipeline pipeline)
    {
        _videoClient = videoClient;
        _pipeline = pipeline;
    }

    public void Start()
    {
        Task.Run(() => StreamLoopAsync(_cts.Token));
    }

    private async Task StreamLoopAsync(CancellationToken ct)
    {
        try
        {
            var stream = _videoClient.GetStream();
            byte[] meta = new byte[64 + 4 + 4]; // Device name (64) + Codec (4) + Width/Height (4)
            await stream.ReadExactlyAsync(meta, ct);

            VideoWidth = BinaryPrimitives.ReadUInt16BigEndian(meta.AsSpan(68, 2));
            VideoHeight = BinaryPrimitives.ReadUInt16BigEndian(meta.AsSpan(70, 2));
            Log.Info(Tag, $"Android stream initialized: {VideoWidth}x{VideoHeight}");

            byte[] header = new byte[12]; // PTS (8) + PacketSize (4)
            while (!ct.IsCancellationRequested && _videoClient.Connected)
            {
                await stream.ReadExactlyAsync(header, ct);
                ulong pts = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0, 8));
                int packetSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4));

                byte[] nalu = new byte[packetSize];
                await stream.ReadExactlyAsync(nalu, ct);

                bool isKey = (nalu.Length > 4) && ((nalu[4] & 0x1F) == 5 || (nalu[4] & 0x1F) == 7);
                _pipeline.Submit(new VideoPacket
                {
                    Data = nalu,
                    IsParameterSet = isKey,
                    Timestamp = pts
                });
            }
        }
        catch when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"Scrcpy stream loop error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _videoClient.Close(); } catch { }
    }
}

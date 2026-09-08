using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AirServerLite.Core;

namespace AirServerLite.Audio;

public sealed class AudioPacket
{
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public ushort SequenceNumber { get; init; }
    public uint Timestamp { get; init; }
    public DateTime ArrivedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Receives AirPlay audio streams (type 96 AAC-ELD / ALAC) on ephemeral UDP ports.
/// Handles RTP headers, sequence tracking, and decrypts packets with AudioCipher.
/// </summary>
public sealed class AudioStreamReceiver : IDisposable
{
    private const string Tag = "audio-rx";
    private static readonly byte[] NoDataMarker = { 0x00, 0x68, 0x34, 0x00 };

    private readonly UdpClient _dataSocket;
    private readonly UdpClient _controlSocket;
    private readonly AudioCipher _cipher;
    private readonly CancellationTokenSource _cts = new();
    private Task? _dataLoopTask;
    private Task? _controlLoopTask;
    private bool _disposed;

    public int DataPort { get; }
    public int ControlPort { get; }

    public event Action<AudioPacket>? PacketReady;
    public event Action<string>? Closed;

    public AudioStreamReceiver(IPAddress bindAddress, byte[] aesKey)
    {
        _cipher = new AudioCipher(aesKey);
        _dataSocket = NetUtil.BindEphemeralUdp(bindAddress, out var dataPort);
        _controlSocket = NetUtil.BindEphemeralUdp(bindAddress, out var controlPort);
        DataPort = dataPort;
        ControlPort = controlPort;

        Log.Info(Tag, $"Audio ports listening: data={DataPort} control={ControlPort}");
    }

    public void Start()
    {
        _dataLoopTask = Task.Run(() => DataReceiveLoopAsync(_cts.Token));
        _controlLoopTask = Task.Run(() => ControlReceiveLoopAsync(_cts.Token));
    }

    public static bool ParseRtpHeader(byte[] packet, out ushort seqNum, out uint timestamp, out int payloadOffset, out int payloadLen)
    {
        seqNum = 0;
        timestamp = 0;
        payloadOffset = 0;
        payloadLen = 0;

        if (packet.Length < 12) return false;

        // RTP header: byte 0 is version/padding, byte 1 is payload type (96)
        seqNum = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2));
        timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4, 4));
        payloadOffset = 12;
        payloadLen = packet.Length - 12;
        return true;
    }

    public static bool IsNoDataMarker(byte[] packet)
    {
        if (packet.Length == 12) return true;
        if (packet.Length == 16 && packet[12] == NoDataMarker[0] && packet[13] == NoDataMarker[1] &&
            packet[14] == NoDataMarker[2] && packet[15] == NoDataMarker[3])
        {
            return true;
        }
        return false;
    }

    private async Task DataReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await _dataSocket.ReceiveAsync(ct).ConfigureAwait(false);
                var packet = result.Buffer;

                if (IsNoDataMarker(packet)) continue;

                if (!ParseRtpHeader(packet, out var seqNum, out var timestamp, out var payloadOffset, out var payloadLen))
                    continue;

                if (payloadLen <= 0) continue;

                var decrypted = new byte[payloadLen];
                _cipher.Decrypt(packet, payloadOffset, payloadLen, decrypted, 0);

                PacketReady?.Invoke(new AudioPacket
                {
                    Data = decrypted,
                    SequenceNumber = seqNum,
                    Timestamp = timestamp
                });
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Debug(Tag, "Audio data loop ended: " + ex.Message);
            Closed?.Invoke(ex.Message);
        }
    }

    private async Task ControlReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _controlSocket.ReceiveAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Trace(Tag, "Audio control loop ended: " + ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        _dataSocket.Dispose();
        _controlSocket.Dispose();
        _cipher.Dispose();
        _cts.Dispose();
        Log.Info(Tag, "Audio receiver disposed");
    }
}

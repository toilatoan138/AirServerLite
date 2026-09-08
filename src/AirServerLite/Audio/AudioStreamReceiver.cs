using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using AirServerLite.Core;

namespace AirServerLite.Audio;

public sealed class AudioPacket
{
    public byte[] Data { get; init; } = Array.Empty<byte>();
    /// <summary>Actual valid byte count within Data (may be smaller than Data.Length when pooled).</summary>
    public int DataLength { get; init; }
    /// <summary>True if Data was rented from ArrayPool and must be returned after use.</summary>
    public bool IsPooled { get; init; }
    public ushort SequenceNumber { get; init; }
    public uint Timestamp { get; init; }
    public DateTime ArrivedUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Return the pooled buffer to ArrayPool. Safe to call multiple times.</summary>
    public void ReturnBuffer()
    {
        if (IsPooled && Data != null && Data.Length > 0)
            ArrayPool<byte>.Shared.Return(Data);
    }
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

    public AudioStreamReceiver(IPAddress bindAddress, byte[] aesKey, byte[]? iv = null)
    {
        _cipher = new AudioCipher(aesKey, iv);
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

        // RTP header: byte 0 is version/padding/extension/CC
        seqNum = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2));
        timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4, 4));

        int offset = 12;
        int cc = packet[0] & 0x0F;
        offset += cc * 4;

        if ((packet[0] & 0x10) != 0) // Extension header present
        {
            if (packet.Length < offset + 4) return false;
            ushort extLen = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 2, 2));
            offset += 4 + (extLen * 4);
        }

        int padLen = 0;
        if ((packet[0] & 0x20) != 0) // Padding present
        {
            padLen = packet[^1];
        }

        if (offset + padLen > packet.Length) return false;

        payloadOffset = offset;
        payloadLen = packet.Length - offset - padLen;
        return payloadLen > 0;
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
        // Without these counters the audio path is invisible in the log: a stream that is set
        // up and torn down again looks identical whether it carried a thousand packets or none,
        // and that difference is the whole diagnosis when playback fails on the phone.
        var received = 0L;
        var delivered = 0L;
        var skippedMarker = 0L;
        var skippedHeader = 0L;
        var lastReport = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await _dataSocket.ReceiveAsync(ct).ConfigureAwait(false);
                var packet = result.Buffer;
                received++;

                if ((DateTime.UtcNow - lastReport).TotalSeconds >= 2)
                {
                    Log.Debug(Tag, $"audio rx: {received} pkt, {delivered} delivered, " +
                                   $"{skippedMarker} no-data markers, {skippedHeader} bad headers");
                    lastReport = DateTime.UtcNow;
                }

                if (IsNoDataMarker(packet)) { skippedMarker++; continue; }

                if (!ParseRtpHeader(packet, out var seqNum, out var timestamp, out var payloadOffset, out var payloadLen))
                {
                    skippedHeader++;
                    continue;
                }

                if (payloadLen <= 0) continue;
                delivered++;

                // Use ArrayPool to eliminate per-packet heap allocation (GC pressure → audio stalls)
                var decrypted = ArrayPool<byte>.Shared.Rent(payloadLen);
                _cipher.Decrypt(packet, payloadOffset, payloadLen, decrypted, 0);

                PacketReady?.Invoke(new AudioPacket
                {
                    Data = decrypted,
                    DataLength = payloadLen,
                    IsPooled = true,
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
        finally
        {
            Log.Info(Tag, $"Audio data loop finished: {received} packets received, " +
                          $"{delivered} delivered to the decoder");
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

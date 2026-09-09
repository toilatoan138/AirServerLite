using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using AirServerLite.AirPlay.Crypto;
using AirServerLite.Core;

namespace AirServerLite.AirPlay.Streaming;

/// <summary>An H.264 access unit in Annex-B form, ready for the decoder.</summary>
public sealed class VideoPacket
{
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public ulong Timestamp { get; init; }
    public bool IsParameterSet { get; init; }
    public DateTime ArrivedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Receives the mirroring video stream on its own TCP port.
///
/// Wire format: a 128-byte header followed by <c>payloadSize</c> bytes. Verified against the
/// reference receive loop in UxPlay's lib/raop_rtp_mirror.c:
///   [0..3]   payload size, uint32 little-endian
///   [4]      payload type - a SINGLE byte, not a 16-bit value:
///              0x00 = encrypted H.264 video data (VCL NAL)
///              0x01 = codec data (SPS/PPS) - sent in the clear
///              0x02 = legacy keepalive, no payload
///              0x05 = periodic "streaming report", unencrypted
///   [5]      for type 0x00 only: 0x00 = non-IDR frame, 0x10 = IDR (keyframe) - a flag, not
///            part of the type. Reading bytes [4..5] together as one 16-bit value (an earlier
///            version of this file did exactly that) misreads every IDR frame as type 0x1000
///            and silently drops it - which looks like "no keyframes ever arrive" rather than
///            a framing bug, and is far harder to notice than it should be.
///   [6..7]   further flags, not currently used by this receiver
///   [8..15]  timestamp, uint64 little-endian
///   [16..127] type-dependent; for type 1 the avcC record starts at offset 6 of the payload
///
/// Type 0 payloads carry length-prefixed NAL units (4-byte big-endian length, AVCC style),
/// which we rewrite to Annex-B start codes because that is what a plain FFmpeg h264 decoder
/// wants without an extradata round-trip.
/// </summary>
public sealed class MirrorStreamReceiver : IDisposable
{
    private const string Tag = "mirror";
    private const int HeaderSize = 128;
    private static readonly byte[] StartCode = { 0x00, 0x00, 0x00, 0x01 };

    private readonly TcpListener _listener;
    private readonly MirrorCipher _cipher;
    private readonly CancellationTokenSource _cts = new();
    private Task? _worker;
    private bool _disposed;
    private bool _dumpedFirstPacket;
    private bool _dumpedFirstPlaintext;
    private byte[]? _cachedParameterSets;
    private bool _prependParameterSets;

    public int Port { get; }

    /// <summary>Raised on the receive thread for every decodable access unit.</summary>
    public event Action<VideoPacket>? PacketReady;

    /// <summary>Raised when the phone tears the connection down or it fails.</summary>
    public event Action<string>? Closed;

    public MirrorStreamReceiver(IPAddress bindAddress, MirrorCipher cipher)
    {
        _cipher = cipher;
        _listener = NetUtil.BindEphemeralTcp(bindAddress, out var port);
        Port = port;
        Log.Info(Tag, $"Mirror data port listening on {bindAddress}:{Port}");
    }

    public void Start()
    {
        _worker = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);

            // Nagle would batch our (read-only) side pointlessly and adds jitter on some
            // stacks; the whole point of this path is latency.
            client.NoDelay = true;
            client.ReceiveBufferSize = 1 << 20;

            Log.Info(Tag, "Mirror stream connected from " + client.Client.RemoteEndPoint);

            client.NoDelay = true;
            client.ReceiveBufferSize = 4 * 1024 * 1024;

            await using var stream = client.GetStream();
            await ReadLoopAsync(stream, ct).ConfigureAwait(false);

            Closed?.Invoke("stream ended");
        }
        catch (OperationCanceledException)
        {
            Closed?.Invoke("cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(Tag, "Mirror receive loop failed", ex);
            Closed?.Invoke(ex.Message);
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        var packets = 0L;
        var bytes = 0L;
        var lastReport = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactAsync(stream, header, 0, HeaderSize, ct).ConfigureAwait(false))
            {
                Log.Info(Tag, "Peer closed the mirror connection");
                return;
            }

            var payloadSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4));
            // Single byte. header[5] is an IDR/non-IDR flag for type 0 only, NOT part of the
            // type - reading both bytes together misreads every keyframe as type 0x1000.
            var payloadType = header[4];
            var timestamp = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8, 8));

            if (payloadSize < 0 || payloadSize > 16 * 1024 * 1024)
                throw new InvalidDataException(
                    $"Implausible mirror payload size {payloadSize} (type {payloadType}) - " +
                    "the AES keystream or the framing has desynchronised");

            if (payloadSize == 0)
            {
                Log.Info(Tag, $"Empty packet type={payloadType}, flags=0x{header[5]:X2}");
                continue;
            }

            var payload = new byte[payloadSize];
            if (!await ReadExactAsync(stream, payload, 0, payloadSize, ct).ConfigureAwait(false))
            {
                Log.Warn(Tag, "Connection closed mid-payload");
                return;
            }

            packets++;
            bytes += payloadSize + HeaderSize;

            switch (payloadType)
            {
                case 0:
                    // One-shot full dump of the very first video packet, ciphertext and
                    // plaintext, so a failing decrypt can be reproduced and tested offline
                    // against the exact bytes a real iPhone sent, instead of guessing.
                    if (!_dumpedFirstPacket)
                    {
                        _dumpedFirstPacket = true;
                        Log.Debug(Tag, $"first video packet ciphertext ({payloadSize}B): " +
                                       Convert.ToHexString(payload));
                    }

                    _cipher.DecryptPacket(payload, 0, payloadSize);

                    if (!_dumpedFirstPlaintext)
                    {
                        _dumpedFirstPlaintext = true;
                        Log.Debug(Tag, $"first video packet plaintext ({payloadSize}B): " +
                                       Convert.ToHexString(payload));
                    }

                    var annexB = ConvertAvccToAnnexB(payload);
                    if (annexB.Length > 0)
                    {
                        // In AirPlay mirror streams (as referenced in UxPlay raop_rtp_mirror.c L434-444),
                        // IDR keyframes require SPS/PPS prepended so the decoder receives a self-contained
                        // Annex-B access unit and can immediately establish sequence parameters.
                        var isKeyframe = (header[5] & 0x10) != 0 || (annexB.Length > 4 && (annexB[4] & 0x1F) == 5);
                        if (_cachedParameterSets != null && (_prependParameterSets || isKeyframe))
                        {
                            _prependParameterSets = false;
                            var combined = new byte[_cachedParameterSets.Length + annexB.Length];
                            Buffer.BlockCopy(_cachedParameterSets, 0, combined, 0, _cachedParameterSets.Length);
                            Buffer.BlockCopy(annexB, 0, combined, _cachedParameterSets.Length, annexB.Length);
                            annexB = combined;
                        }

                        PacketReady?.Invoke(new VideoPacket { Data = annexB, Timestamp = timestamp });
                    }
                    break;

                case 1:
                    Log.Info(Tag, $"Received type 1 codec packet ({payloadSize}B, flags=0x{header[5]:X2}): " +
                                  Log.Hex(payload, Math.Min(payload.Length, 64)));
                    var paramSets = ParseCodecData(payload);
                    if (paramSets.Length > 0)
                    {
                        Log.Info(Tag, $"Extracted parameter sets ({paramSets.Length} bytes Annex-B): " +
                                      Log.Hex(paramSets, Math.Min(paramSets.Length, 32)));
                        _cachedParameterSets = paramSets;
                        _prependParameterSets = true;
                    }
                    else
                    {
                        Log.Warn(Tag, $"Failed to extract parameter sets from type 1 payload ({payload.Length}B): " +
                                      Convert.ToHexString(payload));
                    }
                    break;

                case 2:
                    Log.Trace(Tag, "heartbeat");
                    break;

                default:
                    Log.Debug(Tag, $"Received mirror packet type {payloadType} ({payloadSize} bytes, flags=0x{header[5]:X2}): " +
                                  Log.Hex(payload, Math.Min(payload.Length, 32)));
                    break;
            }

            if ((DateTime.UtcNow - lastReport).TotalSeconds >= 5)
            {
                var secs = (DateTime.UtcNow - lastReport).TotalSeconds;
                Log.Debug(Tag, $"{packets / secs:F1} pkt/s, {bytes / secs / 1024 / 1024:F2} MiB/s");
                packets = 0;
                bytes = 0;
                lastReport = DateTime.UtcNow;
            }
        }
    }

    private static async Task<bool> ReadExactAsync(NetworkStream s, byte[] buf, int offset, int count,
                                                   CancellationToken ct)
    {
        var got = 0;
        while (got < count)
        {
            var n = await s.ReadAsync(buf.AsMemory(offset + got, count - got), ct).ConfigureAwait(false);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }

    /// <summary>
    /// Rewrite 4-byte-length-prefixed NAL units into Annex-B start codes, in place-ish.
    /// A malformed length here means the decrypt went wrong, so we fail loudly rather than
    /// feeding the decoder noise.
    /// </summary>
    private static byte[] ConvertAvccToAnnexB(byte[] payload)
    {
        var output = new System.IO.MemoryStream(payload.Length + 64);
        var pos = 0;

        while (pos + 4 <= payload.Length)
        {
            var nalLen = (int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(pos, 4));
            pos += 4;

            if (nalLen <= 0 || pos + nalLen > payload.Length)
            {
                Log.Warn(Tag, $"Bad NAL length {nalLen} at offset {pos} of {payload.Length} - dropping packet");
                return Array.Empty<byte>();
            }

            output.Write(StartCode, 0, 4);
            output.Write(payload, pos, nalLen);
            pos += nalLen;
        }

        return output.ToArray();
    }

    /// <summary>
    /// Extract parameter sets (SPS/PPS for H.264 or VPS/SPS/PPS for HEVC) from type 1 payload
    /// and emit them as Annex-B (prefixed by 00 00 00 01 start codes).
    /// Handles:
    ///   - Standard AVCC starting at offset 0: payload[0] == 1
    ///   - Direct UxPlay layout: sps_len at offset 6, pps_len at offset sps_len + 9
    ///   - Prefixed AVCC: payload[6] == 1
    ///   - HEVC: payload contains 'hvc1' at offset 4
    ///   - Raw Annex-B: payload already starts with 00 00 00 01
    /// </summary>
    public static byte[] ParseCodecData(byte[] payload)
    {
        if (payload == null || payload.Length < 4)
            return Array.Empty<byte>();

        try
        {
            // 1. Raw Annex-B check: if payload already begins with start code
            if (payload.Length >= 4 && payload[0] == 0 && payload[1] == 0 &&
                (payload[2] == 1 || (payload[2] == 0 && payload[3] == 1)))
            {
                return (byte[])payload.Clone();
            }

            // 2. HEVC check: 'hvc1' at offset 4 (as in UxPlay raop_rtp_mirror.c L628)
            if (payload.Length >= 8 &&
                payload[4] == 0x68 && payload[5] == 0x76 && payload[6] == 0x63 && payload[7] == 0x31)
            {
                var hevcSets = ParseHevcCodecData(payload);
                if (hevcSets.Length > 0) return hevcSets;
            }

            // 3. Standard AVCC at offset 0: payload[0] == 1
            if (payload[0] == 1 && payload.Length >= 7)
            {
                var parsed = ParseAvccFromOffset(payload, 0);
                if (parsed.Length > 0) return parsed;
            }

            // 4. Direct UxPlay layout: sps_size at payload[6..7]
            if (payload.Length >= 12)
            {
                var spsLen = (int)BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(6, 2));
                if (spsLen > 0 && 8 + spsLen < payload.Length)
                {
                    // Check if NAL at offset 8 has SPS nal_unit_type 7: (payload[8] & 0x1F) == 7
                    if ((payload[8] & 0x1F) == 7)
                    {
                        var ppsOffset = spsLen + 9;
                        if (ppsOffset + 2 <= payload.Length)
                        {
                            var ppsLen = (int)BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(ppsOffset, 2));
                            var ppsDataOffset = spsLen + 11;
                            if (ppsLen > 0 && ppsDataOffset + ppsLen <= payload.Length)
                            {
                                var output = new MemoryStream(spsLen + ppsLen + 16);
                                output.Write(StartCode, 0, 4);
                                output.Write(payload, 8, spsLen);
                                output.Write(StartCode, 0, 4);
                                output.Write(payload, ppsDataOffset, ppsLen);
                                return output.ToArray();
                            }
                        }
                    }
                }
            }

            // 5. Prefixed AVCC: payload[6] == 1
            if (payload.Length >= 13 && payload[6] == 1)
            {
                var parsed = ParseAvccFromOffset(payload, 6);
                if (parsed.Length > 0) return parsed;
            }

            // Fallback: attempt offset 0 without strict version check
            return ParseAvccFromOffset(payload, 0);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, "Failed to parse codec data: " + Log.Hex(payload, 32), ex);
            return Array.Empty<byte>();
        }
    }

    private static byte[] ParseAvccFromOffset(byte[] payload, int offset)
    {
        if (payload.Length < offset + 7) return Array.Empty<byte>();

        var p = offset + 5; // skip version(1), profile(1), compat(1), level(1), lengthSizeMinusOne(1)
        var spsCount = payload[p++] & 0x1F;
        if (spsCount == 0 || spsCount > 32) return Array.Empty<byte>();

        var output = new MemoryStream(payload.Length);

        for (var i = 0; i < spsCount; i++)
        {
            if (p + 2 > payload.Length) return output.ToArray();
            var len = (int)BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(p, 2));
            p += 2;
            if (len <= 0 || p + len > payload.Length) return output.ToArray();
            output.Write(StartCode, 0, 4);
            output.Write(payload, p, len);
            p += len;
        }

        if (p >= payload.Length) return output.ToArray();
        var ppsCount = payload[p++];
        for (var i = 0; i < ppsCount; i++)
        {
            if (p + 2 > payload.Length) break;
            var len = (int)BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(p, 2));
            p += 2;
            if (len <= 0 || p + len > payload.Length) break;
            output.Write(StartCode, 0, 4);
            output.Write(payload, p, len);
            p += len;
        }

        return output.ToArray();
    }

    private static byte[] ParseHevcCodecData(byte[] payload)
    {
        const int hevcOffset = 0x75;
        if (payload.Length < hevcOffset + 15) return Array.Empty<byte>();

        var ptr = hevcOffset;
        // Check VPS marker: 0xA0, 0x00, 0x01, 0x00
        if (payload[ptr] != 0xA0 || payload[ptr + 1] != 0x00 ||
            payload[ptr + 2] != 0x01 || payload[ptr + 3] != 0x00)
            return Array.Empty<byte>();

        var vpsSize = (int)BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(ptr + 3, 2));
        ptr += 5;
        if (vpsSize <= 0 || ptr + vpsSize > payload.Length) return Array.Empty<byte>();
        var vpsOffset = ptr;
        ptr += vpsSize;

        // Check SPS marker: 0xA1, 0x00, 0x01, 0x00
        if (ptr + 5 > payload.Length || payload[ptr] != 0xA1) return Array.Empty<byte>();
        var spsSize = (int)BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(ptr + 3, 2));
        ptr += 5;
        if (spsSize <= 0 || ptr + spsSize > payload.Length) return Array.Empty<byte>();
        var spsOffset = ptr;
        ptr += spsSize;

        // Check PPS marker: 0xA2, 0x00, 0x01, 0x00
        if (ptr + 5 > payload.Length || payload[ptr] != 0xA2) return Array.Empty<byte>();
        var ppsSize = (int)BinaryPrimitives.ReadInt16BigEndian(payload.AsSpan(ptr + 3, 2));
        ptr += 5;
        if (ppsSize <= 0 || ptr + ppsSize > payload.Length) return Array.Empty<byte>();
        var ppsOffset = ptr;

        var output = new MemoryStream(vpsSize + spsSize + ppsSize + 16);
        output.Write(StartCode, 0, 4);
        output.Write(payload, vpsOffset, vpsSize);
        output.Write(StartCode, 0, 4);
        output.Write(payload, spsOffset, spsSize);
        output.Write(StartCode, 0, 4);
        output.Write(payload, ppsOffset, ppsSize);
        return output.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _worker?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
        _cipher.Dispose();
        Log.Info(Tag, "Mirror receiver disposed");
    }
}

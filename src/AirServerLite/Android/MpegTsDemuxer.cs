using System.Buffers.Binary;
using System.IO;

namespace AirServerLite.Android;

public record DemuxResult(List<byte[]> VideoNalus, List<byte[]> AudioPackets);

/// <summary>
/// Lightweight, zero-allocation-focused MPEG-2 Transport Stream demuxer.
/// Miracast / Wi-Fi Display streams 188-byte TS packets containing H.264 PES video
/// and AAC/LPCM audio over UDP RTP.
/// </summary>
public sealed class MpegTsDemuxer
{
    public const int TsPacketSize = 188;
    public const byte SyncByte = 0x47;

    private readonly ushort _videoPid;
    private readonly ushort _audioPid;

    private readonly MemoryStream _videoPesBuffer = new();
    private readonly MemoryStream _audioPesBuffer = new();
    private bool _inVideoPes;
    private bool _inAudioPes;

    public MpegTsDemuxer(ushort videoPid = 0x100, ushort audioPid = 0x101)
    {
        _videoPid = videoPid;
        _audioPid = audioPid;
    }

    public DemuxResult ProcessPackets(ReadOnlySpan<byte> buffer)
    {
        var videoNalus = new List<byte[]>();
        var audioPackets = new List<byte[]>();

        int offset = 0;
        while (offset + TsPacketSize <= buffer.Length)
        {
            if (buffer[offset] != SyncByte)
            {
                // Resync
                int nextSync = buffer.Slice(offset).IndexOf(SyncByte);
                if (nextSync < 0) break;
                offset += nextSync;
                if (offset + TsPacketSize > buffer.Length) break;
            }

            var packet = buffer.Slice(offset, TsPacketSize);
            offset += TsPacketSize;

            bool payloadUnitStart = (packet[1] & 0x40) != 0;
            ushort pid = (ushort)(((packet[1] & 0x1F) << 8) | packet[2]);
            byte adaptationControl = (byte)((packet[3] >> 4) & 0x03);

            if (adaptationControl == 0x00 || adaptationControl == 0x02)
            {
                // No payload in this packet
                continue;
            }

            int payloadOffset = 4;
            if (adaptationControl == 0x03)
            {
                byte adaptationLength = packet[4];
                payloadOffset = 5 + adaptationLength;
                if (payloadOffset >= TsPacketSize) continue;
            }

            var payload = packet.Slice(payloadOffset);

            if (pid == _videoPid)
            {
                if (payloadUnitStart)
                {
                    FlushVideoPes(videoNalus);
                    _inVideoPes = true;
                }

                if (_inVideoPes)
                {
                    _videoPesBuffer.Write(payload);
                }
            }
            else if (pid == _audioPid)
            {
                if (payloadUnitStart)
                {
                    FlushAudioPes(audioPackets);
                    _inAudioPes = true;
                }

                if (_inAudioPes)
                {
                    _audioPesBuffer.Write(payload);
                }
            }
        }

        // Also flush any completed or current PES if stream ends
        FlushVideoPes(videoNalus);
        FlushAudioPes(audioPackets);

        return new DemuxResult(videoNalus, audioPackets);
    }

    private void FlushVideoPes(List<byte[]> videoNalus)
    {
        if (_videoPesBuffer.Length < 9)
        {
            _videoPesBuffer.SetLength(0);
            return;
        }

        byte[] data = _videoPesBuffer.ToArray();
        _videoPesBuffer.SetLength(0);

        // Verify PES prefix: 00 00 01
        if (data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x01)
        {
            int headerDataLength = data[8];
            int esOffset = 9 + headerDataLength;
            if (esOffset < data.Length)
            {
                byte[] esPayload = new byte[data.Length - esOffset];
                Array.Copy(data, esOffset, esPayload, 0, esPayload.Length);
                videoNalus.Add(esPayload);
            }
        }
    }

    private void FlushAudioPes(List<byte[]> audioPackets)
    {
        if (_audioPesBuffer.Length < 9)
        {
            _audioPesBuffer.SetLength(0);
            return;
        }

        byte[] data = _audioPesBuffer.ToArray();
        _audioPesBuffer.SetLength(0);

        if (data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x01)
        {
            int headerDataLength = data[8];
            int esOffset = 9 + headerDataLength;
            if (esOffset < data.Length)
            {
                byte[] esPayload = new byte[data.Length - esOffset];
                Array.Copy(data, esOffset, esPayload, 0, esPayload.Length);
                audioPackets.Add(esPayload);
            }
        }
    }
}

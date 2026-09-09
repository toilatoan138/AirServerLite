using AirServerLite.Android;
using Xunit;

namespace AirServerLite.Tests.Android;

public class MpegTsDemuxerTests
{
    [Fact]
    public void Demux_InvalidPacketLength_IgnoresGracefully()
    {
        var demuxer = new MpegTsDemuxer();
        var data = new byte[100]; // TS packet must be 188 bytes
        var result = demuxer.ProcessPackets(data);
        Assert.Empty(result.VideoNalus);
        Assert.Empty(result.AudioPackets);
    }

    [Fact]
    public void Demux_ValidH264PesPacket_ExtractsNalu()
    {
        var demuxer = new MpegTsDemuxer(videoPid: 0x100);
        
        // Build a 188-byte TS packet containing start of H.264 PES
        byte[] tsPacket = new byte[188];
        tsPacket[0] = 0x47; // Sync byte
        tsPacket[1] = 0x41; // Payload unit start indicator = 1, PID high = 0x01
        tsPacket[2] = 0x00; // PID low = 0x00 -> PID = 0x100
        tsPacket[3] = 0x10; // Adaptation field control = 01 (payload only), counter = 0

        // PES header: 00 00 01 E0 (video stream 0), length = 10, header flags = 80 00 00
        byte[] pesHeader = new byte[] {
            0x00, 0x00, 0x01, 0xE0, // PES start code
            0x00, 0x0A,             // PES packet length (10 bytes)
            0x80, 0x00, 0x00        // PES header data length = 0
        };
        Array.Copy(pesHeader, 0, tsPacket, 4, pesHeader.Length);

        // H.264 Annex-B NALU: 00 00 00 01 05 (IDR frame) + dummy payload
        byte[] nalu = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x05, 0x88, 0x84, 0x21 };
        Array.Copy(nalu, 0, tsPacket, 4 + pesHeader.Length, nalu.Length);

        var result = demuxer.ProcessPackets(tsPacket);
        Assert.Single(result.VideoNalus);
        Assert.Equal(0x05, result.VideoNalus[0][4] & 0x1F); // IDR slice
    }
}

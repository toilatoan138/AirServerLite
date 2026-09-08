using AirServerLite.Audio;
using Xunit;

namespace AirServerLite.Tests.Audio;

public class AudioStreamReceiverTests
{
    [Fact]
    public void ParseRtpPacket_ValidAudioPacket_ExtractsHeaderAndPayload()
    {
        var packet = new byte[28];
        packet[0] = 0x80;
        packet[1] = 0x60; // type 96
        packet[2] = 0x00; packet[3] = 0x2A; // seqnum = 42
        packet[4] = 0x00; packet[5] = 0x01; packet[6] = 0x51; packet[7] = 0x80; // timestamp = 86400
        // bytes 8..11 = ssrc
        for (int i = 12; i < 28; i++) packet[i] = (byte)i;

        var parsed = AudioStreamReceiver.ParseRtpHeader(packet, out var seqNum, out var timestamp, out var payloadOffset, out var payloadLen);
        Assert.True(parsed);
        Assert.Equal(42, seqNum);
        Assert.Equal(86400u, timestamp);
        Assert.Equal(12, payloadOffset);
        Assert.Equal(16, payloadLen);
    }

    [Fact]
    public void ParseRtpPacket_NoDataMarker_IdentifiedAsNoData()
    {
        var packet = new byte[16];
        packet[0] = 0x80;
        packet[1] = 0x60;
        // Marker 0x00 0x68 0x34 0x00 at offset 12
        packet[12] = 0x00; packet[13] = 0x68; packet[14] = 0x34; packet[15] = 0x00;

        Assert.True(AudioStreamReceiver.IsNoDataMarker(packet));
    }
}

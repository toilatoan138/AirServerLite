using AirServerLite.Android.Miracast;
using Xunit;

namespace AirServerLite.Tests.Android;

public class MiracastSessionTests
{
    [Fact]
    public void HandleM1_OptionsRequest_ReturnsSupportedMethods()
    {
        var session = new MiracastSession(null!, 19998);
        string request = "OPTIONS * RTSP/1.0\r\nCSeq: 1\r\nRequire: org.wfa.wfd1.0\r\n\r\n";
        string response = session.HandleRtspRequest(request);

        Assert.Contains("RTSP/1.0 200 OK", response);
        Assert.Contains("CSeq: 1", response);
        Assert.Contains("Public:", response);
        Assert.Contains("org.wfa.wfd1.0", response);
    }

    [Fact]
    public void HandleM3_GetParameterRequest_ReturnsVideoAndAudioFormats()
    {
        var session = new MiracastSession(null!, 19998);
        string request = "GET_PARAMETER rtsp://localhost/wfd1.0 RTSP/1.0\r\nCSeq: 2\r\nContent-Type: text/parameters\r\nContent-Length: 35\r\n\r\nwfd_video_formats\r\nwfd_audio_codecs\r\n";
        string response = session.HandleRtspRequest(request);

        Assert.Contains("RTSP/1.0 200 OK", response);
        Assert.Contains("wfd_video_formats:", response);
        Assert.Contains("wfd_audio_codecs: LPCM", response);
        Assert.Contains("wfd_client_rtpports: RTP/AVP/UDP;unicast 19998", response);
    }
}

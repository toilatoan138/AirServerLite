using System.Net;
using System.Text;
using AirServerLite.Discovery;
using Xunit;

namespace AirServerLite.Tests.Discovery;

public class DialServerTests
{
    [Theory]
    [InlineData("urn:dial-multiscreen-org:service:dial:1", true)]
    [InlineData("URN:DIAL-MULTISCREEN-ORG:SERVICE:DIAL:1", true)]
    [InlineData("ssdp:all", true)]
    [InlineData("upnp:rootdevice", true)]
    [InlineData("urn:schemas-upnp-org:device:tvdevice:1", true)]
    [InlineData("urn:schemas-upnp-org:service:ContentDirectory:1", false)]
    [InlineData("invalid:urn", false)]
    public void IsMatchSearchTarget_CorrectlyFiltersTargets(string target, bool expected)
    {
        var result = DialServer.IsMatchSearchTarget(target);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildDeviceDescriptionXml_ContainsRequiredDialFields()
    {
        var xml = DialServer.BuildDeviceDescriptionXml("AirServer-LITE (YouTube)", "12345678-1234-1234-1234-123456789abc");

        Assert.Contains("<friendlyName>AirServer-LITE (YouTube)</friendlyName>", xml);
        Assert.Contains("<serviceType>urn:dial-multiscreen-org:service:dial:1</serviceType>", xml);
        Assert.Contains("<UDN>uuid:12345678-1234-1234-1234-123456789abc</UDN>", xml);
        Assert.Contains("<controlURL>/apps</controlURL>", xml);
    }

    [Fact]
    public void BuildAppStatusXml_Running_IncludesRunLink()
    {
        var xml = DialServer.BuildAppStatusXml(isRunning: true);
        Assert.Contains("<state>running</state>", xml);
        Assert.Contains(@"<link rel=""run"" href=""run""/>", xml);
    }

    [Fact]
    public void BuildAppStatusXml_Stopped_DoesNotIncludeRunLink()
    {
        var xml = DialServer.BuildAppStatusXml(isRunning: false);
        Assert.Contains("<state>stopped</state>", xml);
        Assert.DoesNotContain(@"<link rel=""run""", xml);
    }

    [Fact]
    public void BuildSsdpResponse_FormatsStandardHeaders()
    {
        var ip = IPAddress.Parse("192.168.1.50");
        var resp = DialServer.BuildSsdpResponse(ip, 8088, "my-uuid", DialServer.DialServiceUrn);

        Assert.StartsWith("HTTP/1.1 200 OK", resp);
        Assert.Contains("LOCATION: http://192.168.1.50:8088/dd.xml", resp);
        Assert.Contains("ST: urn:dial-multiscreen-org:service:dial:1", resp);
        Assert.Contains("USN: uuid:my-uuid::urn:dial-multiscreen-org:service:dial:1", resp);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=30s", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("invalid-not-youtube", null)]
    [InlineData("", null)]
    public void ExtractVideoId_ParsesVariousFormats(string input, string? expected)
    {
        var result = DialServer.ExtractVideoId(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ParseLaunchData_WithFormEncodedParams_ExtractsFields()
    {
        var body = "v=xb5bPZBK6nQ&pairingCode=lounge-1234&t=45.5";
        var args = DialServer.ParseLaunchData(body);

        Assert.Equal("xb5bPZBK6nQ", args.VideoId);
        Assert.Equal("lounge-1234", args.PairingCode);
        Assert.Equal(45.5f, args.StartSeconds);
    }

    [Fact]
    public void ParseLaunchData_WithRawUrl_ExtractsVideoId()
    {
        var body = "https://www.youtube.com/watch?v=xb5bPZBK6nQ";
        var args = DialServer.ParseLaunchData(body);

        Assert.Equal("xb5bPZBK6nQ", args.VideoId);
        Assert.Null(args.PairingCode);
        Assert.Equal(0f, args.StartSeconds);
    }

    [Fact]
    public void ParseLaunchData_WithPairingCodeOnly_ExtractsPairingCode()
    {
        var body = "pairingCode=lounge-screen-999";
        var args = DialServer.ParseLaunchData(body);

        Assert.Null(args.VideoId);
        Assert.Equal("lounge-screen-999", args.PairingCode);
        Assert.Equal(0f, args.StartSeconds);
    }

    [Fact]
    public void ParseLaunchData_WithEmptyBody_ReturnsEmptyArgs()
    {
        var args = DialServer.ParseLaunchData("");
        Assert.Null(args.VideoId);
        Assert.Null(args.PairingCode);
        Assert.Equal(0f, args.StartSeconds);
    }

    [Theory]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    public void ExtractVideoId_HandlesSubdomains(string url, string expectedId)
    {
        var result = DialServer.ExtractVideoId(url);
        Assert.Equal(expectedId, result);
    }

    [Fact]
    public void DialServer_Lifecycle_StartsAndStopsCleanly()
    {
        var dial = new DialServer(IPAddress.Loopback, "Test TV", "test-uuid-12345", httpPort: 18089);
        Assert.False(dial.IsRunning);

        dial.Start();
        Assert.True(dial.IsRunning);

        dial.SetAppRunning(true);
        var runningXml = DialServer.BuildAppStatusXml(true);
        Assert.Contains("<state>running</state>", runningXml);

        dial.SetAppRunning(false);
        var stoppedXml = DialServer.BuildAppStatusXml(false);
        Assert.Contains("<state>stopped</state>", stoppedXml);

        dial.Stop();
        Assert.False(dial.IsRunning);
        dial.Dispose();
    }

    [Fact]
    public async Task DialServer_HttpEndpoints_RespondCorrectlyAsync()
    {
        var port = 18088;
        using var dial = new DialServer(IPAddress.Loopback, "Test TV", "test-uuid-12345", httpPort: port);
        dial.Start();

        using var client = new System.Net.Http.HttpClient();

        // 1. GET /dd.xml
        var ddResp = await client.GetAsync($"http://127.0.0.1:{port}/dd.xml");
        Assert.Equal(HttpStatusCode.OK, ddResp.StatusCode);
        var ddXml = await ddResp.Content.ReadAsStringAsync();
        Assert.Contains("Test TV", ddXml);
        Assert.True(ddResp.Headers.Contains("Application-URL"));

        // 2. GET /apps/YouTube (stopped)
        var statusResp = await client.GetAsync($"http://127.0.0.1:{port}/apps/YouTube");
        Assert.Equal(HttpStatusCode.OK, statusResp.StatusCode);
        var statusXml = await statusResp.Content.ReadAsStringAsync();
        Assert.Contains("<state>stopped</state>", statusXml);

        // 3. POST /apps/YouTube
        YouTubeCastLaunchArgs? receivedArgs = null;
        dial.YouTubeCastRequested += args => receivedArgs = args;

        var postContent = new System.Net.Http.FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("v", "dQw4w9WgXcQ")
        });
        var postResp = await client.PostAsync($"http://127.0.0.1:{port}/apps/YouTube", postContent);
        Assert.Equal(HttpStatusCode.Created, postResp.StatusCode);
        Assert.NotNull(postResp.Headers.Location);
        Assert.Contains("/apps/YouTube/run", postResp.Headers.Location.ToString());
        Assert.NotNull(receivedArgs);
        Assert.Equal("dQw4w9WgXcQ", receivedArgs.VideoId);

        // 4. GET /apps/YouTube (now running)
        var runningResp = await client.GetAsync($"http://127.0.0.1:{port}/apps/YouTube");
        var runningXml = await runningResp.Content.ReadAsStringAsync();
        Assert.Contains("<state>running</state>", runningXml);

        dial.Stop();
    }

    [Fact]
    public void BuildSsdpNotify_WithCustomTarget_FormatsTargetAndUsnCorrectly()
    {
        var ip = IPAddress.Parse("192.168.1.50");
        var notify = DialServer.BuildSsdpNotify(ip, 8088, "my-uuid-1234", "ssdp:alive", "upnp:rootdevice");

        Assert.StartsWith("NOTIFY * HTTP/1.1", notify);
        Assert.Contains("NT: upnp:rootdevice", notify);
        Assert.Contains("USN: uuid:my-uuid-1234::upnp:rootdevice", notify);
        Assert.Contains("LOCATION: http://192.168.1.50:8088/dd.xml", notify);
    }

    [Fact]
    public void BuildDeviceDescriptionXml_IncludesGoogleManufacturerAndTvModel()
    {
        var xml = DialServer.BuildDeviceDescriptionXml("AirServer-LITE (YouTube)", "test-uuid-999");
        Assert.Contains("<manufacturer>Google Inc.</manufacturer>", xml);
        Assert.Contains("<modelName>YouTube on TV</modelName>", xml);
    }
}

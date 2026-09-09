using AirServerLite.Discovery;
using Xunit;

namespace AirServerLite.Tests.Discovery;

public class AndroidDiscoveryTests
{
    [Fact]
    public void BuildMiracastTxtRecord_ContainsRequiredWfdTags()
    {
        var records = MdnsAdvertiser.BuildMiracastTxtRecords("AirServerLite-PC");
        Assert.Contains(records, r => r.StartsWith("wfd-id="));
        Assert.Contains(records, r => r.StartsWith("wfd-version="));
    }

    [Fact]
    public void BuildGoogleCastTxtRecord_ContainsModelAndFriendlyName()
    {
        var records = MdnsAdvertiser.BuildGoogleCastTxtRecords("AirServerLite-PC", "dummy-guid");
        Assert.Contains(records, r => r == "md=Chromecast");
        Assert.Contains(records, r => r == "fn=AirServerLite-PC");
    }
}

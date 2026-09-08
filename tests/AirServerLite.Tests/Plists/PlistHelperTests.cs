using AirServerLite.AirPlay;
using Claunia.PropertyList;
using Xunit;

namespace AirServerLite.Tests.Plists;

public class PlistHelperTests
{
    [Fact]
    public void PlistHelper_ParseAndBinaryRoundTrip_PreservesFields()
    {
        var dict = new NSDictionary
        {
            { "clientProcName", "YouTube" },
            { "Start-Position-Seconds", 12.5 }
        };

        var binary = PlistHelper.ToBinary(dict);
        Assert.NotNull(binary);
        Assert.True(binary.Length > 0);

        var parsed = PlistHelper.Parse(binary);
        Assert.NotNull(parsed);
        Assert.Equal("YouTube", parsed.GetString("clientProcName"));
    }
}

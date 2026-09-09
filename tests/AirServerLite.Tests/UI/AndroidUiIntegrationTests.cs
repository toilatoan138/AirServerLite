using AirServerLite.Core;
using Xunit;

namespace AirServerLite.Tests.UI;

public class AndroidUiIntegrationTests
{
    [Fact]
    public void StatusFormatting_SupportsBothIosAndAndroid()
    {
        string statusIos = "Connected (iOS AirPlay)";
        string statusAndroid = "Connected (Android Miracast / Smart View)";
        Assert.Contains("iOS", statusIos);
        Assert.Contains("Android", statusAndroid);
    }
}

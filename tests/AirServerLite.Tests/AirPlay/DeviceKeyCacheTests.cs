using System.Net;
using AirServerLite.AirPlay;
using Xunit;

namespace AirServerLite.Tests.AirPlay;

public class DeviceKeyCacheTests
{
    [Fact]
    public void DeviceKeyCache_RememberAndTryGetAudioIv_StoresAndRetrieves()
    {
        var ip = IPAddress.Parse("192.168.1.100");
        var iv = new byte[16] { 0x3F, 0x16, 0x03, 0xAF, 0x18, 0x12, 0x72, 0x8F, 0x51, 0xAF, 0x5E, 0x4E, 0x11, 0x22, 0x33, 0x44 };

        DeviceKeyCache.RememberAudioIv(ip, iv);
        var retrieved = DeviceKeyCache.TryGetAudioIv(ip);

        Assert.NotNull(retrieved);
        Assert.Equal(iv, retrieved);
    }

    [Fact]
    public void DeviceKeyCache_RememberAesKeyAndAudioIv_PreservesBoth()
    {
        var ip = IPAddress.Parse("192.168.1.101");
        var key = new byte[16] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        var iv = new byte[16] { 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };

        DeviceKeyCache.Remember(ip, key);
        DeviceKeyCache.RememberAudioIv(ip, iv);

        var retrievedKey = DeviceKeyCache.TryGet(ip);
        var retrievedIv = DeviceKeyCache.TryGetAudioIv(ip);

        Assert.NotNull(retrievedKey);
        Assert.NotNull(retrievedIv);
        Assert.Equal(key, retrievedKey);
        Assert.Equal(iv, retrievedIv);
    }

    [Fact]
    public void DeviceKeyCache_Forget_ClearsAllFieldsForDevice()
    {
        var ip = IPAddress.Parse("192.168.1.102");
        var iv = new byte[16] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 };

        DeviceKeyCache.RememberAudioIv(ip, iv);
        DeviceKeyCache.Forget(ip);

        Assert.Null(DeviceKeyCache.TryGetAudioIv(ip));
    }
}

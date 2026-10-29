using HeartRateBroadcast.App.Models;

namespace HeartRateBroadcast.Tests.HeartRate;

public class HeartRateDeviceClassifierTests
{
    [Theory]
    [InlineData("HUAWEI Band 8")]
    [InlineData("Xiaomi Smart Band 7 Pro")]
    [InlineData("Garmin HRM-Pro Plus")]
    [InlineData("Polar H10 12345678")]
    [InlineData("COROS PACE 3")]
    [InlineData("Magene H64")]
    [InlineData("CooSpo H808S")]
    [InlineData("Decathlon HR Strap")]
    [InlineData("Wahoo TICKR")]
    [InlineData("My Heart Rate Monitor")]
    public void MatchesHeartRateName_ReturnsTrue_ForKnownFitnessBrandsAndKeywords(string deviceName)
    {
        Assert.True(HeartRateDeviceClassifier.MatchesHeartRateName(deviceName));
    }

    [Theory]
    [InlineData("AirPods Pro")]
    [InlineData("SONY WH-1000XM4")]
    [InlineData("[TV] Samsung 7 Series")]
    [InlineData("Logitech MX Master 3")]
    [InlineData("DESKTOP-89F3A1")]
    [InlineData("未知设备")]
    [InlineData("")]
    [InlineData(null)]
    public void MatchesHeartRateName_ReturnsFalse_ForIrrelevantOrEmptyDevices(string? deviceName)
    {
        Assert.False(HeartRateDeviceClassifier.MatchesHeartRateName(deviceName));
    }

    [Fact]
    public void DiscoveredDevice_CategorizesBroadcastingDeviceHighest()
    {
        var device = new DiscoveredDevice(0x112233445566, "Unknown", -65, DateTimeOffset.UtcNow, isHeartRateCapable: true, isHeartRateBroadcasting: true);
        Assert.Equal(HeartRateDeviceCategory.Broadcasting, device.Category);
        Assert.Equal("广播心率", device.CategoryBadgeText);
        Assert.True(device.IsHeartRateCandidate);
    }

    [Fact]
    public void DiscoveredDevice_CategorizesGattServiceDeviceCorrectly()
    {
        var device = new DiscoveredDevice(0x112233445566, "Unknown", -75, DateTimeOffset.UtcNow, isHeartRateCapable: true, hasHeartRateService: true);
        Assert.Equal(HeartRateDeviceCategory.GattService, device.Category);
        Assert.Equal("GATT心率", device.CategoryBadgeText);
        Assert.True(device.IsHeartRateCandidate);
    }

    [Fact]
    public void DiscoveredDevice_CategorizesGenericBleDevice()
    {
        var device = new DiscoveredDevice(0x112233445566, "AirPods", -80, DateTimeOffset.UtcNow, isHeartRateCapable: false);
        Assert.Equal(HeartRateDeviceCategory.GenericBle, device.Category);
        Assert.Equal("其他BLE", device.CategoryBadgeText);
        Assert.False(device.IsHeartRateCandidate);
    }

    [Theory]
    [InlineData(-50, 4)]
    [InlineData(-68, 3)]
    [InlineData(-80, 2)]
    [InlineData(-90, 1)]
    [InlineData(-98, 0)]
    public void DiscoveredDevice_CalculatesSignalBars(short rssi, int expectedBars)
    {
        var device = new DiscoveredDevice(0x112233445566, "Test", rssi, DateTimeOffset.UtcNow, false);
        Assert.Equal(expectedBars, device.SignalBars);
    }
}


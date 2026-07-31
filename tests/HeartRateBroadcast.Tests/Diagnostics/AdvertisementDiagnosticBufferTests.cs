using HeartRateBroadcast.App.Models;
using HeartRateBroadcast.App.Services.Diagnostics;

namespace HeartRateBroadcast.Tests.Diagnostics;

public sealed class AdvertisementDiagnosticBufferTests
{
    [Fact]
    public void Disabled_DoesNotRetainPackets()
    {
        var buffer = new AdvertisementDiagnosticBuffer(new AppSettings { DiagnosticMode = false });
        buffer.Add(Packet(1));
        Assert.Empty(buffer.Snapshot());
    }

    [Fact]
    public void Enabled_RateLimitsAndRedactsDeviceIdentity()
    {
        var buffer = new AdvertisementDiagnosticBuffer(new AppSettings { DiagnosticMode = true }, 10, TimeSpan.FromSeconds(1));
        buffer.Add(Packet(0xAABBCC, DateTimeOffset.UtcNow));
        buffer.Add(Packet(0xAABBCC, DateTimeOffset.UtcNow.AddMilliseconds(100)));

        AdvertisementDiagnostic item = Assert.Single(buffer.Snapshot());
        Assert.StartsWith("设备-", item.Device);
        Assert.DoesNotContain("AABBCC", item.Device, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Capacity_DropsOldestItems()
    {
        var buffer = new AdvertisementDiagnosticBuffer(new AppSettings { DiagnosticMode = true }, 2, TimeSpan.Zero);
        buffer.Add(Packet(1));
        buffer.Add(Packet(2));
        buffer.Add(Packet(3));
        Assert.Equal(2, buffer.Snapshot().Count);
    }

    private static BluetoothPacket Packet(ulong address, DateTimeOffset? time = null) => new(
        address, "Sensitive Name", -50, time ?? DateTimeOffset.UtcNow,
        [Guid.NewGuid()], [new BluetoothDataSection(0x16, Enumerable.Range(0, 64).Select(i => (byte)i).ToArray())],
        null, []);
}

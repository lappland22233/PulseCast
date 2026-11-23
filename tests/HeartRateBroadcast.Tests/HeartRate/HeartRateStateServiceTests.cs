using HeartRateBroadcast.App.Models;
using HeartRateBroadcast.App.Services.Bluetooth;
using HeartRateBroadcast.App.Services.Diagnostics;
using HeartRateBroadcast.App.Services.HeartRate;
using HeartRateBroadcast.App.Services.Parsing;

namespace HeartRateBroadcast.Tests.HeartRate;

public sealed class HeartRateStateServiceTests : IDisposable
{
    private sealed class FakeBluetoothService : IBluetoothAdvertisementService
    {
        public event EventHandler<BluetoothPacket>? PacketReceived;
        public event EventHandler<BluetoothScanState>? StatusChanged { add { } remove { } }
        public BluetoothScanState CurrentState => BluetoothScanState.Idle;
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
        public void FirePacket(BluetoothPacket packet) => PacketReceived?.Invoke(this, packet);
    }

    private sealed class FakeGattClient : IHeartRateGattClient
    {
        public event EventHandler<HeartRateSample>? SampleReceived;
        public event EventHandler<ulong>? Disconnected { add { } remove { } }
        public Task ConnectAsync(ulong address, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void CancelConnect() { }
        public Task DisconnectAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void FireSample(HeartRateSample sample) => SampleReceived?.Invoke(this, sample);
    }

    private readonly FakeBluetoothService _bluetooth = new();
    private readonly FakeGattClient _gatt = new();
    private readonly AdvertisementDiagnosticBuffer _diagnostics = new(new AppSettings { DiagnosticMode = false });
    private readonly HeartRateParserPipeline _pipeline = new([]);

    [Fact]
    public void AcceptGattSample_FiltersOutOfRangeBpm()
    {
        var settings = new AppSettings { MinimumBpm = 40, MaximumBpm = 200 };
        using var service = new HeartRateStateService(_bluetooth, _pipeline, _diagnostics, _gatt, settings);

        service.AcceptGattSample(new HeartRateSample(30, 0x112233445566, DateTimeOffset.UtcNow, HeartRateSource.Gatt, "test"));
        Assert.Equal(HeartRateStatusKind.Scanning, service.Current.Kind);

        service.AcceptGattSample(new HeartRateSample(85, 0x112233445566, DateTimeOffset.UtcNow, HeartRateSource.Gatt, "test"));
        Assert.Equal(HeartRateStatusKind.Live, service.Current.Kind);
        Assert.Equal((ushort)85, service.Current.BeatsPerMinute);

        service.AcceptGattSample(new HeartRateSample(220, 0x112233445566, DateTimeOffset.UtcNow, HeartRateSource.Gatt, "test"));
        Assert.Equal((ushort)85, service.Current.BeatsPerMinute);
    }

    [Fact]
    public void AcceptGattSample_IgnoresSamplesFromUnselectedDevices()
    {
        var settings = new AppSettings { MinimumBpm = 40, MaximumBpm = 200 };
        using var service = new HeartRateStateService(_bluetooth, _pipeline, _diagnostics, _gatt, settings);
        service.SelectedAddress = 0xAAAAAAAAAAAAAAAA;

        service.AcceptGattSample(new HeartRateSample(75, 0xBBBBBBBBBBBB, DateTimeOffset.UtcNow, HeartRateSource.Gatt, "test"));
        Assert.Equal(HeartRateStatusKind.Scanning, service.Current.Kind);

        service.AcceptGattSample(new HeartRateSample(75, 0xAAAAAAAAAAAAAAAA, DateTimeOffset.UtcNow, HeartRateSource.Gatt, "test"));
        Assert.Equal(HeartRateStatusKind.Live, service.Current.Kind);
    }

    [Fact]
    public void ChangingSelectedAddress_ResetsStateToWaiting()
    {
        var settings = new AppSettings { MinimumBpm = 40, MaximumBpm = 200 };
        using var service = new HeartRateStateService(_bluetooth, _pipeline, _diagnostics, _gatt, settings);
        service.SelectedAddress = 0xAAAAAAAAAAAAAAAA;

        service.AcceptGattSample(new HeartRateSample(80, 0xAAAAAAAAAAAAAAAA, DateTimeOffset.UtcNow, HeartRateSource.Gatt, "test"));
        Assert.Equal(HeartRateStatusKind.Live, service.Current.Kind);

        service.SelectedAddress = 0xBBBBBBBBBBBB;
        Assert.Equal(HeartRateStatusKind.Waiting, service.Current.Kind);
        Assert.Null(service.Current.BeatsPerMinute);
    }

    [Fact]
    public void DeviceChanged_ReportsImmutableSnapshotsAndKeepsKnownDeviceDetails()
    {
        var settings = new AppSettings();
        using var service = new HeartRateStateService(_bluetooth, _pipeline, _diagnostics, _gatt, settings);

        var snapshots = new List<DeviceSnapshot>();
        service.DeviceChanged += (_, snapshot) => snapshots.Add(snapshot);

        var now = DateTimeOffset.UtcNow;
        var heartRateService = new Guid("0000180d-0000-1000-8000-00805f9b34fb");
        _bluetooth.FirePacket(new BluetoothPacket(0x1234567890AB, "HRM-A", -70, now, [heartRateService], [], null, []));
        _bluetooth.FirePacket(new BluetoothPacket(0x1234567890AB, "未知设备", -55, now.AddSeconds(1), [], [], null, []));

        Assert.Collection(snapshots,
            first => Assert.Equal(new DeviceSnapshot(0x1234567890AB, "HRM-A", -70, now, true, false, true), first),
            second => Assert.Equal(new DeviceSnapshot(0x1234567890AB, "HRM-A", -55, now.AddSeconds(1), true, false, true), second));
    }

    [Fact]
    public void CapacityOverflow_EvictsOldestUnselectedGenericDeviceBeforeHeartRateCandidates()
    {
        using var service = new HeartRateStateService(_bluetooth, _pipeline, _diagnostics, _gatt, new AppSettings());
        service.SelectedAddress = 1;

        var now = DateTimeOffset.UtcNow;
        _bluetooth.FirePacket(new BluetoothPacket(1, "Selected", -70, now, [], [], null, []));
        _bluetooth.FirePacket(new BluetoothPacket(2, "HRM-A", -70, now.AddSeconds(1), [], [], null, []));
        for (ulong address = 3; address <= 200; address++)
        {
            _bluetooth.FirePacket(new BluetoothPacket(address, "Speaker", -70, now.AddSeconds((double)address), [], [], null, []));
        }

        var events = new List<(ulong Address, bool Removed)>();
        service.DeviceChanged += (_, snapshot) => events.Add((snapshot.Address, false));
        service.DeviceRemoved += (_, address) => events.Add((address, true));
        _bluetooth.FirePacket(new BluetoothPacket(201, "Speaker", -70, now.AddSeconds(201), [], [], null, []));

        Assert.Equal(new (ulong, bool)[] { (201, false), (3, true) }, events);
    }

    public void Dispose()
    {
        _bluetooth.Dispose();
    }
}

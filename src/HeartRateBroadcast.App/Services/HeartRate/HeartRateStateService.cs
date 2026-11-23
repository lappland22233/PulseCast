using HeartRateBroadcast.App.Models;
using HeartRateBroadcast.App.Services.Bluetooth;
using HeartRateBroadcast.App.Services.Diagnostics;
using HeartRateBroadcast.App.Services.Parsing;

namespace HeartRateBroadcast.App.Services.HeartRate;

public interface IHeartRateStateService : IDisposable
{
    event EventHandler<HeartRateDisplayState>? StateChanged;
    event EventHandler<DeviceSnapshot>? DeviceChanged;
    event EventHandler<ulong>? DeviceRemoved;
    HeartRateDisplayState Current { get; }
    ulong? SelectedAddress { get; set; }
    void AcceptGattSample(HeartRateSample sample);
}

public sealed class HeartRateStateService : IHeartRateStateService
{
    private const int MaximumDevices = 200;
    private static readonly Guid HeartRateServiceUuid = new("0000180d-0000-1000-8000-00805f9b34fb");
    private readonly IBluetoothAdvertisementService _bluetooth;
    private readonly HeartRateParserPipeline _pipeline;
    private readonly AdvertisementDiagnosticBuffer _diagnostics;
    private readonly IHeartRateGattClient _gattClient;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, DeviceSnapshot> _devices = [];
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _deviceLifetime;
    private readonly int _minimumBpm;
    private readonly int _maximumBpm;
    private HeartRateDisplayState _current = HeartRateDisplayState.Scanning;
    private DateTimeOffset? _lastSampleAt;
    private ulong? _selectedAddress;
    private bool _disposed;

    public event EventHandler<HeartRateDisplayState>? StateChanged;
    public event EventHandler<DeviceSnapshot>? DeviceChanged;
    public event EventHandler<ulong>? DeviceRemoved;

    public HeartRateDisplayState Current
    {
        get { lock (_gate) return _current; }
    }

    public ulong? SelectedAddress
    {
        get { lock (_gate) return _selectedAddress; }
        set
        {
            HeartRateDisplayState? newState = null;
            lock (_gate)
            {
                if (_selectedAddress != value)
                {
                    _selectedAddress = value;
                    _lastSampleAt = null;
                    if (_current.Kind == HeartRateStatusKind.Live || _current.Kind == HeartRateStatusKind.Stale)
                    {
                        newState = new HeartRateDisplayState(
                            HeartRateStatusKind.Waiting,
                            null,
                            value is null ? "准备扫描" : "等待心率",
                            null,
                            value);
                        _current = newState;
                    }
                }
            }
            if (newState is not null) StateChanged?.Invoke(this, newState);
        }
    }

    public HeartRateStateService(
        IBluetoothAdvertisementService bluetooth,
        HeartRateParserPipeline pipeline,
        AdvertisementDiagnosticBuffer diagnostics,
        IHeartRateGattClient gattClient,
        AppSettings settings)
    {
        _bluetooth = bluetooth;
        _pipeline = pipeline;
        _diagnostics = diagnostics;
        _gattClient = gattClient;
        _timeout = TimeSpan.FromSeconds(Math.Clamp(settings.DataTimeoutSeconds, 3, 60));
        _deviceLifetime = TimeSpan.FromSeconds(Math.Max(30, settings.DataTimeoutSeconds * 3));
        _minimumBpm = Math.Clamp(settings.MinimumBpm, 1, 300);
        _maximumBpm = Math.Clamp(settings.MaximumBpm, _minimumBpm, 300);
        _bluetooth.PacketReceived += OnPacket;
        _bluetooth.StatusChanged += OnBluetoothStatusChanged;
        _gattClient.SampleReceived += OnGattSample;
        _timer = new(_ => CheckTimeoutAndPruneDevices(), null, 1000, 1000);
    }

    private void OnPacket(object? sender, BluetoothPacket packet)
    {
        _diagnostics.Add(packet);
        var parsed = _pipeline.Parse(packet);

        lock (_gate)
        {
            if (_disposed) return;

            bool knownDevice = _devices.TryGetValue(packet.Address, out var previous);
            string name = packet.Name;
            if (knownDevice && (string.IsNullOrWhiteSpace(name) || name == "未知设备" || name == previous.Name))
                name = previous.Name;

            bool broadcasting = previous.IsHeartRateBroadcasting || parsed.Sample is not null;
            bool hasGattService = previous.HasHeartRateService || packet.ServiceUuids.Contains(HeartRateServiceUuid);
            bool capable = previous.IsHeartRateCapable || broadcasting || hasGattService ||
                ((!knownDevice || name != previous.Name) && HeartRateDeviceClassifier.MatchesHeartRateName(name));
            var snapshot = new DeviceSnapshot(packet.Address, name, packet.Rssi, packet.ReceivedAt, capable, broadcasting, hasGattService);
            _devices[packet.Address] = snapshot;

            ulong? removed = _devices.Count > MaximumDevices ? RemoveOverflowDevice() : null;

            // Publish changes and removals in mutation order, including expiry on the timer thread.
            DeviceChanged?.Invoke(this, snapshot);
            if (removed is not null) DeviceRemoved?.Invoke(this, removed.Value);
        }

        if (parsed.Sample is not null) AcceptGattSample(parsed.Sample);
    }

    private ulong? RemoveOverflowDevice()
    {
        DeviceSnapshot? leastRelevant = null;
        foreach (var device in _devices.Values)
        {
            if (device.Address == _selectedAddress) continue;
            if (leastRelevant is not { } current ||
                (device.IsHeartRateCandidate == current.IsHeartRateCandidate
                    ? device.LastSeen < current.LastSeen
                    : !device.IsHeartRateCandidate))
            {
                leastRelevant = device;
            }
        }

        if (leastRelevant is not { } removed) return null;
        _devices.Remove(removed.Address);
        return removed.Address;
    }

    private void OnGattSample(object? sender, HeartRateSample sample) => AcceptGattSample(sample);

    private void OnBluetoothStatusChanged(object? sender, BluetoothScanState scanState)
    {
        HeartRateDisplayState? state = scanState.Status switch
        {
            BluetoothScanStatus.Starting or BluetoothScanStatus.Scanning =>
                new(HeartRateStatusKind.Scanning, null, scanState.Message, null, SelectedAddress),
            BluetoothScanStatus.Error =>
                new(HeartRateStatusKind.BluetoothUnavailable, null, scanState.Message, null, SelectedAddress),
            _ => null,
        };
        if (state is not null) Publish(state);
    }

    public void AcceptGattSample(HeartRateSample sample)
    {
        HeartRateDisplayState state;
        lock (_gate)
        {
            if (_disposed || (_selectedAddress is not null && sample.BluetoothAddress != _selectedAddress)) return;
            if (sample.BeatsPerMinute < _minimumBpm || sample.BeatsPerMinute > _maximumBpm) return;
            if (_lastSampleAt is not null && sample.ReceivedAt < _lastSampleAt) return;

            _lastSampleAt = sample.ReceivedAt;
            state = new(HeartRateStatusKind.Live, sample.BeatsPerMinute, "实时", sample.ReceivedAt, sample.BluetoothAddress);
            _current = state;
        }
        StateChanged?.Invoke(this, state);
    }

    private void CheckTimeoutAndPruneDevices()
    {
        HeartRateDisplayState? staleState = null;
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (_disposed) return;
            if (_lastSampleAt is not null && now - _lastSampleAt > _timeout && _current.Kind == HeartRateStatusKind.Live)
            {
                staleState = new(HeartRateStatusKind.Stale, _current.BeatsPerMinute, "信号中断", _lastSampleAt, _current.DeviceAddress);
                _current = staleState;
            }

            List<ulong>? removed = null;
            foreach (var device in _devices.Values)
            {
                if (device.Address != _selectedAddress && now - device.LastSeen > _deviceLifetime)
                    (removed ??= []).Add(device.Address);
            }

            if (removed is not null)
            {
                foreach (var address in removed) _devices.Remove(address);
                foreach (var address in removed) DeviceRemoved?.Invoke(this, address);
            }
        }

        if (staleState is not null) StateChanged?.Invoke(this, staleState);
    }

    private void Publish(HeartRateDisplayState state)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _current = state;
        }
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer.Dispose();
        _bluetooth.PacketReceived -= OnPacket;
        _bluetooth.StatusChanged -= OnBluetoothStatusChanged;
        _gattClient.SampleReceived -= OnGattSample;
    }
}

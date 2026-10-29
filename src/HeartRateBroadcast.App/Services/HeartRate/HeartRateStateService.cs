using HeartRateBroadcast.App.Models;
using HeartRateBroadcast.App.Services.Bluetooth;
using HeartRateBroadcast.App.Services.Diagnostics;
using HeartRateBroadcast.App.Services.Parsing;

namespace HeartRateBroadcast.App.Services.HeartRate;

public interface IHeartRateStateService : IDisposable
{
    event EventHandler<HeartRateDisplayState>? StateChanged;
    event EventHandler<DiscoveredDevice>? DeviceChanged;
    event EventHandler<ulong>? DeviceRemoved;
    HeartRateDisplayState Current { get; }
    ulong? SelectedAddress { get; set; }
    void AcceptGattSample(HeartRateSample sample);
}

public sealed class HeartRateStateService : IHeartRateStateService
{
    private const int MaximumDevices = 200;
    private readonly IBluetoothAdvertisementService _bluetooth;
    private readonly HeartRateParserPipeline _pipeline;
    private readonly AdvertisementDiagnosticBuffer _diagnostics;
    private readonly IHeartRateGattClient _gattClient;
    private readonly System.Threading.Timer _timer;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, DiscoveredDevice> _devices = [];
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _deviceLifetime;
    private readonly int _minimumBpm;
    private readonly int _maximumBpm;
    private HeartRateDisplayState _current = HeartRateDisplayState.Scanning;
    private DateTimeOffset? _lastSampleAt;
    private ulong? _selectedAddress;
    private bool _disposed;

    public event EventHandler<HeartRateDisplayState>? StateChanged;
    public event EventHandler<DiscoveredDevice>? DeviceChanged;
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
        DiscoveredDevice snapshot;
        List<ulong>? removed = null;

        lock (_gate)
        {
            bool broadcasting = parsed.Sample is not null;
            bool hasGattService = packet.ServiceUuids.Contains(Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceUuids.HeartRate);
            bool isCandidate = broadcasting || hasGattService || HeartRateDeviceClassifier.MatchesHeartRateName(packet.Name);

            if (_devices.TryGetValue(packet.Address, out var existing))
            {
                existing.Update(
                    packet.Rssi,
                    packet.ReceivedAt,
                    packet.Name,
                    capable: isCandidate || existing.IsHeartRateCapable,
                    broadcasting: broadcasting || existing.IsHeartRateBroadcasting,
                    hasGattService: hasGattService || existing.HasHeartRateService);
                snapshot = existing;
            }
            else
            {
                snapshot = new DiscoveredDevice(
                    packet.Address,
                    packet.Name,
                    packet.Rssi,
                    packet.ReceivedAt,
                    isHeartRateCapable: isCandidate,
                    isHeartRateBroadcasting: broadcasting,
                    hasHeartRateService: hasGattService);
                _devices[packet.Address] = snapshot;
            }

            if (_devices.Count > MaximumDevices)
            {
                removed = _devices.Values
                    .Where(device => device.Address != _selectedAddress)
                    .OrderBy(device => device.IsHeartRateCandidate ? 1 : 0)
                    .ThenBy(device => device.LastSeen)
                    .Take(_devices.Count - MaximumDevices)
                    .Select(device => device.Address)
                    .ToList();
                foreach (var address in removed) _devices.Remove(address);
            }
        }

        DeviceChanged?.Invoke(this, snapshot);
        if (removed is not null)
            foreach (var address in removed) DeviceRemoved?.Invoke(this, address);
        if (parsed.Sample is not null) AcceptGattSample(parsed.Sample);
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
        List<ulong>? removed = null;
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            if (_disposed) return;
            if (_lastSampleAt is not null && now - _lastSampleAt > _timeout && _current.Kind == HeartRateStatusKind.Live)
            {
                staleState = new(HeartRateStatusKind.Stale, _current.BeatsPerMinute, "信号中断", _lastSampleAt, _current.DeviceAddress);
                _current = staleState;
            }

            removed = _devices.Values
                .Where(device => device.Address != _selectedAddress && now - device.LastSeen > _deviceLifetime)
                .Select(device => device.Address)
                .ToList();
            foreach (var address in removed) _devices.Remove(address);
        }

        if (staleState is not null) StateChanged?.Invoke(this, staleState);
        foreach (var address in removed) DeviceRemoved?.Invoke(this, address);
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

using System.Runtime.InteropServices.WindowsRuntime;
using HeartRateBroadcast.App.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;

namespace HeartRateBroadcast.App.Services.Bluetooth;

public interface IBluetoothAdvertisementService : IDisposable
{
    event EventHandler<BluetoothPacket>? PacketReceived;
    event EventHandler<BluetoothScanState>? StatusChanged;
    BluetoothScanState CurrentState { get; }
    void Start();
    void Stop();
}

public sealed class BluetoothAdvertisementService : IBluetoothAdvertisementService
{
    private readonly BluetoothLEAdvertisementWatcher _watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };
    private readonly object _gate = new();
    private BluetoothScanState _currentState = BluetoothScanState.Idle;
    private bool _disposed;

    public event EventHandler<BluetoothPacket>? PacketReceived;
    public event EventHandler<BluetoothScanState>? StatusChanged;

    public BluetoothScanState CurrentState
    {
        get { lock (_gate) return _currentState; }
    }

    public BluetoothAdvertisementService()
    {
        _watcher.Received += OnReceived;
        _watcher.Stopped += OnStopped;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_watcher.Status is BluetoothLEAdvertisementWatcherStatus.Started or BluetoothLEAdvertisementWatcherStatus.Stopping)
                return;
        }

        PublishState(new(BluetoothScanStatus.Starting, "正在启动扫描"));
        try
        {
            _watcher.Start();
            PublishState(new(BluetoothScanStatus.Scanning, "正在扫描"));
        }
        catch (Exception ex)
        {
            PublishState(new(BluetoothScanStatus.Error, "无法启动蓝牙扫描", ex.GetType().Name));
            throw;
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed || _watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
                return;
        }

        PublishState(new(BluetoothScanStatus.Stopping, "正在停止扫描"));
        _watcher.Stop();
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        PublishState(args.Error == BluetoothError.Success
            ? BluetoothScanState.Idle
            : new BluetoothScanState(BluetoothScanStatus.Error, $"扫描停止：{args.Error}", args.Error.ToString()));
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var sections = args.Advertisement.DataSections.Select(s => new BluetoothDataSection(s.DataType, s.Data.ToArray())).ToArray();
        var manufacturer = args.Advertisement.ManufacturerData.FirstOrDefault();
        string deviceName = BluetoothNameDecoder.Decode(sections, args.Advertisement.LocalName);

        PacketReceived?.Invoke(this, new BluetoothPacket(
            args.BluetoothAddress,
            deviceName,
            args.RawSignalStrengthInDBm,
            args.Timestamp,
            args.Advertisement.ServiceUuids.ToArray(),
            sections,
            manufacturer?.CompanyId,
            manufacturer is null ? [] : manufacturer.Data.ToArray()));
    }

    private void PublishState(BluetoothScanState state)
    {
        lock (_gate) _currentState = state;
        StatusChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
            _watcher.Stop();
        _watcher.Received -= OnReceived;
        _watcher.Stopped -= OnStopped;
    }
}

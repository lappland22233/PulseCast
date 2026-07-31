using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using HeartRateBroadcast.App.Models;
using HeartRateBroadcast.App.Services.Parsing;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;

namespace HeartRateBroadcast.App.Services.Bluetooth;

public interface IHeartRateGattClient : IAsyncDisposable
{
    event EventHandler<HeartRateSample>? SampleReceived;
    Task ConnectAsync(ulong address, CancellationToken cancellationToken = default);
    void CancelConnect();
    Task DisconnectAsync();
}

public sealed class HeartRateGattClient : IHeartRateGattClient
{
    private static readonly Guid HeartRateService = GattServiceUuids.HeartRate;
    private static readonly Guid Measurement = GattCharacteristicUuids.HeartRateMeasurement;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(3);
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _connectionGate = new();
    private CancellationTokenSource? _connectingCts;
    private GattSession? _session;
    private long _nextSessionId;
    private long _activeSessionId;
    private bool _disposed;

    public event EventHandler<HeartRateSample>? SampleReceived;

    public async Task ConnectAsync(ulong address, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await CleanupCurrentSessionAsync().ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(ConnectTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            lock (_connectionGate) _connectingCts = linkedCts;

            BluetoothLEDevice? device = null;
            GattDeviceService? service = null;
            GattCharacteristic? characteristic = null;
            TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? handler = null;
            var sessionId = Interlocked.Increment(ref _nextSessionId);

            try
            {
                device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(linkedCts.Token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("无法打开蓝牙设备");

                var services = await device.GetGattServicesForUuidAsync(HeartRateService, BluetoothCacheMode.Uncached)
                    .AsTask(linkedCts.Token).ConfigureAwait(false);
                if (services.Status != GattCommunicationStatus.Success)
                    throw new InvalidOperationException($"读取心率服务失败：{services.Status}");
                service = services.Services.FirstOrDefault()
                    ?? throw new InvalidOperationException("设备未提供标准心率服务");

                var characteristics = await service.GetCharacteristicsForUuidAsync(Measurement, BluetoothCacheMode.Uncached)
                    .AsTask(linkedCts.Token).ConfigureAwait(false);
                if (characteristics.Status != GattCommunicationStatus.Success)
                    throw new InvalidOperationException($"读取心率特征失败：{characteristics.Status}");
                characteristic = characteristics.Characteristics.FirstOrDefault()
                    ?? throw new InvalidOperationException("设备未提供心率测量特征");

                handler = (_, args) => OnValueChanged(sessionId, address, args);
                characteristic.ValueChanged += handler;
                var status = await characteristic
                    .WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify)
                    .AsTask(linkedCts.Token).ConfigureAwait(false);
                if (status != GattCommunicationStatus.Success)
                    throw new InvalidOperationException($"心率通知订阅失败：{status}");

                var lifetimeCts = new CancellationTokenSource();
                _session = new(sessionId, address, device, service, characteristic, handler, lifetimeCts);
                Interlocked.Exchange(ref _activeSessionId, sessionId);
                device = null;
                service = null;
                characteristic = null;
                handler = null;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await CleanupResourcesAsync(characteristic, handler, service, device).ConfigureAwait(false);
                throw new TimeoutException($"连接心率设备超过 {ConnectTimeout.TotalSeconds:0} 秒");
            }
            catch
            {
                await CleanupResourcesAsync(characteristic, handler, service, device).ConfigureAwait(false);
                throw;
            }
            finally
            {
                lock (_connectionGate)
                {
                    if (ReferenceEquals(_connectingCts, linkedCts)) _connectingCts = null;
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void CancelConnect()
    {
        lock (_connectionGate) _connectingCts?.Cancel();
    }

    private void OnValueChanged(long sessionId, ulong address, GattValueChangedEventArgs args)
    {
        if (Interlocked.Read(ref _activeSessionId) != sessionId) return;
        byte[] bytes = args.CharacteristicValue.ToArray();
        if (HeartRateMeasurementParser.TryParse(bytes, out ushort bpm))
            SampleReceived?.Invoke(this, new(bpm, address, args.Timestamp, HeartRateSource.Gatt, "GATT 0x2A37"));
    }

    public async Task DisconnectAsync()
    {
        CancelConnect();
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CleanupCurrentSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task CleanupCurrentSessionAsync()
    {
        var session = _session;
        _session = null;
        Interlocked.Exchange(ref _activeSessionId, 0);
        if (session is null) return;

        session.LifetimeCts.Cancel();
        await CleanupResourcesAsync(session.Characteristic, session.Handler, session.Service, session.Device).ConfigureAwait(false);
        session.LifetimeCts.Dispose();
    }

    private static async Task CleanupResourcesAsync(
        GattCharacteristic? characteristic,
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs>? handler,
        GattDeviceService? service,
        BluetoothLEDevice? device)
    {
        if (characteristic is not null && handler is not null)
            characteristic.ValueChanged -= handler;

        if (characteristic is not null)
        {
            try
            {
                using var timeoutCts = new CancellationTokenSource(CleanupTimeout);
                await characteristic
                    .WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.None)
                    .AsTask(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or UnauthorizedAccessException or InvalidOperationException or COMException)
            {
                // 清理是尽力而为；本地事件和 WinRT 对象仍必须释放。
            }
        }

        service?.Dispose();
        device?.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        lock (_connectionGate)
        {
            if (_disposed) return;
            _disposed = true;
            _connectingCts?.Cancel();
        }

        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CleanupCurrentSessionAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }

    private sealed record GattSession(
        long Id,
        ulong Address,
        BluetoothLEDevice Device,
        GattDeviceService Service,
        GattCharacteristic Characteristic,
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> Handler,
        CancellationTokenSource LifetimeCts);
}

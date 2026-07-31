using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HeartRateBroadcast.App.Models;
using HeartRateBroadcast.App.Services.Bluetooth;
using HeartRateBroadcast.App.Services.HeartRate;
using HeartRateBroadcast.App.Services.Overlay;
using HeartRateBroadcast.App.Services.Settings;

namespace HeartRateBroadcast.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly IBluetoothAdvertisementService _bluetooth;
    private readonly IHeartRateGattClient _gattClient;
    private readonly IHeartRateStateService _state;
    private readonly IOverlayWindowManager _overlay;
    private readonly ISettingsStore _settingsStore;
    private readonly AppSettings _settings;
    public ObservableCollection<DiscoveredDevice> Devices { get; } = [];
    /// <summary>共享的悬浮窗视图模型：主窗口直接绑定其样式/反色属性，实时联动悬浮窗。</summary>
    public OverlayViewModel Overlay { get; }
    [ObservableProperty] private DiscoveredDevice? selectedDevice;
    [ObservableProperty] private string currentBpm = "--";
    [ObservableProperty] private string status = "准备扫描";
    [ObservableProperty] private HeartRateStatusKind statusKind = HeartRateStatusKind.Waiting;
    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private bool isConnecting;
    [ObservableProperty] private bool isOverlayLocked;
    [ObservableProperty] private bool isOverlayBackgroundHidden;
    [ObservableProperty] private bool isOverlayClickThrough;

    public string ConnectButtonText => IsConnecting ? "连接中…" : "连接所选设备";
    public string ScanButtonText => IsScanning ? "停止扫描" : "开始扫描";
    public bool HasNoDevices => Devices.Count == 0;
    public string EmptyStateText => IsScanning ? "正在搜索附近的心率设备…" : "未发现设备，请检查蓝牙后重新扫描。";
    public System.Windows.Media.Brush StatusBrush => StatusKind switch
    {
        HeartRateStatusKind.Live => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Live"],
        HeartRateStatusKind.Error or HeartRateStatusKind.BluetoothUnavailable => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Error"],
        HeartRateStatusKind.Stale => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Warn"],
        _ => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Info"],
    };
    public string OverlayLockButtonText => IsOverlayLocked ? "解除固定" : "固定悬浮窗";
    public string OverlayBackgroundButtonText => IsOverlayBackgroundHidden ? "显示背景" : "隐藏背景";

    public MainWindowViewModel(IBluetoothAdvertisementService bluetooth, IHeartRateGattClient gattClient, IHeartRateStateService state, IOverlayWindowManager overlay, ISettingsStore settingsStore, AppSettings settings, OverlayViewModel overlayViewModel)
    {
        _bluetooth = bluetooth; _gattClient = gattClient; _state = state; _overlay = overlay; _settingsStore = settingsStore; _settings = settings; Overlay = overlayViewModel;
        IsOverlayLocked = _overlay.IsLocked;
        IsOverlayBackgroundHidden = _overlay.IsBackgroundHidden;
        IsOverlayClickThrough = _overlay.IsClickThrough;
        _state.DeviceChanged += OnDeviceChanged;
        _state.DeviceRemoved += OnDeviceRemoved;
        _state.StateChanged += (_, value) => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            CurrentBpm = value.BeatsPerMinute?.ToString() ?? "--";
            Status = value.Message;
            StatusKind = value.Kind;
        });
        _bluetooth.StatusChanged += OnBluetoothStatusChanged;
        ApplyScanState(_bluetooth.CurrentState);
    }

    partial void OnSelectedDeviceChanged(DiscoveredDevice? value)
    {
        _state.SelectedAddress = value?.Address;
        _settings.SelectedDeviceAddress = value?.Address;
        Status = value is null ? "请选择设备" : $"已选择 {value.Name}";
        ConnectSelectedDeviceCommand.NotifyCanExecuteChanged();
        _ = SaveSettingsSafelyAsync();
    }

    partial void OnStatusKindChanged(HeartRateStatusKind value) => OnPropertyChanged(nameof(StatusBrush));

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(ScanButtonText));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    partial void OnIsConnectingChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectButtonText));
        ConnectSelectedDeviceCommand.NotifyCanExecuteChanged();
        CancelConnectCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsOverlayLockedChanged(bool value) => OnPropertyChanged(nameof(OverlayLockButtonText));
    partial void OnIsOverlayBackgroundHiddenChanged(bool value) => OnPropertyChanged(nameof(OverlayBackgroundButtonText));

    private void OnDeviceChanged(object? sender, DiscoveredDevice device) => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
    {
        var index = Devices.ToList().FindIndex(existing => existing.Address == device.Address);
        if (index < 0)
        {
            Devices.Add(device);
            OnPropertyChanged(nameof(HasNoDevices));
        }
        else
        {
            Devices[index] = device;
        }

        if (_settings.SelectedDeviceAddress == device.Address)
            SelectedDevice = device;
    });

    private void OnDeviceRemoved(object? sender, ulong address) => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
    {
        var existing = Devices.FirstOrDefault(device => device.Address == address);
        if (existing is not null)
        {
            Devices.Remove(existing);
            OnPropertyChanged(nameof(HasNoDevices));
        }
    });

    private void OnBluetoothStatusChanged(object? sender, BluetoothScanState state) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => ApplyScanState(state));

    private void ApplyScanState(BluetoothScanState state)
    {
        IsScanning = state.IsScanning;
        StatusKind = state.Status == BluetoothScanStatus.Error ? HeartRateStatusKind.BluetoothUnavailable : HeartRateStatusKind.Scanning;
        if (!IsConnecting) Status = state.Message;
    }

    private bool CanConnectSelectedDevice() => SelectedDevice is not null && !IsConnecting;

    [RelayCommand(CanExecute = nameof(CanConnectSelectedDevice))]
    private async Task ConnectSelectedDeviceAsync()
    {
        if (SelectedDevice is null) return;
        IsConnecting = true;
        Status = $"正在连接 {SelectedDevice.Name}…";
        try
        {
            _bluetooth.Stop();
            await _gattClient.ConnectAsync(SelectedDevice.Address);
            Status = "已连接，等待心率数据";
        }
        catch (OperationCanceledException)
        {
            Status = "已取消连接";
        }
        catch (TimeoutException ex)
        {
            Status = ex.Message;
        }
        catch (Exception ex)
        {
            Status = $"连接失败：{ex.Message}";
        }
        finally
        {
            IsConnecting = false;
            _bluetooth.Start();
        }
    }

    [RelayCommand] private void ToggleScan() { if (_bluetooth.CurrentState.IsScanning) _bluetooth.Stop(); else _bluetooth.Start(); }
    [RelayCommand(CanExecute = nameof(IsConnecting))] private void CancelConnect() => _gattClient.CancelConnect();
    [RelayCommand] private void ShowOverlay() => _overlay.Show();

    private async Task SaveSettingsSafelyAsync()
    {
        try
        {
            await _settingsStore.SaveAsync(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"设置保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ToggleOverlayLockAsync()
    {
        IsOverlayLocked = !IsOverlayLocked;
        _overlay.SetLocked(IsOverlayLocked);
        await _settingsStore.SaveAsync(_settings);
    }

    [RelayCommand]
    private async Task ToggleOverlayBackgroundAsync()
    {
        IsOverlayBackgroundHidden = !IsOverlayBackgroundHidden;
        _overlay.SetBackgroundHidden(IsOverlayBackgroundHidden);
        await _settingsStore.SaveAsync(_settings);
    }

    [RelayCommand]
    private async Task ToggleOverlayClickThroughAsync()
    {
        IsOverlayClickThrough = !IsOverlayClickThrough;
        _overlay.SetClickThrough(IsOverlayClickThrough);
        IsOverlayClickThrough = _overlay.IsClickThrough;
        await _settingsStore.SaveAsync(_settings);
    }
}

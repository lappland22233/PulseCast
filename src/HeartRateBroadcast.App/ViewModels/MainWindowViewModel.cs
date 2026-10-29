using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
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
    private readonly DispatcherTimer _saveDebounceTimer;

    public ObservableCollection<DiscoveredDevice> Devices { get; } = [];
    public ICollectionView FilteredDevices { get; }

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

    // 设备筛选与搜索
    [ObservableProperty] private bool filterHeartRateOnly = true;
    [ObservableProperty] private bool hideUnknownDevices = true;
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private int heartRateDeviceCount;
    [ObservableProperty] private int totalDeviceCount;

    public string ConnectButtonText => IsConnecting ? "连接中…" : "连接所选设备";
    public string ScanButtonText => IsScanning ? "停止扫描" : "开始扫描";
    public bool HasNoDevices => FilteredDevices.IsEmpty;

    public string EmptyStateText
    {
        get
        {
            if (Devices.Count == 0)
                return IsScanning ? "正在雷达搜索附近的心率设备…" : "未发现设备，请检查电脑蓝牙是否已开启后点击“开始扫描”。";

            if (FilteredDevices.IsEmpty)
            {
                if (!string.IsNullOrWhiteSpace(SearchText))
                    return $"未找到与“{SearchText}”匹配的设备。";
                if (FilterHeartRateOnly)
                    return "当前未过滤到心率设备。\n请确保手环/心率带已开启“心率广播/共享心率”，或切换至【全部设备】查看。";
                if (HideUnknownDevices)
                    return "附近发现的设备均为无名匿名设备，已自动隐藏。\n可取消“过滤匿名”查看所有信标。";
                return "当前筛选条件下无可用设备。";
            }

            return string.Empty;
        }
    }

    public System.Windows.Media.Brush StatusBrush => StatusKind switch
    {
        HeartRateStatusKind.Live => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Live"],
        HeartRateStatusKind.Error or HeartRateStatusKind.BluetoothUnavailable => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Error"],
        HeartRateStatusKind.Stale => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Warn"],
        _ => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Info"],
    };

    public string OverlayLockButtonText => IsOverlayLocked ? "解除固定" : "固定悬浮窗";
    public string OverlayBackgroundButtonText => IsOverlayBackgroundHidden ? "显示卡片" : "透明背景";

    public MainWindowViewModel(
        IBluetoothAdvertisementService bluetooth,
        IHeartRateGattClient gattClient,
        IHeartRateStateService state,
        IOverlayWindowManager overlay,
        ISettingsStore settingsStore,
        AppSettings settings,
        OverlayViewModel overlayViewModel)
    {
        _bluetooth = bluetooth;
        _gattClient = gattClient;
        _state = state;
        _overlay = overlay;
        _settingsStore = settingsStore;
        _settings = settings;
        Overlay = overlayViewModel;

        FilteredDevices = CollectionViewSource.GetDefaultView(Devices);
        FilteredDevices.Filter = FilterDevicePredicate;
        FilteredDevices.SortDescriptions.Add(new SortDescription(nameof(DiscoveredDevice.IsHeartRateCandidate), ListSortDirection.Descending));
        FilteredDevices.SortDescriptions.Add(new SortDescription(nameof(DiscoveredDevice.Rssi), ListSortDirection.Descending));

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
        _gattClient.Disconnected += OnGattDisconnected;
        ApplyScanState(_bluetooth.CurrentState);

        _saveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _saveDebounceTimer.Tick += async (_, _) =>
        {
            _saveDebounceTimer.Stop();
            await SaveSettingsSafelyAsync();
        };

        Overlay.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(OverlayViewModel.Bpm) or nameof(OverlayViewModel.Status) or nameof(OverlayViewModel.IsLive) or nameof(OverlayViewModel.IsLightText)))
            {
                _saveDebounceTimer.Stop();
                _saveDebounceTimer.Start();
            }
        };

        _overlay.PlacementChanged += (_, _) =>
        {
            _saveDebounceTimer.Stop();
            _saveDebounceTimer.Start();
        };
    }

    private bool FilterDevicePredicate(object obj)
    {
        if (obj is not DiscoveredDevice device) return false;

        // 当前选中的目标设备始终予以显示
        if (SelectedDevice?.Address == device.Address || _settings.SelectedDeviceAddress == device.Address)
            return true;

        // 搜索关键字（匹配名称或 12 位十六进制 MAC 地址）
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            string query = SearchText.Trim();
            bool nameMatch = device.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
            bool addrMatch = device.AddressText.Contains(query, StringComparison.OrdinalIgnoreCase);
            if (!nameMatch && !addrMatch) return false;
        }

        // 仅心率设备模式
        if (FilterHeartRateOnly && !device.IsHeartRateCandidate)
            return false;

        // 过滤未知匿名设备
        if (HideUnknownDevices && !device.IsNamedDevice && !device.IsHeartRateCandidate)
            return false;

        return true;
    }

    private void ApplyFilter()
    {
        FilteredDevices.Refresh();
        OnPropertyChanged(nameof(HasNoDevices));
        OnPropertyChanged(nameof(EmptyStateText));
    }

    partial void OnFilterHeartRateOnlyChanged(bool value) => ApplyFilter();
    partial void OnHideUnknownDevicesChanged(bool value) => ApplyFilter();
    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void ShowHeartRateOnly() => FilterHeartRateOnly = true;

    [RelayCommand]
    private void ShowAllDevices() => FilterHeartRateOnly = false;

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
        var existing = Devices.FirstOrDefault(existingDevice => existingDevice.Address == device.Address);
        if (existing is null)
        {
            Devices.Add(device);
        }
        else if (!ReferenceEquals(existing, device))
        {
            existing.Update(
                device.Rssi,
                device.LastSeen,
                device.Name,
                device.IsHeartRateCapable,
                device.IsHeartRateBroadcasting,
                device.HasHeartRateService);
        }

        UpdateDeviceCounts();
        OnPropertyChanged(nameof(HasNoDevices));
        OnPropertyChanged(nameof(EmptyStateText));

        if (_settings.SelectedDeviceAddress == device.Address && SelectedDevice != (existing ?? device))
            SelectedDevice = existing ?? device;
    });

    private void OnDeviceRemoved(object? sender, ulong address) => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
    {
        var existing = Devices.FirstOrDefault(device => device.Address == address);
        if (existing is not null)
        {
            Devices.Remove(existing);
            UpdateDeviceCounts();
            OnPropertyChanged(nameof(HasNoDevices));
            OnPropertyChanged(nameof(EmptyStateText));
        }
    });

    private void UpdateDeviceCounts()
    {
        TotalDeviceCount = Devices.Count;
        HeartRateDeviceCount = Devices.Count(d => d.IsHeartRateCandidate);
    }

    private void OnBluetoothStatusChanged(object? sender, BluetoothScanState state) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => ApplyScanState(state));

    private void OnGattDisconnected(object? sender, ulong address) =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (SelectedDevice?.Address == address)
            {
                Status = $"{SelectedDevice.Name} 连接已断开";
                StatusKind = HeartRateStatusKind.Stale;
            }
        });

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

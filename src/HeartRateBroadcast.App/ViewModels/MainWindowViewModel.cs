using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
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

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IBluetoothAdvertisementService _bluetooth;
    private readonly IHeartRateGattClient _gattClient;
    private readonly IHeartRateStateService _state;
    private readonly IOverlayWindowManager _overlay;
    private readonly ISettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _saveDebounceTimer;
    private readonly DispatcherTimer _deviceUpdatesTimer;
    private readonly ObservableCollection<DiscoveredDevice> _devices = [];
    private readonly Dictionary<ulong, DiscoveredDevice> _devicesByAddress = [];
    private readonly object _deviceUpdatesGate = new();
    private readonly HashSet<ulong> _deliveredDeviceAddresses = [];
    private Dictionary<ulong, DeviceSnapshot?> _pendingDeviceUpdates = [];
    private Dictionary<ulong, DeviceSnapshot?> _deviceUpdatesToApply = [];
    private string _searchQuery = string.Empty;
    private bool _hasNoDevices = true;
    private string _emptyStateText = string.Empty;
    private bool _deviceViewActive;
    private bool _disposed;

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
    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    public bool HasNoDevices
    {
        get => _hasNoDevices;
        private set => SetProperty(ref _hasNoDevices, value);
    }

    public string EmptyStateText
    {
        get => _emptyStateText;
        private set => SetProperty(ref _emptyStateText, value);
    }

    public System.Windows.Media.Brush StatusBrush => StatusKind switch
    {
        HeartRateStatusKind.Live => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Live"],
        HeartRateStatusKind.Error or HeartRateStatusKind.BluetoothUnavailable => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Error"],
        HeartRateStatusKind.Stale => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Warn"],
        _ => (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Info"],
    };

    public string OverlayLockButtonText => IsOverlayLocked ? "解锁位置" : "锁定位置";
    public string OverlayBackgroundButtonText => IsOverlayBackgroundHidden ? "显示背景" : "隐藏背景";
    public string OverlayClickThroughButtonText => IsOverlayClickThrough ? "关闭穿透" : "开启穿透";

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
        _dispatcher = Application.Current.Dispatcher;
        Overlay = overlayViewModel;

        var devicesView = (ListCollectionView)CollectionViewSource.GetDefaultView(_devices);
        using (devicesView.DeferRefresh())
        {
            devicesView.Filter = FilterDevicePredicate;
            devicesView.SortDescriptions.Add(new SortDescription(nameof(DiscoveredDevice.IsHeartRateCandidate), ListSortDirection.Descending));
            devicesView.SortDescriptions.Add(new SortDescription(nameof(DiscoveredDevice.Rssi), ListSortDirection.Descending));
            devicesView.LiveFilteringProperties.Add(nameof(DiscoveredDevice.Name));
            devicesView.LiveFilteringProperties.Add(nameof(DiscoveredDevice.IsHeartRateCandidate));
            devicesView.IsLiveSorting = true;
            devicesView.IsLiveFiltering = true;
        }
        FilteredDevices = devicesView;
        ((INotifyPropertyChanged)devicesView).PropertyChanged += OnFilteredDevicesPropertyChanged;

        _deviceUpdatesTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _deviceUpdatesTimer.Tick += ApplyPendingDeviceUpdates;

        _saveDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _saveDebounceTimer.Tick += OnSaveDebounceTick;

        RefreshOverlayControls();
        _state.DeviceChanged += OnDeviceChanged;
        _state.DeviceRemoved += OnDeviceRemoved;
        _state.StateChanged += OnStateChanged;
        _bluetooth.StatusChanged += OnBluetoothStatusChanged;
        _gattClient.Disconnected += OnGattDisconnected;
        Overlay.PropertyChanged += OnOverlayPropertyChanged;
        _overlay.PlacementChanged += OnOverlayPlacementChanged;
        ApplyScanState(_bluetooth.CurrentState);
        UpdateEmptyState();
    }

    private async void OnSaveDebounceTick(object? sender, EventArgs e)
    {
        _saveDebounceTimer.Stop();
        await SaveSettingsSafelyAsync();
    }

    private void OnOverlayPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(OverlayViewModel.Bpm) or nameof(OverlayViewModel.Status) or nameof(OverlayViewModel.IsLive) or nameof(OverlayViewModel.IsLightText)))
            ScheduleSettingsSave();
    }

    private void OnOverlayPlacementChanged(object? sender, EventArgs e) => ScheduleSettingsSave();

    private void ScheduleSettingsSave()
    {
        if (_disposed) return;
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    private bool FilterDevicePredicate(object obj)
    {
        if (obj is not DiscoveredDevice device) return false;

        // 当前选中的目标设备始终予以显示
        if (SelectedDevice?.Address == device.Address || _settings.SelectedDeviceAddress == device.Address)
            return true;

        if (_searchQuery.Length > 0 &&
            !device.Name.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase) &&
            !device.AddressText.Contains(_searchQuery, StringComparison.OrdinalIgnoreCase))
            return false;

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
        if (_disposed) return;
        FilteredDevices.Refresh();
        UpdateEmptyState();
    }

    private void OnFilteredDevicesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ICollectionView.IsEmpty)) UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        HasNoDevices = FilteredDevices.IsEmpty;
        EmptyStateText = HasNoDevices switch
        {
            false => string.Empty,
            true when _devices.Count == 0 => IsScanning ? "正在搜索附近的蓝牙设备…" : "未发现设备。开启蓝牙后开始扫描。",
            true when _searchQuery.Length > 0 => "没有匹配的设备。",
            true when FilterHeartRateOnly => "未发现心率设备。请开启设备的心率共享，或查看全部设备。",
            true when HideUnknownDevices => "没有已命名的设备。可取消隐藏无名设备。",
            _ => "没有符合筛选条件的设备。",
        };
    }

    partial void OnFilterHeartRateOnlyChanged(bool value) => ApplyFilter();
    partial void OnHideUnknownDevicesChanged(bool value) => ApplyFilter();
    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasSearchText));
        var query = value?.Trim() ?? string.Empty;
        if (_searchQuery == query) return;
        _searchQuery = query;
        ApplyFilter();
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void ShowHeartRateOnly() => FilterHeartRateOnly = true;

    [RelayCommand]
    private void ShowAllDevices() => FilterHeartRateOnly = false;

    partial void OnSelectedDeviceChanged(DiscoveredDevice? oldValue, DiscoveredDevice? newValue)
    {
        _state.SelectedAddress = newValue?.Address;
        _settings.SelectedDeviceAddress = newValue?.Address;
        Status = newValue is null ? "请选择设备" : $"已选择 {newValue.Name}";
        ConnectSelectedDeviceCommand.NotifyCanExecuteChanged();
        if (oldValue is not null && !FilterDevicePredicate(oldValue))
            _dispatcher.BeginInvoke(DispatcherPriority.DataBind, (Action)ApplyFilter);
        _ = SaveSettingsSafelyAsync();
    }

    partial void OnStatusKindChanged(HeartRateStatusKind value) => OnPropertyChanged(nameof(StatusBrush));

    partial void OnIsScanningChanged(bool value)
    {
        OnPropertyChanged(nameof(ScanButtonText));
        UpdateEmptyState();
    }

    partial void OnIsConnectingChanged(bool value)
    {
        OnPropertyChanged(nameof(ConnectButtonText));
        ConnectSelectedDeviceCommand.NotifyCanExecuteChanged();
        CancelConnectCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsOverlayLockedChanged(bool value) => OnPropertyChanged(nameof(OverlayLockButtonText));
    partial void OnIsOverlayBackgroundHiddenChanged(bool value) => OnPropertyChanged(nameof(OverlayBackgroundButtonText));
    partial void OnIsOverlayClickThroughChanged(bool value) => OnPropertyChanged(nameof(OverlayClickThroughButtonText));

    private void OnDeviceChanged(object? sender, DeviceSnapshot device) => QueueDeviceUpdate(device.Address, device);
    private void OnDeviceRemoved(object? sender, ulong address) => QueueDeviceUpdate(address, null);

    public void SetDeviceViewActive(bool active)
    {
        _dispatcher.VerifyAccess();
        lock (_deviceUpdatesGate)
        {
            if (_disposed || _deviceViewActive == active) return;
            _deviceViewActive = active;
            if (!active) _deviceUpdatesTimer.Stop();
            else if (_pendingDeviceUpdates.Count > 0) _deviceUpdatesTimer.Start();
        }
    }

    private void QueueDeviceUpdate(ulong address, DeviceSnapshot? update)
    {
        lock (_deviceUpdatesGate)
        {
            if (_disposed) return;
            if (update is null && !_deliveredDeviceAddresses.Contains(address))
            {
                _pendingDeviceUpdates.Remove(address);
                return;
            }

            bool startTimer = _deviceViewActive && _pendingDeviceUpdates.Count == 0;
            _pendingDeviceUpdates[address] = update;
            if (startTimer) _deviceUpdatesTimer.Start();
        }
    }

    private void ApplyPendingDeviceUpdates(object? sender, EventArgs e)
    {
        lock (_deviceUpdatesGate)
        {
            _deviceUpdatesTimer.Stop();
            if (_disposed || !_deviceViewActive || _pendingDeviceUpdates.Count == 0) return;

            // Give producers an empty buffer while the dispatcher updates the view.
            (_pendingDeviceUpdates, _deviceUpdatesToApply) = (_deviceUpdatesToApply, _pendingDeviceUpdates);
            foreach (var (address, update) in _deviceUpdatesToApply)
            {
                if (update.HasValue) _deliveredDeviceAddresses.Add(address);
                else _deliveredDeviceAddresses.Remove(address);
            }
        }

        int heartRateCount = HeartRateDeviceCount;
        foreach (var (address, update) in _deviceUpdatesToApply)
        {
            if (update is { } device)
                heartRateCount += ApplyDeviceSnapshot(device);
            else if (_devicesByAddress.Remove(address, out var removed))
            {
                _devices.Remove(removed);
                if (removed.IsHeartRateCandidate) heartRateCount--;
            }
        }
        _deviceUpdatesToApply.Clear();
        TotalDeviceCount = _devices.Count;
        HeartRateDeviceCount = heartRateCount;
        UpdateEmptyState();
    }

    private int ApplyDeviceSnapshot(DeviceSnapshot snapshot)
    {
        bool wasCandidate = false;
        if (_devicesByAddress.TryGetValue(snapshot.Address, out var device))
        {
            wasCandidate = device.IsHeartRateCandidate;
            device.Rssi = snapshot.Rssi;
            device.LastSeen = snapshot.LastSeen;
            device.Name = snapshot.Name;
            device.IsHeartRateCapable = snapshot.IsHeartRateCapable;
            device.IsHeartRateBroadcasting = snapshot.IsHeartRateBroadcasting;
            device.HasHeartRateService = snapshot.HasHeartRateService;
        }
        else
        {
            device = new DiscoveredDevice(snapshot.Address, snapshot.Name, snapshot.Rssi, snapshot.LastSeen,
                snapshot.IsHeartRateCapable, snapshot.IsHeartRateBroadcasting, snapshot.HasHeartRateService);
            _devicesByAddress.Add(snapshot.Address, device);
            _devices.Add(device);
        }

        if (_settings.SelectedDeviceAddress == snapshot.Address && SelectedDevice != device)
            SelectedDevice = device;

        return (device.IsHeartRateCandidate ? 1 : 0) - (wasCandidate ? 1 : 0);
    }

    private void OnStateChanged(object? sender, HeartRateDisplayState value) => _dispatcher.BeginInvoke(() =>
    {
        CurrentBpm = value.BeatsPerMinute?.ToString() ?? "--";
        Status = value.Message;
        StatusKind = value.Kind;
    });

    private void OnBluetoothStatusChanged(object? sender, BluetoothScanState state) =>
        _dispatcher.BeginInvoke(() => ApplyScanState(state));

    private void OnGattDisconnected(object? sender, ulong address) =>
        _dispatcher.BeginInvoke(() =>
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
    [RelayCommand]
    private void ShowOverlay()
    {
        _overlay.Show();
        RefreshOverlayControls();
    }

    public void RefreshOverlayControls()
    {
        _dispatcher.VerifyAccess();
        IsOverlayLocked = _overlay.IsLocked;
        IsOverlayBackgroundHidden = _overlay.IsBackgroundHidden;
        IsOverlayClickThrough = _overlay.IsClickThrough;
    }

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
        await SaveSettingsSafelyAsync();
    }

    [RelayCommand]
    private async Task ToggleOverlayBackgroundAsync()
    {
        IsOverlayBackgroundHidden = !IsOverlayBackgroundHidden;
        _overlay.SetBackgroundHidden(IsOverlayBackgroundHidden);
        await SaveSettingsSafelyAsync();
    }

    [RelayCommand]
    private async Task ToggleOverlayClickThroughAsync()
    {
        IsOverlayClickThrough = !IsOverlayClickThrough;
        _overlay.SetClickThrough(IsOverlayClickThrough);
        IsOverlayClickThrough = _overlay.IsClickThrough;
        await SaveSettingsSafelyAsync();
    }

    public void Dispose()
    {
        lock (_deviceUpdatesGate)
        {
            _disposed = true;
            _deviceUpdatesTimer.Stop();
            _pendingDeviceUpdates.Clear();
            _deliveredDeviceAddresses.Clear();
        }

        _saveDebounceTimer.Stop();
        _state.DeviceChanged -= OnDeviceChanged;
        _state.DeviceRemoved -= OnDeviceRemoved;
        _state.StateChanged -= OnStateChanged;
        _bluetooth.StatusChanged -= OnBluetoothStatusChanged;
        _gattClient.Disconnected -= OnGattDisconnected;
        Overlay.PropertyChanged -= OnOverlayPropertyChanged;
        _overlay.PlacementChanged -= OnOverlayPlacementChanged;
        ((INotifyPropertyChanged)FilteredDevices).PropertyChanged -= OnFilteredDevicesPropertyChanged;
    }
}

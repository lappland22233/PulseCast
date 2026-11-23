using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using HeartRateBroadcast.App.Interop;
using HeartRateBroadcast.App.Models;
using HeartRateBroadcast.App.Views;
using Microsoft.Win32;

namespace HeartRateBroadcast.App.Services.Overlay;

public sealed class TopmostWindowService : IDisposable
{
    private readonly Window _window;
    private readonly DispatcherTimer _watchdog;
    private nint _handle;

    public TopmostWindowService(Window window)
    {
        _window = window;
        _watchdog = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        _watchdog.Tick += OnWatchdogTick;
        _window.IsVisibleChanged += OnVisibilityChanged;
    }

    public void Attach(bool clickThrough)
    {
        _handle = new WindowInteropHelper(_window).Handle;
        if (_handle == 0) throw new InvalidOperationException("悬浮窗句柄尚未初始化");
        UpdateWatchdog();
        SetClickThrough(clickThrough);
        EnsureTopmost();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateWatchdog();

    private void UpdateWatchdog() => _watchdog.IsEnabled = _handle != 0 && _window.IsVisible;

    private void OnWatchdogTick(object? sender, EventArgs e)
    {
        try { EnsureTopmost(); }
        catch (Win32Exception) { }
    }

    public void SetClickThrough(bool clickThrough)
    {
        if (_handle == 0) return;
        Marshal.GetLastWin32Error();
        long styles = NativeMethods.GetWindowLongPtr(_handle, NativeMethods.GwlExStyle).ToInt64();
        int error = Marshal.GetLastWin32Error();
        if (styles == 0 && error != 0) throw new Win32Exception(error);
        styles |= NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate;
        if (clickThrough) styles |= NativeMethods.WsExTransparent;
        else styles &= ~NativeMethods.WsExTransparent;
        Marshal.GetLastWin32Error();
        nint previous = NativeMethods.SetWindowLongPtr(_handle, NativeMethods.GwlExStyle, new(styles));
        error = Marshal.GetLastWin32Error();
        if (previous == 0 && error != 0) throw new Win32Exception(error);
    }

    public void EnsureTopmost()
    {
        if (_handle != 0 && _window.IsVisible &&
            !NativeMethods.SetWindowPos(_handle, NativeMethods.HwndTopmost, 0, 0, 0, 0,
                NativeMethods.SwpNoActivate | NativeMethods.SwpNoMove | NativeMethods.SwpNoSize))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose()
    {
        _window.IsVisibleChanged -= OnVisibilityChanged;
        _watchdog.Stop();
        _watchdog.Tick -= OnWatchdogTick;
    }
}

public sealed class DisplayPlacementService
{
    public Rect Calculate(Rect workArea, OverlaySettings settings, double? actualWidth = null, double? actualHeight = null)
    {
        double width = Math.Min(ValidSize(actualWidth, settings.Width), workArea.Width);
        double height = Math.Min(ValidSize(actualHeight, settings.Height), workArea.Height);
        double x = Math.Clamp(workArea.Right - settings.RightOffset - width, workArea.Left, workArea.Right - width);
        double y = Math.Clamp(workArea.Top + settings.TopOffset, workArea.Top, workArea.Bottom - height);
        return new(x, y, width, height);
    }

    public void Apply(Window window, OverlaySettings settings)
    {
        Rect area = GetWorkAreaDip(window, out string deviceName);
        if (!string.IsNullOrWhiteSpace(deviceName)) settings.MonitorDeviceName = deviceName;
        Rect target = Calculate(area, settings, window.ActualWidth, window.ActualHeight);
        window.MaxWidth = area.Width;
        window.MaxHeight = area.Height;
        window.Left = target.Left;
        window.Top = target.Top;
    }

    public Rect GetWorkAreaDip(Window window, out string deviceName)
    {
        nint hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0)
        {
            deviceName = string.Empty;
            return SystemParameters.WorkArea;
        }

        nint monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfoEx
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
            Device = string.Empty,
        };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            deviceName = string.Empty;
            return SystemParameters.WorkArea;
        }

        deviceName = info.Device ?? string.Empty;
        var source = PresentationSource.FromVisual(window);
        if (source?.CompositionTarget is null)
            return new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        var transform = source.CompositionTarget.TransformFromDevice;
        Point topLeft = transform.Transform(new Point(info.Work.Left, info.Work.Top));
        Point bottomRight = transform.Transform(new Point(info.Work.Right, info.Work.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private static double ValidSize(double? actual, double configured) =>
        actual is > 0 and < double.PositiveInfinity ? actual.Value : configured;
}

public interface IOverlayWindowManager
{
    bool IsVisible { get; }
    bool IsLocked { get; }
    bool IsBackgroundHidden { get; }
    bool IsClickThrough { get; }
    event EventHandler? PlacementChanged;
    void Show();
    void Hide();
    void SetLocked(bool locked);
    void SetBackgroundHidden(bool hidden);
    void SetClickThrough(bool enabled);
    void RestoreTopmost();
}

public sealed class OverlayWindowManager : IOverlayWindowManager, IDisposable
{
    private readonly Func<OverlayWindow> _createWindow;
    private readonly OverlaySettings _settings;
    private readonly DisplayPlacementService _placement;
    private OverlayWindow? _window;
    private TopmostWindowService? _topmost;
    private DispatcherTimer? _displayDebounce;
    private HwndSource? _source;
    private bool _disposed;

    public bool IsVisible => _window?.IsVisible == true;
    public bool IsLocked => _settings.Locked;
    public bool IsBackgroundHidden => _settings.HideBackground;
    public bool IsClickThrough => _settings.ClickThrough;
    public event EventHandler? PlacementChanged;

    public OverlayWindowManager(Func<OverlayWindow> createWindow, AppSettings settings, DisplayPlacementService placement)
    {
        _createWindow = createWindow;
        _settings = settings.Overlay;
        _placement = placement;
    }

    private OverlayWindow CreateWindow()
    {
        var window = _createWindow();
        _topmost = new(window);
        _displayDebounce = new DispatcherTimer(DispatcherPriority.Loaded, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _displayDebounce.Tick += OnDisplayDebounce;
        window.IsPositionLocked = _settings.Locked;
        window.SetBackgroundHidden(_settings.HideBackground);
        window.SourceInitialized += OnSourceInitialized;
        window.LocationChanged += OnWindowLocationChanged;
        window.SizeChanged += OnWindowSizeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        return window;
    }

    public void Show()
    {
        if (_disposed) return;
        var window = _window ??= CreateWindow();
        if (!window.IsVisible) window.Show();
        window.Dispatcher.BeginInvoke(ApplyPlacement, DispatcherPriority.Loaded);
    }

    public void Hide()
    {
        _displayDebounce?.Stop();
        if (_window is { IsVisible: true } window) window.Hide();
    }

    public void SetLocked(bool locked)
    {
        _settings.Locked = locked;
        if (_window is { } window) window.IsPositionLocked = locked;
    }

    public void SetBackgroundHidden(bool hidden)
    {
        _settings.HideBackground = hidden;
        _window?.SetBackgroundHidden(hidden);
    }

    public void SetClickThrough(bool enabled)
    {
        _settings.ClickThrough = enabled;
        try { _topmost?.SetClickThrough(enabled); }
        catch (Win32Exception) { _settings.ClickThrough = false; }
    }

    public void RestoreTopmost() => TryEnsureTopmost();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (_window is not { } window) return;
        try { _topmost?.Attach(_settings.ClickThrough); }
        catch (Win32Exception) { _settings.ClickThrough = false; }
        _source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
        _source?.AddHook(WindowProc);
    }

    private void OnWindowLocationChanged(object? sender, EventArgs e) => SavePlacement();
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => ScheduleDisplayUpdate();

    private void SavePlacement()
    {
        if (_disposed || _settings.Locked || _window is not { IsVisible: true } window) return;
        Rect area = _placement.GetWorkAreaDip(window, out string deviceName);
        _settings.MonitorDeviceName = deviceName;
        _settings.RightOffset = Math.Max(0, area.Right - window.Left - window.ActualWidth);
        _settings.TopOffset = Math.Max(0, window.Top - area.Top);
        PlacementChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => _window?.Dispatcher.BeginInvoke(ScheduleDisplayUpdate);

    private void ScheduleDisplayUpdate()
    {
        if (_disposed || _window?.IsVisible != true || _displayDebounce is not { } timer) return;
        timer.Stop();
        timer.Start();
    }

    private void OnDisplayDebounce(object? sender, EventArgs e)
    {
        _displayDebounce?.Stop();
        ApplyPlacement();
    }

    private void ApplyPlacement()
    {
        if (_disposed || _window is not { IsVisible: true } window) return;
        _placement.Apply(window, _settings);
        TryEnsureTopmost();
    }

    private nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg is NativeMethods.WmDpiChanged or NativeMethods.WmDisplayChange or NativeMethods.WmSettingChange)
            ScheduleDisplayUpdate();
        return 0;
    }

    private void TryEnsureTopmost()
    {
        try { _topmost?.EnsureTopmost(); }
        catch (Win32Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_displayDebounce is { } timer)
        {
            timer.Stop();
            timer.Tick -= OnDisplayDebounce;
        }

        if (_window is { } window)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            window.SourceInitialized -= OnSourceInitialized;
            window.LocationChanged -= OnWindowLocationChanged;
            window.SizeChanged -= OnWindowSizeChanged;
        }

        _source?.RemoveHook(WindowProc);
        _source = null;
        _topmost?.Dispose();
    }
}

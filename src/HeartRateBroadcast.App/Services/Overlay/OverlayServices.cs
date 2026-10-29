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
    private readonly System.Threading.Timer _watchdog;
    private nint _handle;
    private bool _disposed;

    public TopmostWindowService(Window window)
    {
        _window = window;
        _watchdog = new(_ =>
        {
            if (!_disposed)
            {
                _window.Dispatcher.BeginInvoke(() =>
                {
                    try { EnsureTopmost(); }
                    catch (Win32Exception) { }
                });
            }
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Attach(bool clickThrough)
    {
        _handle = new WindowInteropHelper(_window).Handle;
        if (_handle == 0) throw new InvalidOperationException("悬浮窗句柄尚未初始化");
        SetClickThrough(clickThrough);
        EnsureTopmost();
        _watchdog.Change(1500, 1500);
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
        _disposed = true;
        _watchdog.Dispose();
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
    OverlayWindow Window { get; }
    bool IsLocked { get; }
    bool IsBackgroundHidden { get; }
    bool IsClickThrough { get; }
    event EventHandler? PlacementChanged;
    void Show();
    void SetLocked(bool locked);
    void SetBackgroundHidden(bool hidden);
    void SetClickThrough(bool enabled);
    void RestoreTopmost();
}

public sealed class OverlayWindowManager : IOverlayWindowManager, IDisposable
{
    private readonly OverlaySettings _settings;
    private readonly DisplayPlacementService _placement;
    private readonly TopmostWindowService _topmost;
    private readonly DispatcherTimer _displayDebounce;
    private HwndSource? _source;
    private bool _disposed;

    public OverlayWindow Window { get; }
    public bool IsLocked => _settings.Locked;
    public bool IsBackgroundHidden => _settings.HideBackground;
    public bool IsClickThrough => _settings.ClickThrough;
    public event EventHandler? PlacementChanged;

    public OverlayWindowManager(OverlayWindow window, AppSettings settings, DisplayPlacementService placement)
    {
        Window = window;
        _settings = settings.Overlay;
        _placement = placement;
        _topmost = new(window);
        _displayDebounce = new DispatcherTimer(DispatcherPriority.Loaded, Window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(120),
        };
        _displayDebounce.Tick += OnDisplayDebounce;
        Window.IsPositionLocked = _settings.Locked;
        Window.SetBackgroundHidden(_settings.HideBackground);
        Window.SourceInitialized += OnSourceInitialized;
        Window.LocationChanged += OnWindowLocationChanged;
        Window.SizeChanged += OnWindowSizeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    public void Show()
    {
        if (!Window.IsVisible) Window.Show();
        Window.Dispatcher.BeginInvoke(() =>
        {
            _placement.Apply(Window, _settings);
            TryEnsureTopmost();
        }, DispatcherPriority.Loaded);
    }

    public void SetLocked(bool locked)
    {
        _settings.Locked = locked;
        Window.IsPositionLocked = locked;
    }

    public void SetBackgroundHidden(bool hidden)
    {
        _settings.HideBackground = hidden;
        Window.SetBackgroundHidden(hidden);
    }

    public void SetClickThrough(bool enabled)
    {
        _settings.ClickThrough = enabled;
        try { _topmost.SetClickThrough(enabled); }
        catch (Win32Exception) { _settings.ClickThrough = false; }
    }

    public void RestoreTopmost() => TryEnsureTopmost();

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try { _topmost.Attach(_settings.ClickThrough); }
        catch (Win32Exception) { _settings.ClickThrough = false; }
        _source = HwndSource.FromHwnd(new WindowInteropHelper(Window).Handle);
        _source?.AddHook(WindowProc);
    }

    private void OnWindowLocationChanged(object? sender, EventArgs e) => SavePlacement();
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) => ScheduleDisplayUpdate();

    private void SavePlacement()
    {
        if (!Window.IsVisible || _settings.Locked || _disposed) return;
        Rect area = _placement.GetWorkAreaDip(Window, out string deviceName);
        _settings.MonitorDeviceName = deviceName;
        _settings.RightOffset = Math.Max(0, area.Right - Window.Left - Window.ActualWidth);
        _settings.TopOffset = Math.Max(0, Window.Top - area.Top);
        PlacementChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => Window.Dispatcher.BeginInvoke(ScheduleDisplayUpdate);

    private void ScheduleDisplayUpdate()
    {
        if (_disposed) return;
        _displayDebounce.Stop();
        _displayDebounce.Start();
    }

    private void OnDisplayDebounce(object? sender, EventArgs e)
    {
        _displayDebounce.Stop();
        _placement.Apply(Window, _settings);
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
        try { _topmost.EnsureTopmost(); }
        catch (Win32Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _displayDebounce.Stop();
        _displayDebounce.Tick -= OnDisplayDebounce;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        Window.SourceInitialized -= OnSourceInitialized;
        Window.LocationChanged -= OnWindowLocationChanged;
        Window.SizeChanged -= OnWindowSizeChanged;
        _source?.RemoveHook(WindowProc);
        _source = null;
        _topmost.Dispose();
    }
}

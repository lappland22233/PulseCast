using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using HeartRateBroadcast.App.Interop;

namespace HeartRateBroadcast.App.Services.Tray;

public enum TrayCommand
{
    OpenMainWindow = 1,
    ToggleOverlay,
    ToggleLock,
    ToggleBackground,
    ToggleClickThrough,
    Exit,
}

public readonly record struct TrayMenuState(
    bool OverlayVisible,
    bool OverlayLocked,
    bool OverlayBackgroundHidden,
    bool OverlayClickThrough);

public sealed class TrayIcon : IDisposable
{
    private const int TrayCallbackMessage = 0x8001; // WM_APP + 1
    private const int WmContextMenu = 0x007B;
    private const int WmLeftDoubleClick = 0x0203;
    private const int WmRightButtonUp = 0x0205;
    private const int NinKeySelect = 0x0401;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NimSetFocus = 3;
    private const uint NimSetVersion = 4;
    private const uint NotifyIconVersion4 = 4;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const uint NifShowTip = 0x80;
    private const uint MenuChecked = 0x08;
    private const uint MenuSeparator = 0x0800;
    private const uint MenuRightButton = 0x0002;
    private const uint MenuBottomAlign = 0x0020;
    private const uint MenuNoNotify = 0x0080;
    private const uint MenuReturnCommand = 0x0100;

    private readonly HwndSource _source;
    private readonly Func<TrayMenuState> _readState;
    private readonly Action<TrayCommand> _execute;
    private readonly int _taskbarCreated;
    private readonly bool _ownsIcon;
    private NativeMethods.NotifyIconData _iconData;
    private DispatcherOperation? _menuOperation;
    private bool _version4;
    private bool _disposed;

    public TrayIcon(Func<TrayMenuState> readState, Action<TrayCommand> execute)
    {
        _readState = readState;
        _execute = execute;
        _taskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        if (_taskbarCreated == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());

        // A hidden top-level HWND receives Explorer broadcasts without depending on the main window.
        _source = new HwndSource(new HwndSourceParameters("PulseCast.Tray", 0, 0)
        {
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = (int)NativeMethods.WsExToolWindow,
        });

        try
        {
            (nint icon, _ownsIcon) = LoadApplicationIcon();
            _iconData = new NativeMethods.NotifyIconData
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconData>(),
                Window = _source.Handle,
                Id = 1,
                Flags = NifMessage | NifIcon | NifTip | NifShowTip,
                CallbackMessage = TrayCallbackMessage,
                Icon = icon,
                Tip = "PulseCast 心率广播",
                TimeoutOrVersion = NotifyIconVersion4,
            };
            _source.AddHook(OnWindowMessage);
            if (!RegisterIcon()) throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法添加 PulseCast 通知区域图标。");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static (nint Icon, bool Owned) LoadApplicationIcon()
    {
        string? path = Environment.ProcessPath;
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            path = typeof(TrayIcon).Assembly.Location;
        if (!string.IsNullOrEmpty(path) && NativeMethods.ExtractSmallIcon(path, 0, 0, out nint icon, 1) == 1 && icon != 0)
            return (icon, true);

        nint systemIcon = NativeMethods.LoadIcon(0, 32512); // IDI_APPLICATION
        if (systemIcon == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return (systemIcon, false);
    }

    private bool RegisterIcon()
    {
        if (!NativeMethods.ShellNotifyIcon(NimAdd, ref _iconData)) return false;
        _version4 = NativeMethods.ShellNotifyIcon(NimSetVersion, ref _iconData);
        return true;
    }

    private nint OnWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (_disposed) return 0;
        if (message == _taskbarCreated)
        {
            RegisterIcon();
            return 0;
        }

        if (message != TrayCallbackMessage) return 0;
        handled = true;
        int notification = unchecked((int)lParam) & 0xffff;
        if (notification == WmLeftDoubleClick || (_version4 && notification == NinKeySelect))
        {
            _source.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() => Execute(TrayCommand.OpenMainWindow)));
        }
        else if (notification == (_version4 ? WmContextMenu : WmRightButtonUp) && _menuOperation is null)
        {
            _menuOperation = _source.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)ShowMenu);
        }

        return 0;
    }

    private void ShowMenu()
    {
        uint selection = 0;
        try
        {
            if (!_disposed) selection = OpenPopup();
        }
        finally
        {
            _menuOperation = null;
        }

        if (selection != 0) Execute((TrayCommand)selection);
    }

    private uint OpenPopup()
    {
        nint menu = NativeMethods.CreatePopupMenu();
        if (menu == 0) return 0;

        try
        {
            if (!PopulateMenu(menu, _readState()) || !TryGetMenuPosition(out var position)) return 0;

            nint owner = _source.Handle;
            NativeMethods.SetForegroundWindow(owner);
            uint selected = NativeMethods.TrackPopupMenuEx(menu, MenuRightButton | MenuBottomAlign |
                MenuNoNotify | MenuReturnCommand, position.X, position.Y, owner, 0);

            if (!_disposed)
            {
                // Let the next menu close when a click lands outside it.
                NativeMethods.PostMessage(owner, 0, 0, 0); // WM_NULL
                if (selected == 0) NativeMethods.ShellNotifyIcon(NimSetFocus, ref _iconData);
            }

            return selected;
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private static bool PopulateMenu(nint menu, TrayMenuState state) =>
        AppendItem(menu, TrayCommand.OpenMainWindow, "打开主窗口") &&
        NativeMethods.AppendMenu(menu, MenuSeparator, 0, null) &&
        AppendItem(menu, TrayCommand.ToggleOverlay, state.OverlayVisible ? "隐藏悬浮窗" : "显示悬浮窗") &&
        AppendItem(menu, TrayCommand.ToggleLock, "锁定悬浮窗位置", state.OverlayLocked) &&
        AppendItem(menu, TrayCommand.ToggleBackground, "透明背景", state.OverlayBackgroundHidden) &&
        AppendItem(menu, TrayCommand.ToggleClickThrough, "鼠标点击穿透", state.OverlayClickThrough) &&
        NativeMethods.AppendMenu(menu, MenuSeparator, 0, null) &&
        AppendItem(menu, TrayCommand.Exit, "退出 PulseCast");

    private static bool AppendItem(nint menu, TrayCommand command, string label, bool isChecked = false) =>
        NativeMethods.AppendMenu(menu, isChecked ? MenuChecked : 0, (nuint)command, label);

    private bool TryGetMenuPosition(out NativeMethods.Point position)
    {
        var identifier = new NativeMethods.NotifyIconIdentifier
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.NotifyIconIdentifier>(),
            Window = _iconData.Window,
            Id = _iconData.Id,
        };
        if (NativeMethods.ShellNotifyIconGetRect(in identifier, out var bounds) >= 0)
        {
            position = new NativeMethods.Point { X = (bounds.Left + bounds.Right) / 2, Y = (bounds.Top + bounds.Bottom) / 2 };
            return true;
        }

        return NativeMethods.GetCursorPos(out position);
    }

    private void Execute(TrayCommand command)
    {
        if (!_disposed) _execute(command);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _menuOperation?.Abort();
        _source.RemoveHook(OnWindowMessage);
        if (_iconData.Window != 0) NativeMethods.ShellNotifyIcon(NimDelete, ref _iconData);
        if (_ownsIcon) NativeMethods.DestroyIcon(_iconData.Icon);
        _source.Dispose();
    }
}

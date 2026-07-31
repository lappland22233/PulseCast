using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using HeartRateBroadcast.App.Interop;
using HeartRateBroadcast.App.ViewModels;

namespace HeartRateBroadcast.App;

public partial class MainWindow : Window
{
    private bool _closeApproved;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += OnClosing;
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, (_, _) =>
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, (_, _) => SystemCommands.RestoreWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, (_, _) => SystemCommands.CloseWindow(this)));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        // Windows 11 启用 DWM 圆角（DWMWCP_ROUND=2）；旧系统调用失败可忽略
        int cornerPreference = 2;
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DwmwaWindowCornerPreference, in cornerPreference, sizeof(int));
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved) return;
        e.Cancel = true;
        IsEnabled = false;
        if (Application.Current is App app)
            await app.ShutdownAsync();
        _closeApproved = true;
        Close();
    }
}

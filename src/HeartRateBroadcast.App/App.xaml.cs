using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using HeartRateBroadcast.App.Models;
using HeartRateBroadcast.App.Services.Bluetooth;
using HeartRateBroadcast.App.Services.Diagnostics;
using HeartRateBroadcast.App.Services.HeartRate;
using HeartRateBroadcast.App.Services.Overlay;
using HeartRateBroadcast.App.Services.Parsing;
using HeartRateBroadcast.App.Services.Settings;
using HeartRateBroadcast.App.Services.Tray;
using HeartRateBroadcast.App.ViewModels;
using HeartRateBroadcast.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HeartRateBroadcast.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            var settingsStore = new JsonSettingsStore();
            AppSettings settings = await settingsStore.LoadAsync();
            settings.Normalize();
            var collection = new ServiceCollection();
            collection.AddSingleton(settings).AddSingleton<ISettingsStore>(settingsStore);
            collection.AddSingleton<IBluetoothAdvertisementService, BluetoothAdvertisementService>();
            collection.AddSingleton<IHeartRateGattClient, HeartRateGattClient>();
            collection.AddSingleton<AdvertisementDiagnosticBuffer>();
            collection.AddSingleton<IHeartRatePacketParser, StandardHeartRateAdvertisementParser>();
            collection.AddSingleton<HeartRateParserPipeline>();
            collection.AddSingleton<IHeartRateStateService, HeartRateStateService>();
            collection.AddSingleton<DisplayPlacementService>();
            collection.AddSingleton<IScreenLuminanceSampler, ScreenLuminanceSampler>();
            collection.AddSingleton<CharacterContrastService>();
            collection.AddSingleton<OverlayViewModel>();
            collection.AddSingleton<OverlayWindow>();
            collection.AddSingleton<Func<OverlayWindow>>(provider => () => provider.GetRequiredService<OverlayWindow>());
            collection.AddSingleton<IOverlayWindowManager, OverlayWindowManager>();
            collection.AddSingleton<MainWindowViewModel>();
            collection.AddTransient<MainWindow>();
            _services = collection.BuildServiceProvider();
            _services.GetRequiredService<IHeartRateStateService>().SelectedAddress = settings.SelectedDeviceAddress;
            ShowMainWindow();
            try
            {
                _trayIcon = new TrayIcon(ReadTrayState, ExecuteTrayCommand);
            }
            catch (Win32Exception ex)
            {
                MessageBox.Show(_mainWindow!, $"通知区域图标无法注册。关闭主窗口将退出 PulseCast。\n{ex.Message}",
                    "PulseCast", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            if (settingsStore.LoadWarning is not null)
                _services.GetRequiredService<MainWindowViewModel>().Status = settingsStore.LoadWarning;
            _services.GetRequiredService<IBluetoothAdvertisementService>().Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"PulseCast 启动失败：{ex.Message}", "启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    private void ShowMainWindow()
    {
        if (_exiting || _services is not { } services) return;

        if (_mainWindow is null)
        {
            var window = services.GetRequiredService<MainWindow>();
            _mainWindow = window;
            MainWindow = window;
            window.Closed += OnMainWindowClosed;
            window.Show();
            services.GetRequiredService<MainWindowViewModel>().SetDeviceViewActive(true);
        }

        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window) return;
        window.Closed -= OnMainWindowClosed;
        if (ReferenceEquals(MainWindow, window)) MainWindow = null;
        _mainWindow = null;
        _services?.GetRequiredService<MainWindowViewModel>().SetDeviceViewActive(false);
        if (_trayIcon is null && !_exiting) Dispatcher.BeginInvoke(ExitApplication);
    }

    private TrayMenuState ReadTrayState()
    {
        if (_services?.GetRequiredService<IOverlayWindowManager>() is not { } overlay) return default;
        return new(overlay.IsVisible, overlay.IsLocked, overlay.IsBackgroundHidden, overlay.IsClickThrough);
    }

    private void ExecuteTrayCommand(TrayCommand command)
    {
        if (_exiting || _services is not { } services) return;

        var viewModel = services.GetRequiredService<MainWindowViewModel>();
        switch (command)
        {
            case TrayCommand.OpenMainWindow:
                ShowMainWindow();
                break;
            case TrayCommand.Exit:
                ExitApplication();
                break;
            case TrayCommand.ToggleOverlay:
                var overlay = services.GetRequiredService<IOverlayWindowManager>();
                if (overlay.IsVisible) overlay.Hide();
                else viewModel.ShowOverlayCommand.Execute(null);
                break;
            case TrayCommand.ToggleLock:
                viewModel.ToggleOverlayLockCommand.Execute(null);
                break;
            case TrayCommand.ToggleBackground:
                viewModel.ToggleOverlayBackgroundCommand.Execute(null);
                break;
            case TrayCommand.ToggleClickThrough:
                viewModel.ToggleOverlayClickThroughCommand.Execute(null);
                break;
        }
    }

    private async void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        if (_mainWindow is { } window) window.IsEnabled = false;

        try { await ShutdownCoreAsync(); }
        finally { Shutdown(); }
    }

    private async Task ShutdownCoreAsync()
    {
        var services = _services;
        _services = null;
        if (services is null) return;

        try
        {
            services.GetRequiredService<IBluetoothAdvertisementService>().Stop();
            services.GetRequiredService<IHeartRateGattClient>().CancelConnect();
            var store = services.GetRequiredService<ISettingsStore>();
            await store.SaveAsync(services.GetRequiredService<AppSettings>());
            await store.FlushAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            MessageBox.Show($"设置保存未完成：{ex.Message}", "退出提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            await services.DisposeAsync();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}

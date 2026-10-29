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
using HeartRateBroadcast.App.ViewModels;
using HeartRateBroadcast.App.Views;
using Microsoft.Extensions.DependencyInjection;

namespace HeartRateBroadcast.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private Task? _shutdownTask;

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
            collection.AddSingleton<IOverlayWindowManager, OverlayWindowManager>();
            collection.AddSingleton<MainWindowViewModel>();
            collection.AddSingleton<MainWindow>();
            _services = collection.BuildServiceProvider();
            _services.GetRequiredService<IHeartRateStateService>().SelectedAddress = settings.SelectedDeviceAddress;
            MainWindow = _services.GetRequiredService<MainWindow>();
            MainWindow.Show();
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

    public Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

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
        base.OnExit(e);
    }
}

using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HeartRateBroadcast.App.Models;
using HeartRateBroadcast.App.Services.HeartRate;

namespace HeartRateBroadcast.App.ViewModels;

public partial class OverlayViewModel : ObservableObject
{
    public OverlaySettings Settings { get; }

    [ObservableProperty] private string bpm = "--";
    [ObservableProperty] private string status = "等待心率";
    [ObservableProperty] private bool isLive;

    /// <summary>反色判定结果：true = 深色背景，使用浅色字体。</summary>
    [ObservableProperty] private bool isLightText = true;

    // 样式自定义
    [ObservableProperty] private bool showHeart;
    [ObservableProperty] private bool showUnit;
    [ObservableProperty] private bool showLive;
    [ObservableProperty] private double scale;
    [ObservableProperty] private double overlayOpacity;
    [ObservableProperty] private string numberFont = "等宽";

    // 反色与字体颜色
    [ObservableProperty] private string inversionMode = "自动";
    [ObservableProperty] private string colorMode = "自动";
    [ObservableProperty] private string solidColor = "纯白";
    [ObservableProperty] private string gradientId = "红粉";
    [ObservableProperty] private bool hideBackground;

    public double NumberFontSize => Settings.FontSize * Scale;

    public FontFamily NumberFontFamily => NumberFont switch
    {
        "仪表" => new("Bahnschrift"),
        "无衬线" => new("Segoe UI"),
        _ => new("Cascadia Mono"),
    };

    public OverlayViewModel(IHeartRateStateService state, AppSettings settings)
    {
        Settings = settings.Overlay;
        showHeart = Settings.ShowHeart;
        showUnit = Settings.ShowUnit;
        showLive = Settings.ShowLive;
        scale = Settings.Scale;
        overlayOpacity = Settings.Opacity;
        numberFont = Settings.NumberFont;
        inversionMode = Settings.InversionMode;
        colorMode = Settings.ColorMode;
        solidColor = Settings.SolidColor;
        gradientId = Settings.GradientId;
        hideBackground = Settings.HideBackground;

        state.StateChanged += (_, value) => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Bpm = value.BeatsPerMinute?.ToString() ?? "--";
            Status = value.Message;
            IsLive = value.Kind == HeartRateStatusKind.Live;
        });
    }

    partial void OnScaleChanged(double value)
    {
        Settings.Scale = value;
        OnPropertyChanged(nameof(NumberFontSize));
    }
    partial void OnOverlayOpacityChanged(double value) => Settings.Opacity = value;
    partial void OnNumberFontChanged(string value) { Settings.NumberFont = value; OnPropertyChanged(nameof(NumberFontFamily)); }
    partial void OnShowHeartChanged(bool value) => Settings.ShowHeart = value;
    partial void OnShowUnitChanged(bool value) => Settings.ShowUnit = value;
    partial void OnShowLiveChanged(bool value) => Settings.ShowLive = value;
    partial void OnInversionModeChanged(string value) => Settings.InversionMode = value;
    partial void OnColorModeChanged(string value) => Settings.ColorMode = value;
    partial void OnSolidColorChanged(string value) => Settings.SolidColor = value;
    partial void OnGradientIdChanged(string value) => Settings.GradientId = value;
    partial void OnHideBackgroundChanged(bool value) => Settings.HideBackground = value;
}

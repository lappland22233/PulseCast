using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using HeartRateBroadcast.App.Interop;
using HeartRateBroadcast.App.Services.Overlay;
using HeartRateBroadcast.App.ViewModels;

namespace HeartRateBroadcast.App.Views;

public partial class OverlayWindow : Window
{
    // 自适应前景色：深底浅字 / 浅底深字
    private static readonly Color LightFg = Color.FromRgb(0xF6, 0xF8, 0xFB);
    private static readonly Color DarkFg = Color.FromRgb(0x16, 0x1A, 0x21);
    private static readonly Color HeartOnDark = Color.FromRgb(0xFF, 0x4D, 0x6D);
    private static readonly Color HeartOnLight = Color.FromRgb(0xD6, 0x3A, 0x58);
    private static readonly Color LiveOnDark = Color.FromRgb(0x22, 0xC5, 0x5E);
    private static readonly Color LiveOnLight = Color.FromRgb(0x15, 0x80, 0x3D);

    private readonly OverlayViewModel _vm;
    private readonly IScreenLuminanceSampler _sampler;
    private readonly InversionDecider _decider = new();
    private System.Windows.Threading.DispatcherTimer? _inversionTimer;
    private Storyboard? _beat;
    private bool? _appliedLightText;
    private CancellationTokenSource? _sampleCts;
    private bool _sampleInProgress;
    private bool _disposed;

    public bool IsPositionLocked { get; set; }

    public OverlayWindow(OverlayViewModel viewModel, IScreenLuminanceSampler sampler)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        _sampler = sampler;
        Loaded += OnLoaded;
        Closed += OnClosed;
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyBackgroundMode();
        ApplyTheme(force: true);
        UpdateHeartBeat();
        if (_inversionTimer is null)
        {
            _inversionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
            _inversionTimer.Tick += OnInversionTick;
        }
        _inversionTimer.Start();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(OverlayViewModel.Bpm):
                RebuildBpmText();
                UpdateHeartBeat();
                break;
            case nameof(OverlayViewModel.IsLightText):
            case nameof(OverlayViewModel.ColorMode):
            case nameof(OverlayViewModel.SolidColor):
            case nameof(OverlayViewModel.GradientId):
            case nameof(OverlayViewModel.InversionMode):
                ApplyTheme(force: true);
                break;
            case nameof(OverlayViewModel.HideBackground):
                ApplyBackgroundMode();
                ApplyTheme();
                break;
        }
    }

    private async void OnInversionTick(object? sender, EventArgs e) => await SampleAndApplyAsync();

    /// <summary>在后台单航班采样悬浮窗外侧背景亮度，不改变窗口可见性。</summary>
    private async Task SampleAndApplyAsync()
    {
        if (_disposed || _sampleInProgress || !IsVisible || _vm.InversionMode != "自动" || !_vm.HideBackground) return;
        _sampleInProgress = true;
        _sampleCts?.Cancel();
        _sampleCts?.Dispose();
        _sampleCts = new CancellationTokenSource();
        try
        {
            double? luminance = await _sampler.SampleAsync(GetAdjacentScreenRegion(), _sampleCts.Token);
            if (!luminance.HasValue || _disposed) return;
            bool useLightText = _decider.ShouldUseLightText(luminance.Value);
            if (_vm.IsLightText != useLightText) _vm.IsLightText = useLightText;
        }
        catch (OperationCanceledException) { }
        finally
        {
            _sampleInProgress = false;
        }
    }

    private Int32Rect GetAdjacentScreenRegion()
    {
        nint hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == 0 || !NativeMethods.GetWindowRect(hwnd, out var windowRect)) return Int32Rect.Empty;
        nint monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfoEx
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfoEx>(),
            Device = string.Empty,
        };
        if (monitor == 0 || !NativeMethods.GetMonitorInfo(monitor, ref info)) return Int32Rect.Empty;

        int windowWidth = Math.Max(1, windowRect.Right - windowRect.Left);
        int windowHeight = Math.Max(1, windowRect.Bottom - windowRect.Top);
        int sampleWidth = Math.Min(Math.Clamp(windowWidth, 48, 160), info.Monitor.Right - info.Monitor.Left);
        int sampleHeight = Math.Min(Math.Clamp(windowHeight, 32, 80), info.Monitor.Bottom - info.Monitor.Top);
        int centerX = windowRect.Left + windowWidth / 2;
        int aboveY = windowRect.Top - 8 - sampleHeight;
        int belowY = windowRect.Bottom + 8;
        int y = aboveY >= info.Monitor.Top ? aboveY : Math.Min(belowY, info.Monitor.Bottom - sampleHeight);
        int x = Math.Clamp(centerX - sampleWidth / 2, info.Monitor.Left, info.Monitor.Right - sampleWidth);
        y = Math.Clamp(y, info.Monitor.Top, info.Monitor.Bottom - sampleHeight);
        return new Int32Rect(x, y, sampleWidth, sampleHeight);
    }

    /// <summary>根据反色判定与颜色模式设置前景色。</summary>
    private void ApplyTheme(bool force = false)
    {
        bool lightText = EffectiveLightText();
        if (!force && _appliedLightText == lightText) return;

        _appliedLightText = lightText;
        Color dim = lightText ? Color.FromArgb(0xCC, 0xF6, 0xF8, 0xFB) : Color.FromArgb(0xC7, 0x16, 0x1A, 0x21);
        HeartIcon.Fill = new SolidColorBrush(lightText ? HeartOnDark : HeartOnLight);
        LiveDot.Fill = new SolidColorBrush(lightText ? LiveOnDark : LiveOnLight);
        LiveText.Foreground = new SolidColorBrush(lightText ? LiveOnDark : LiveOnLight);
        UnitText.Foreground = new SolidColorBrush(dim);
        RebuildBpmText();
    }

    private bool EffectiveLightText()
    {
        bool lightText = _vm.InversionMode switch
        {
            "强制浅字" => true,
            "强制深字" => false,
            _ => _vm.IsLightText,
        };
        // 带背景卡片时自身提供对比，恒浅字
        if (!_vm.HideBackground) lightText = true;
        return lightText;
    }

    /// <summary>按颜色模式重建 BPM 文本：自动/单色为整体前景，渐变逐字上色。</summary>
    private void RebuildBpmText()
    {
        string text = _vm.Bpm;
        if (BpmText.Inlines.Count == 1 && BpmText.Inlines.FirstInline is Run existing && existing.Text == text &&
            _vm.ColorMode != "逐字渐变")
        {
            existing.Foreground = new SolidColorBrush(GetBpmColor());
            return;
        }

        BpmText.Inlines.Clear();

        if (_vm.ColorMode == "逐字渐变")
        {
            GetGradientColors(out Color from, out Color to);
            char[] chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                double t = chars.Length > 1 ? (double)i / (chars.Length - 1) : 0;
                BpmText.Inlines.Add(new Run(chars[i].ToString()) { Foreground = new SolidColorBrush(Lerp(from, to, t)) });
            }
        }
        else
        {
            BpmText.Inlines.Add(new Run(text) { Foreground = new SolidColorBrush(GetBpmColor()) });
        }
    }

    private Color GetBpmColor()
    {
        if (_vm.ColorMode == "单色" && TryParseColor(_vm.SolidColor, out Color solid)) return solid;
        return EffectiveLightText() ? LightFg : DarkFg;
    }

    private void GetGradientColors(out Color from, out Color to)
    {
        (from, to) = _vm.GradientId switch
        {
            "冰蓝" => (Color.FromRgb(0x36, 0xD1, 0xDC), Color.FromRgb(0x5B, 0x86, 0xE5)),
            "翠绿" => (Color.FromRgb(0x11, 0x99, 0x8E), Color.FromRgb(0x38, 0xEF, 0x7D)),
            "金橙" => (Color.FromRgb(0xF7, 0x97, 0x1E), Color.FromRgb(0xFF, 0xD2, 0x00)),
            _ => (Color.FromRgb(0xFF, 0x4D, 0x6D), Color.FromRgb(0xFF, 0xB1, 0x99)), // 红粉
        };
    }

    private static Color Lerp(Color a, Color b, double t) => Color.FromRgb(
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));

    private static bool TryParseColor(string value, out Color color)
    {
        switch (value)
        {
            case "纯白": color = Colors.White; return true;
            case "深黑": color = Color.FromRgb(0x16, 0x18, 0x1D); return true;
            case "心率红": color = Color.FromRgb(0xFF, 0x4D, 0x6D); return true;
            case "荧光绿": color = Color.FromRgb(0x22, 0xC5, 0x5E); return true;
            case "天蓝": color = Color.FromRgb(0x38, 0xBD, 0xF8); return true;
            case "琥珀": color = Color.FromRgb(0xFB, 0xBF, 0x24); return true;
            case "橙": color = Color.FromRgb(0xFB, 0x92, 0x3C); return true;
            case "玫红": color = Color.FromRgb(0xF4, 0x72, 0xB6); return true;
            default:
                try { color = (Color)ColorConverter.ConvertFromString(value); return true; }
                catch { color = Colors.White; return false; }
        }
    }

    /// <summary>背景模式：透明（依赖反色）或深色卡片（自带对比）。</summary>
    public void SetBackgroundHidden(bool hidden) => _vm.HideBackground = hidden;

    private void ApplyBackgroundMode()
    {
        if (_vm.HideBackground)
        {
            OverlayBackground.Background = Brushes.Transparent;
            OverlayBackground.BorderBrush = Brushes.Transparent;
        }
        else
        {
            OverlayBackground.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x15, 0x18, 0x21));
            OverlayBackground.BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF));
        }
    }

    /// <summary>心跳动画，时长随 BPM 同步。</summary>
    private void UpdateHeartBeat()
    {
        _beat?.Remove(this);
        if (!SystemParameters.ClientAreaAnimation || !double.TryParse(_vm.Bpm, out double bpm) || bpm <= 0) return;
        double pulse = Math.Clamp(60.0 / bpm * 0.35, 0.08, 0.5);
        var ax = new DoubleAnimation(1, 1.22, new Duration(TimeSpan.FromSeconds(pulse))) { AutoReverse = true };
        var ay = new DoubleAnimation(1, 1.22, new Duration(TimeSpan.FromSeconds(pulse))) { AutoReverse = true };
        Storyboard.SetTarget(ax, HeartScale);
        Storyboard.SetTargetProperty(ax, new PropertyPath(ScaleTransform.ScaleXProperty));
        Storyboard.SetTarget(ay, HeartScale);
        Storyboard.SetTargetProperty(ay, new PropertyPath(ScaleTransform.ScaleYProperty));
        _beat = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
        _beat.Children.Add(ax);
        _beat.Children.Add(ay);
        _beat.Begin(this);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (!IsPositionLocked && e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        if (_inversionTimer is not null)
        {
            _inversionTimer.Stop();
            _inversionTimer.Tick -= OnInversionTick;
        }
        _sampleCts?.Cancel();
        _sampleCts?.Dispose();
        _sampleCts = null;
        _beat?.Remove(this);
        _beat = null;
    }
}

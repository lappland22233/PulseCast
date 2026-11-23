using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using HeartRateBroadcast.App.Services.Overlay;
using HeartRateBroadcast.App.ViewModels;

namespace HeartRateBroadcast.App.Views;

public partial class OverlayWindow : Window
{
    private static readonly SolidColorBrush LightTextBrush = CreateBrush(Color.FromRgb(0xF6, 0xF8, 0xFB));
    private static readonly SolidColorBrush DarkTextBrush = CreateBrush(Color.FromRgb(0x16, 0x1A, 0x21));
    private static readonly SolidColorBrush LightMutedBrush = CreateBrush(Color.FromArgb(0xCC, 0xF6, 0xF8, 0xFB));
    private static readonly SolidColorBrush DarkMutedBrush = CreateBrush(Color.FromArgb(0xC7, 0x16, 0x1A, 0x21));
    private static readonly SolidColorBrush HeartOnDark = CreateBrush(Color.FromRgb(0xFF, 0x75, 0x72));
    private static readonly SolidColorBrush HeartOnLight = CreateBrush(Color.FromRgb(0xBB, 0x32, 0x36));
    private static readonly SolidColorBrush LiveOnDark = CreateBrush(Color.FromRgb(0x84, 0xD4, 0xAE));
    private static readonly SolidColorBrush LiveOnLight = CreateBrush(Color.FromRgb(0x15, 0x72, 0x4A));
    private static readonly TimeSpan ActiveSampleInterval = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan StableSampleInterval = TimeSpan.FromMilliseconds(500);

    private readonly OverlayViewModel _vm;
    private readonly IScreenLuminanceSampler _sampler;
    private readonly CharacterContrastService _characterContrast;
    private readonly CancellationTokenSource _samplingLifetime = new();
    private readonly DispatcherTimer _inversionTimer;
    private readonly List<Run> _characterRuns = [];
    private readonly List<Int32Rect> _characterRegions = [];
    private bool[] _characterLightText = [];
    private Run? _wholeTextRun;
    private (bool LightText, bool Live)? _appliedTheme;
    private AnimationClock? _beat;
    private double? _animatedBpm;

    private DispatcherOperation? _queuedSample;
    private Int32Rect _captureRegion;
    private byte[]? _capturePixels;
    private int _sampleVersion;
    private int _measuredVersion = -1;
    private int _stableSampleCount;
    private bool _sampleInProgress;
    private bool _disposed;

    public bool IsPositionLocked { get; set; }

    public OverlayWindow(OverlayViewModel viewModel, IScreenLuminanceSampler sampler, CharacterContrastService characterContrast)
    {
        InitializeComponent();
        DataContext = _vm = viewModel;
        _sampler = sampler;
        _characterContrast = characterContrast;
        _inversionTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = ActiveSampleInterval,
        };
        _inversionTimer.Tick += OnInversionTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
        IsVisibleChanged += OnVisibilityChanged;
        LocationChanged += OnGeometryChanged;
        SizeChanged += OnGeometryChanged;
        DpiChanged += OnGeometryChanged;
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => RefreshVisibleState();

    private void RefreshVisibleState()
    {
        if (!IsLoaded || !IsVisible) return;

        _characterContrast.Reset();
        ApplyBackgroundMode();
        ApplyTheme();
        RefreshBpmText();
        UpdateHeartBeat();
        InvalidateSampling();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            RefreshVisibleState();
            return;
        }

        UpdateHeartBeat();
        InvalidateSampling();
    }

    private void OnGeometryChanged(object? sender, EventArgs e)
    {
        if (CanSample) InvalidateSampling();
    }

    private bool CanSample => !_disposed && IsLoaded && IsVisible && UsesAutomaticInversion;

    private bool UsesAutomaticInversion =>
        _vm.HideBackground && _vm.InversionMode == "自动" && _vm.ColorMode == "自动";

    private void InvalidateSampling()
    {
        _sampleVersion++;
        _stableSampleCount = 0;
        if (!CanSample)
        {
            _inversionTimer.Stop();
            _queuedSample?.Abort();
            _queuedSample = null;
            _capturePixels = null;
            return;
        }

        _inversionTimer.Interval = ActiveSampleInterval;
        _inversionTimer.Start();
        QueueSample();
    }

    private void QueueSample()
    {
        if (_sampleInProgress || _queuedSample is not null) return;

        // 等待本轮绑定和布局完成，再测量文字的屏幕坐标。
        _queuedSample = Dispatcher.InvokeAsync(async () =>
        {
            _queuedSample = null;
            await SampleAndApplyAsync();
        }, DispatcherPriority.Background);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!IsLoaded || !IsVisible) return;

        switch (e.PropertyName)
        {
            case nameof(OverlayViewModel.Bpm):
                _characterContrast.Reset();
                RefreshBpmText();
                InvalidateSampling();
                UpdateHeartBeat();
                break;
            case nameof(OverlayViewModel.IsLightText):
                ApplyTheme();
                if (!UsesAutomaticInversion) RefreshBpmText();
                break;
            case nameof(OverlayViewModel.ColorMode):
            case nameof(OverlayViewModel.SolidColor):
            case nameof(OverlayViewModel.GradientId):
            case nameof(OverlayViewModel.InversionMode):
                _characterContrast.Reset();
                ApplyTheme();
                RefreshBpmText();
                InvalidateSampling();
                break;
            case nameof(OverlayViewModel.HideBackground):
                _characterContrast.Reset();
                ApplyBackgroundMode();
                ApplyTheme();
                RefreshBpmText();
                InvalidateSampling();
                break;
            case nameof(OverlayViewModel.IsLive):
                ApplyTheme();
                UpdateHeartBeat();
                break;
            case nameof(OverlayViewModel.ShowHeart):
                UpdateHeartBeat();
                InvalidateSampling();
                break;
            case nameof(OverlayViewModel.Status):
            case nameof(OverlayViewModel.ShowUnit):
            case nameof(OverlayViewModel.ShowLive):
            case nameof(OverlayViewModel.Scale):
            case nameof(OverlayViewModel.NumberFontFamily):
                InvalidateSampling();
                break;
        }
    }

    private async void OnInversionTick(object? sender, EventArgs e) => await SampleAndApplyAsync();

    private async Task SampleAndApplyAsync()
    {
        if (!CanSample || _sampleInProgress) return;

        int version = _sampleVersion;
        if (_measuredVersion != version || _captureRegion.IsEmpty)
        {
            MeasureCharacterScreenRegions();
            _measuredVersion = version;
        }
        if (_captureRegion.IsEmpty) return;

        int byteCount = checked(_captureRegion.Width * _captureRegion.Height * 4);
        if (_capturePixels is null || _capturePixels.Length < byteCount || _capturePixels.Length / 4 > byteCount)
            _capturePixels = GC.AllocateUninitializedArray<byte>(byteCount);

        _sampleInProgress = true;
        try
        {
            ScreenPixelBuffer? pixels = await _sampler.CaptureIntoAsync(_captureRegion, _capturePixels, _samplingLifetime.Token);
            if (pixels is null || version != _sampleVersion || !CanSample) return;

            bool resized = _characterLightText.Length != _characterRegions.Count;
            if (resized) Array.Resize(ref _characterLightText, _characterRegions.Count);
            bool changed = _characterContrast.Analyze(pixels, _characterRegions, EffectiveLightText(), _characterLightText);
            if (changed || resized)
            {
                ApplyCharacterColors();
                int lightCount = 0;
                foreach (bool light in _characterLightText)
                {
                    if (light) lightCount++;
                }
                _vm.IsLightText = lightCount >= _characterLightText.Length - lightCount;
                _stableSampleCount = 0;
            }
            else if (_stableSampleCount < 16)
            {
                _stableSampleCount++;
            }

            TimeSpan interval = _stableSampleCount >= 16 ? StableSampleInterval : ActiveSampleInterval;
            if (_inversionTimer.Interval != interval) _inversionTimer.Interval = interval;
        }
        catch (OperationCanceledException) when (_disposed)
        {
        }
        finally
        {
            _sampleInProgress = false;
            if (version != _sampleVersion && CanSample) QueueSample();
        }
    }

    private void MeasureCharacterScreenRegions()
    {
        _characterRegions.Clear();
        _captureRegion = Int32Rect.Empty;
        if (PresentationSource.FromVisual(BpmText) is null) return;

        foreach (Run run in _characterRuns)
        {
            try
            {
                Rect start = run.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                Rect end = run.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
                if (start.IsEmpty || end.IsEmpty)
                {
                    _characterRegions.Add(Int32Rect.Empty);
                    continue;
                }

                Point topLeft = BpmText.PointToScreen(new Point(Math.Min(start.Left, end.Left), Math.Min(start.Top, end.Top)));
                Point bottomRight = BpmText.PointToScreen(new Point(Math.Max(start.Right, end.Right), Math.Max(start.Bottom, end.Bottom)));
                int left = (int)Math.Floor(Math.Min(topLeft.X, bottomRight.X));
                int top = (int)Math.Floor(Math.Min(topLeft.Y, bottomRight.Y));
                int right = (int)Math.Ceiling(Math.Max(topLeft.X, bottomRight.X));
                int bottom = (int)Math.Ceiling(Math.Max(topLeft.Y, bottomRight.Y));
                _characterRegions.Add(right > left && bottom > top ? new Int32Rect(left, top, right - left, bottom - top) : Int32Rect.Empty);
            }
            catch (InvalidOperationException)
            {
                _characterRegions.Add(Int32Rect.Empty);
            }
        }

        _captureRegion = CharacterContrastService.Union(_characterRegions);
    }

    private void ApplyTheme()
    {
        bool lightText = EffectiveLightText();
        bool live = _vm.IsLive;
        if (_appliedTheme == (lightText, live)) return;

        _appliedTheme = (lightText, live);
        SolidColorBrush muted = lightText ? LightMutedBrush : DarkMutedBrush;
        SolidColorBrush status = live ? (lightText ? LiveOnDark : LiveOnLight) : muted;
        HeartIcon.Fill = lightText ? HeartOnDark : HeartOnLight;
        LiveDot.Fill = status;
        LiveText.Foreground = status;
        UnitText.Foreground = muted;
    }

    private bool EffectiveLightText() => !_vm.HideBackground || (_vm.InversionMode switch
    {
        "强制浅字" => true,
        "强制深字" => false,
        _ => _vm.IsLightText,
    });

    private void RefreshBpmText()
    {
        string text = _vm.Bpm;
        bool gradient = _vm.ColorMode == "逐字渐变";
        if (!UsesAutomaticInversion && !gradient)
        {
            if (_wholeTextRun is null)
            {
                BpmText.Inlines.Clear();
                _characterRuns.Clear();
                _wholeTextRun = new Run();
                BpmText.Inlines.Add(_wholeTextRun);
            }

            if (_wholeTextRun.Text != text) _wholeTextRun.Text = text;
            if (_vm.ColorMode == "单色" && TryParseColor(_vm.SolidColor, out Color solid))
                ApplyColor(_wholeTextRun, solid);
            else
                ApplyBrush(_wholeTextRun, EffectiveLightText() ? LightTextBrush : DarkTextBrush);
            return;
        }

        if (_wholeTextRun is not null)
        {
            BpmText.Inlines.Clear();
            _wholeTextRun = null;
        }

        while (_characterRuns.Count > text.Length)
        {
            BpmText.Inlines.Remove(_characterRuns[^1]);
            _characterRuns.RemoveAt(_characterRuns.Count - 1);
        }

        for (int i = 0; i < text.Length; i++)
        {
            if (i == _characterRuns.Count)
            {
                var run = new Run(text[i].ToString());
                _characterRuns.Add(run);
                BpmText.Inlines.Add(run);
            }
            else if (_characterRuns[i].Text[0] != text[i])
            {
                _characterRuns[i].Text = text[i].ToString();
            }
        }

        if (gradient)
        {
            (Color from, Color to) = GetGradientColors();
            for (int i = 0; i < _characterRuns.Count; i++)
            {
                double t = text.Length > 1 ? (double)i / (text.Length - 1) : 0;
                ApplyColor(_characterRuns[i], Lerp(from, to, t));
            }
        }
        else
        {
            ApplyCharacterColors();
        }
    }

    private void ApplyCharacterColors()
    {
        bool fallback = EffectiveLightText();
        for (int i = 0; i < _characterRuns.Count; i++)
        {
            bool light = i < _characterLightText.Length ? _characterLightText[i] : fallback;
            ApplyBrush(_characterRuns[i], light ? LightTextBrush : DarkTextBrush);
        }
    }

    private static void ApplyBrush(Run run, SolidColorBrush brush)
    {
        if (run.Foreground != brush) run.Foreground = brush;
    }

    private static void ApplyColor(Run run, Color color)
    {
        if (run.Foreground is not SolidColorBrush brush || brush.Color != color)
            run.Foreground = CreateBrush(color);
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private (Color From, Color To) GetGradientColors() => _vm.GradientId switch
    {
        "冰蓝" => (Color.FromRgb(0x36, 0xD1, 0xDC), Color.FromRgb(0x5B, 0x86, 0xE5)),
        "翠绿" => (Color.FromRgb(0x11, 0x99, 0x8E), Color.FromRgb(0x38, 0xEF, 0x7D)),
        "金橙" => (Color.FromRgb(0xF7, 0x97, 0x1E), Color.FromRgb(0xFF, 0xD2, 0x00)),
        _ => (Color.FromRgb(0xFF, 0x4D, 0x6D), Color.FromRgb(0xFF, 0xB1, 0x99)),
    };

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
            OverlayBackground.SetResourceReference(Border.BackgroundProperty, "OverlayPanelBrush");
            OverlayBackground.SetResourceReference(Border.BorderBrushProperty, "OverlayBorderBrush");
        }
    }

    private void UpdateHeartBeat()
    {
        if (_disposed || !IsLoaded || !IsVisible || !_vm.IsLive || !_vm.ShowHeart || !SystemParameters.ClientAreaAnimation ||
            !double.TryParse(_vm.Bpm, out double bpm) || bpm <= 0)
        {
            if (_beat is null) return;
            _beat.Controller?.Remove();
            HeartScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, null);
            HeartScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, null);
            _beat = null;
            _animatedBpm = null;
            return;
        }

        if (_animatedBpm == bpm) return;

        double beatSeconds = 60 / bpm;
        double pulseSeconds = Math.Clamp(beatSeconds * 0.18, 0.05, 0.16);
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(beatSeconds),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1.18, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(pulseSeconds))));
        animation.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(pulseSeconds * 2))));
        Timeline.SetDesiredFrameRate(animation, 24);
        _beat?.Controller?.Remove();
        _beat = animation.CreateClock();
        HeartScale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, _beat);
        HeartScale.ApplyAnimationClock(ScaleTransform.ScaleYProperty, _beat);
        _animatedBpm = bpm;
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
        IsVisibleChanged -= OnVisibilityChanged;
        LocationChanged -= OnGeometryChanged;
        SizeChanged -= OnGeometryChanged;
        DpiChanged -= OnGeometryChanged;
        _vm.PropertyChanged -= OnVmPropertyChanged;
        InvalidateSampling();
        _inversionTimer.Tick -= OnInversionTick;
        _samplingLifetime.Cancel();
        _samplingLifetime.Dispose();
        UpdateHeartBeat();
    }
}

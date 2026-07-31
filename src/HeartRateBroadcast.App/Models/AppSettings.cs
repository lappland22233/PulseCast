namespace HeartRateBroadcast.App.Models;

public sealed class AppSettings
{
    public ulong? SelectedDeviceAddress { get; set; }
    public int DataTimeoutSeconds { get; set; } = 8;
    public int MinimumBpm { get; set; } = 30;
    public int MaximumBpm { get; set; } = 240;
    public bool DiagnosticMode { get; set; }
    public OverlaySettings Overlay { get; set; } = new();

    public AppSettings Snapshot() => new()
    {
        SelectedDeviceAddress = SelectedDeviceAddress,
        DataTimeoutSeconds = DataTimeoutSeconds,
        MinimumBpm = MinimumBpm,
        MaximumBpm = MaximumBpm,
        DiagnosticMode = DiagnosticMode,
        Overlay = Overlay.Snapshot(),
    };

    public void Normalize()
    {
        DataTimeoutSeconds = Math.Clamp(DataTimeoutSeconds, 3, 60);
        MinimumBpm = Math.Clamp(MinimumBpm, 1, 299);
        MaximumBpm = Math.Clamp(MaximumBpm, MinimumBpm, 300);
        Overlay ??= new OverlaySettings();
        Overlay.Normalize();
    }
}

public sealed class OverlaySettings
{
    public double Width { get; set; } = 214;
    public double Height { get; set; } = 92;
    public double RightOffset { get; set; } = 32;
    public double TopOffset { get; set; } = 32;
    public double Opacity { get; set; } = 0.92;
    public double FontSize { get; set; } = 42;
    public bool Locked { get; set; }
    public bool ClickThrough { get; set; }
    public bool HideBackground { get; set; }
    public string MonitorDeviceName { get; set; } = string.Empty;
    public string NumberFont { get; set; } = "等宽";
    public bool ShowHeart { get; set; } = true;
    public bool ShowUnit { get; set; } = true;
    public bool ShowLive { get; set; } = true;
    public double Scale { get; set; } = 1.0;
    public string InversionMode { get; set; } = "自动";
    public string ColorMode { get; set; } = "自动";
    public string SolidColor { get; set; } = "纯白";
    public string GradientId { get; set; } = "红粉";

    public OverlaySettings Snapshot() => (OverlaySettings)MemberwiseClone();

    public void Normalize()
    {
        Width = NormalizeDouble(Width, 80, 1200, 214);
        Height = NormalizeDouble(Height, 48, 800, 92);
        RightOffset = NormalizeDouble(RightOffset, 0, 10000, 32);
        TopOffset = NormalizeDouble(TopOffset, 0, 10000, 32);
        Opacity = NormalizeDouble(Opacity, 0.2, 1, 0.92);
        FontSize = NormalizeDouble(FontSize, 12, 200, 42);
        Scale = NormalizeDouble(Scale, 0.5, 3, 1);
        MonitorDeviceName ??= string.Empty;
        NumberFont = Valid(NumberFont, ["等宽", "仪表", "无衬线"], "等宽");
        InversionMode = Valid(InversionMode, ["自动", "强制浅字", "强制深字"], "自动");
        ColorMode = Valid(ColorMode, ["自动", "逐字渐变", "单色"], "自动");
        SolidColor = Valid(SolidColor, ["纯白", "纯黑", "红色", "蓝色", "绿色", "黄色"], "纯白");
        GradientId = Valid(GradientId, ["红粉", "冰蓝", "翠绿", "金橙"], "红粉");
    }

    private static double NormalizeDouble(double value, double minimum, double maximum, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static string Valid(string? value, string[] allowed, string fallback) =>
        value is not null && allowed.Contains(value, StringComparer.Ordinal) ? value : fallback;
}

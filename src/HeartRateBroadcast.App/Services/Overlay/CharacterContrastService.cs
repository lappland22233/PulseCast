using System.Windows;

namespace HeartRateBroadcast.App.Services.Overlay;

/// <summary>按字符屏幕区域分析背景，并通过独立迟滞选择浅色或深色前景。</summary>
public sealed class CharacterContrastService
{
    private const double SwitchAdvantage = 1.15;
    private readonly List<bool?> _useLightText = [];

    public IReadOnlyList<bool> Analyze(ScreenPixelBuffer pixels, IReadOnlyList<Int32Rect> characterRegions, bool fallbackUseLightText)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(characterRegions);
        ResizeState(characterRegions.Count);

        var results = new bool[characterRegions.Count];
        for (int i = 0; i < characterRegions.Count; i++)
        {
            double? luminance = SampleBackgroundLuminance(pixels, characterRegions[i]);
            bool current = _useLightText[i] ?? fallbackUseLightText;
            if (luminance.HasValue)
            {
                double lightContrast = ContrastRatio(1, luminance.Value);
                double darkContrast = ContrastRatio(ScreenLuminanceSampler.RelativeLuminance(0x16, 0x1A, 0x21), luminance.Value);
                if (current && darkContrast > lightContrast * SwitchAdvantage) current = false;
                else if (!current && lightContrast > darkContrast * SwitchAdvantage) current = true;
            }
            _useLightText[i] = current;
            results[i] = current;
        }
        return results;
    }

    public void Reset() => _useLightText.Clear();

    public static Int32Rect Union(IReadOnlyList<Int32Rect> regions)
    {
        if (regions.Count == 0) return Int32Rect.Empty;
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (Int32Rect region in regions)
        {
            if (region.Width <= 0 || region.Height <= 0) continue;
            left = Math.Min(left, region.X);
            top = Math.Min(top, region.Y);
            right = Math.Max(right, region.X + region.Width);
            bottom = Math.Max(bottom, region.Y + region.Height);
        }
        return left == int.MaxValue ? Int32Rect.Empty : new Int32Rect(left, top, right - left, bottom - top);
    }

    private static double? SampleBackgroundLuminance(ScreenPixelBuffer pixels, Int32Rect region)
    {
        int left = Math.Max(region.X, pixels.Region.X);
        int top = Math.Max(region.Y, pixels.Region.Y);
        int right = Math.Min(region.X + region.Width, pixels.Region.X + pixels.Region.Width);
        int bottom = Math.Min(region.Y + region.Height, pixels.Region.Y + pixels.Region.Height);
        if (right <= left || bottom <= top) return null;

        // 优先采样布局单元的边缘与内部空白，减少当前文字笔画对截图结果的污染。
        var samples = new List<double>(Math.Min((right - left) * (bottom - top), 256));
        int insetX = Math.Max(1, (right - left) / 5);
        int insetY = Math.Max(1, (bottom - top) / 5);
        int step = Math.Max(1, Math.Min(right - left, bottom - top) / 12);
        for (int y = top; y < bottom; y += step)
        {
            for (int x = left; x < right; x += step)
            {
                bool edge = x < left + insetX || x >= right - insetX || y < top + insetY || y >= bottom - insetY;
                if (!edge || !pixels.TryGetPixel(x, y, out byte r, out byte g, out byte b)) continue;
                samples.Add(ScreenLuminanceSampler.RelativeLuminance(r, g, b));
            }
        }
        if (samples.Count < 4) return null;

        samples.Sort();
        // 中位数能抵抗少量前景笔画和动态高光，比平均值更稳定。
        int middle = samples.Count / 2;
        return samples.Count % 2 == 0 ? (samples[middle - 1] + samples[middle]) / 2 : samples[middle];
    }

    private static double ContrastRatio(double a, double b) =>
        (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

    private void ResizeState(int count)
    {
        while (_useLightText.Count < count) _useLightText.Add(null);
        if (_useLightText.Count > count) _useLightText.RemoveRange(count, _useLightText.Count - count);
    }
}

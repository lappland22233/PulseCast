using System.Buffers;
using System.Windows;

namespace HeartRateBroadcast.App.Services.Overlay;

/// <summary>按字符屏幕区域分析背景，并通过独立迟滞选择浅色或深色前景。</summary>
public sealed class CharacterContrastService
{
    private const double SwitchAdvantage = 1.15;
    private static readonly double DarkTextLuminance = ScreenLuminanceSampler.RelativeLuminance(0x16, 0x1A, 0x21);
    private readonly List<bool?> _useLightText = [];

    public IReadOnlyList<bool> Analyze(ScreenPixelBuffer pixels, IReadOnlyList<Int32Rect> characterRegions, bool fallbackUseLightText)
    {
        ArgumentNullException.ThrowIfNull(characterRegions);
        var results = new bool[characterRegions.Count];
        Analyze(pixels, characterRegions, fallbackUseLightText, results);
        return results;
    }

    /// <summary>将字符颜色写入调用方的缓冲区，返回其中是否有颜色发生变化。</summary>
    public bool Analyze(ScreenPixelBuffer pixels, IReadOnlyList<Int32Rect> characterRegions, bool fallbackUseLightText, Span<bool> results)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(characterRegions);
        if (results.Length < characterRegions.Count)
            throw new ArgumentException("结果缓冲区不能小于字符数。", nameof(results));

        ResizeState(characterRegions.Count);
        bool changed = false;
        for (int i = 0; i < characterRegions.Count; i++)
        {
            double? luminance = SampleBackgroundLuminance(pixels, characterRegions[i]);
            bool current = _useLightText[i] ?? fallbackUseLightText;
            if (luminance.HasValue)
            {
                double lightContrast = ContrastRatio(1, luminance.Value);
                double darkContrast = ContrastRatio(DarkTextLuminance, luminance.Value);
                if (current && darkContrast > lightContrast * SwitchAdvantage) current = false;
                else if (!current && lightContrast > darkContrast * SwitchAdvantage) current = true;
            }
            _useLightText[i] = current;
            changed |= results[i] != current;
            results[i] = current;
        }
        return changed;
    }

    public void Reset() => _useLightText.Clear();

    public static Int32Rect Union(IReadOnlyList<Int32Rect> regions)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (int i = 0; i < regions.Count; i++)
        {
            Int32Rect region = regions[i];
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
        int width = right - left, height = bottom - top;
        int insetX = Math.Max(1, width / 5);
        int insetY = Math.Max(1, height / 5);
        int step = Math.Max(1, Math.Min(width, height) / 12);
        int capacity = ((width - 1) / step + 1) * ((height - 1) / step + 1);
        double[]? rented = null;
        Span<double> samples = capacity <= 512 ? stackalloc double[capacity] : (rented = ArrayPool<double>.Shared.Rent(capacity));
        try
        {
            int count = 0;
            for (int y = top; y < bottom; y += step)
            {
                for (int x = left; x < right; x += step)
                {
                    bool edge = x < left + insetX || x >= right - insetX || y < top + insetY || y >= bottom - insetY;
                    if (!edge || !pixels.TryGetPixel(x, y, out byte r, out byte g, out byte b)) continue;
                    samples[count++] = ScreenLuminanceSampler.RelativeLuminance(r, g, b);
                }
            }
            if (count < 4) return null;

            samples[..count].Sort();
            // 中位数能抵抗少量前景笔画和动态高光，比平均值更稳定。
            int middle = count / 2;
            return count % 2 == 0 ? (samples[middle - 1] + samples[middle]) / 2 : samples[middle];
        }
        finally
        {
            if (rented is not null) ArrayPool<double>.Shared.Return(rented);
        }
    }

    private static double ContrastRatio(double a, double b) =>
        (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);

    private void ResizeState(int count)
    {
        while (_useLightText.Count < count) _useLightText.Add(null);
        if (_useLightText.Count > count) _useLightText.RemoveRange(count, _useLightText.Count - count);
    }
}

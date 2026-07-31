using System.Windows;
using HeartRateBroadcast.App.Interop;

namespace HeartRateBroadcast.App.Services.Overlay;

/// <summary>
/// 采样屏幕指定区域的平均相对亮度（WCAG 公式）。
/// 采样区域位于悬浮窗外侧，采样期间不会改变窗口可见性。
/// </summary>
public interface IScreenLuminanceSampler
{
    /// <param name="region">物理像素坐标的采样区域</param>
    /// <returns>0（全黑）~1（全白）的平均相对亮度；失败时返回 null</returns>
    double? Sample(Int32Rect region);
    Task<double?> SampleAsync(Int32Rect region, CancellationToken cancellationToken = default);
}

public sealed class ScreenLuminanceSampler : IScreenLuminanceSampler
{
    private static readonly double[] LinearChannels = Enumerable.Range(0, 256)
        .Select(value =>
        {
            double channel = value / 255.0;
            return channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        })
        .ToArray();

    public Task<double?> SampleAsync(Int32Rect region, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Sample(region);
        }, cancellationToken);

    public double? Sample(Int32Rect region)
    {
        if (region.Width <= 0 || region.Height <= 0) return null;

        nint screenDc = nint.Zero, memDc = nint.Zero, hbm = nint.Zero, oldObj = nint.Zero;
        try
        {
            screenDc = NativeMethods.GetDC(nint.Zero);
            if (screenDc == nint.Zero) return null;
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == nint.Zero) return null;
            hbm = NativeMethods.CreateCompatibleBitmap(screenDc, region.Width, region.Height);
            if (hbm == nint.Zero) return null;
            oldObj = NativeMethods.SelectObject(memDc, hbm);

            bool ok = NativeMethods.BitBlt(memDc, 0, 0, region.Width, region.Height, screenDc, region.X, region.Y, NativeMethods.SrcCopy);
            if (!ok) return null;

            var bmi = new NativeMethods.BitMapInfo();
            bmi.BmiHeader.BiSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.BitMapInfoHeader>();
            bmi.BmiHeader.BiWidth = region.Width;
            bmi.BmiHeader.BiHeight = -region.Height; // 自上而下
            bmi.BmiHeader.BiPlanes = 1;
            bmi.BmiHeader.BiBitCount = 32;
            bmi.BmiHeader.BiCompression = NativeMethods.BiRgb;

            byte[] bits = new byte[region.Width * region.Height * 4];
            int lines = NativeMethods.GetDIBits(memDc, hbm, 0, (uint)region.Height, bits, ref bmi, NativeMethods.DibRgbColors);
            if (lines == 0) return null;

            return AverageLuminance(bits);
        }
        finally
        {
            if (oldObj != nint.Zero && memDc != nint.Zero) NativeMethods.SelectObject(memDc, oldObj);
            if (hbm != nint.Zero) NativeMethods.DeleteObject(hbm);
            if (memDc != nint.Zero) NativeMethods.DeleteDC(memDc);
            if (screenDc != nint.Zero) _ = NativeMethods.ReleaseDC(nint.Zero, screenDc);
        }
    }

    private static double AverageLuminance(byte[] bgra)
    {
        double sum = 0;
        int count = 0;
        // 步长采样，降低计算量（每 4 个像素取 1 个）
        for (int i = 0; i + 3 < bgra.Length; i += 16)
        {
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            sum += RelativeLuminance(r, g, b);
            count++;
        }
        return count == 0 ? 0 : sum / count;
    }

    /// <summary>WCAG 2.x 相对亮度公式。</summary>
    public static double RelativeLuminance(byte r, byte g, byte b) =>
        0.2126 * LinearChannels[r] + 0.7152 * LinearChannels[g] + 0.0722 * LinearChannels[b];
}

/// <summary>
/// 智能反色判定：对平均亮度做迟滞处理，避免悬浮窗在明暗边界来回抖动。
/// </summary>
public sealed class InversionDecider
{
    public const double LightThreshold = 0.55; // 高于 → 浅色背景 → 用深色字
    public const double DarkThreshold = 0.45;  // 低于 → 深色背景 → 用浅色字

    /// <summary>当前背景是否判定为浅色。</summary>
    public bool BackgroundIsLight { get; private set; }

    /// <summary>输入采样亮度，返回是否应使用浅色字体（用于深色背景）。</summary>
    public bool ShouldUseLightText(double luminance)
    {
        if (luminance > LightThreshold) BackgroundIsLight = true;
        else if (luminance < DarkThreshold) BackgroundIsLight = false;
        return !BackgroundIsLight;
    }
}

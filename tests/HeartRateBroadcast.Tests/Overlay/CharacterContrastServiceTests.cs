using System.Windows;
using HeartRateBroadcast.App.Services.Overlay;

namespace HeartRateBroadcast.Tests.Overlay;

public class CharacterContrastServiceTests
{
    [Fact]
    public void Union_SupportsNegativeCoordinatesAndSkipsEmptyRegions()
    {
        Int32Rect result = CharacterContrastService.Union(
        [
            new Int32Rect(-120, 20, 40, 60),
            Int32Rect.Empty,
            new Int32Rect(-75, 10, 25, 30),
        ]);

        Assert.Equal(new Int32Rect(-120, 10, 70, 70), result);
    }

    [Fact]
    public void Analyze_SelectsDarkTextForLightBackgroundPerCharacter()
    {
        var service = new CharacterContrastService();
        ScreenPixelBuffer pixels = SolidBuffer(new Int32Rect(0, 0, 20, 10), 255, 255, 255);

        IReadOnlyList<bool> result = service.Analyze(pixels,
        [
            new Int32Rect(0, 0, 10, 10),
            new Int32Rect(10, 0, 10, 10),
        ], fallbackUseLightText: true);

        Assert.Equal([false, false], result);
    }

    [Fact]
    public void Analyze_ChoosesColorsIndependentlyAcrossSplitBackground()
    {
        var region = new Int32Rect(-10, -5, 20, 10);
        byte[] bgra = new byte[region.Width * region.Height * 4];
        for (int y = 0; y < region.Height; y++)
        {
            for (int x = 0; x < region.Width; x++)
            {
                byte value = x < 10 ? (byte)0 : (byte)255;
                int offset = (y * region.Width + x) * 4;
                bgra[offset] = bgra[offset + 1] = bgra[offset + 2] = value;
                bgra[offset + 3] = 255;
            }
        }

        var service = new CharacterContrastService();
        IReadOnlyList<bool> result = service.Analyze(new ScreenPixelBuffer(region, bgra),
        [
            new Int32Rect(-10, -5, 10, 10),
            new Int32Rect(0, -5, 10, 10),
        ], fallbackUseLightText: false);

        Assert.Equal([true, false], result);
    }

    [Fact]
    public void Analyze_KeepsFallbackWhenCharacterHasNoValidPixels()
    {
        var service = new CharacterContrastService();
        ScreenPixelBuffer pixels = SolidBuffer(new Int32Rect(0, 0, 4, 4), 128, 128, 128);

        IReadOnlyList<bool> result = service.Analyze(pixels, [new Int32Rect(20, 20, 5, 5)], fallbackUseLightText: true);

        Assert.True(result[0]);
    }

    [Fact]
    public void Reset_DiscardsPreviousCharacterDecision()
    {
        var service = new CharacterContrastService();
        Int32Rect region = new(0, 0, 10, 10);
        service.Analyze(SolidBuffer(region, 255, 255, 255), [region], fallbackUseLightText: true);

        service.Reset();
        IReadOnlyList<bool> result = service.Analyze(SolidBuffer(region, 128, 128, 128), [Int32Rect.Empty], fallbackUseLightText: true);

        Assert.True(result[0]);
    }

    [Fact]
    public void ScreenPixelBuffer_MapsPhysicalScreenCoordinates()
    {
        var buffer = SolidBuffer(new Int32Rect(-20, 30, 2, 2), 12, 34, 56);

        Assert.True(buffer.TryGetPixel(-19, 31, out byte r, out byte g, out byte b));
        Assert.Equal((byte)12, r);
        Assert.Equal((byte)34, g);
        Assert.Equal((byte)56, b);
        Assert.False(buffer.TryGetPixel(0, 0, out _, out _, out _));
    }

    private static ScreenPixelBuffer SolidBuffer(Int32Rect region, byte r, byte g, byte b)
    {
        byte[] bgra = new byte[region.Width * region.Height * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = b;
            bgra[i + 1] = g;
            bgra[i + 2] = r;
            bgra[i + 3] = 255;
        }
        return new ScreenPixelBuffer(region, bgra);
    }
}

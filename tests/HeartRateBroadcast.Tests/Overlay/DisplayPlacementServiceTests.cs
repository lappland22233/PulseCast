using System.Windows;
using HeartRateBroadcast.App.Models;
using HeartRateBroadcast.App.Services.Overlay;

namespace HeartRateBroadcast.Tests.Overlay;

public class DisplayPlacementServiceTests
{
    [Fact]
    public void PreservesRightAndTopOffsets()
    {
        var settings = new OverlaySettings { Width = 200, Height = 90, RightOffset = 30, TopOffset = 20 };
        Rect result = new DisplayPlacementService().Calculate(new Rect(0, 0, 1920, 1080), settings);
        Assert.Equal(new Rect(1690, 20, 200, 90), result);
    }

    [Fact]
    public void ClipsOversizedWindowToWorkArea()
    {
        Rect result = new DisplayPlacementService().Calculate(new Rect(100, 50, 800, 600), new OverlaySettings { Width = 1200, Height = 900 });
        Assert.Equal(new Rect(100, 50, 800, 600), result);
    }

    [Fact]
    public void SupportsNegativeMonitorCoordinatesAndActualWindowSize()
    {
        var settings = new OverlaySettings { Width = 200, Height = 90, RightOffset = 24, TopOffset = 16 };
        Rect result = new DisplayPlacementService().Calculate(new Rect(-1920, -200, 1920, 1080), settings, 320, 140);
        Assert.Equal(new Rect(-344, -184, 320, 140), result);
    }

    [Fact]
    public void ClampsOffsetsAfterActualSizeChanges()
    {
        var settings = new OverlaySettings { RightOffset = 5000, TopOffset = 5000 };
        Rect result = new DisplayPlacementService().Calculate(new Rect(100, 50, 800, 600), settings, 300, 120);
        Assert.Equal(new Rect(100, 530, 300, 120), result);
    }
}

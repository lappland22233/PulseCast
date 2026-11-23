using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using HeartRateBroadcast.App.Interop;
using HeartRateBroadcast.App.ViewModels;

namespace HeartRateBroadcast.App;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (SystemParameters.HighContrast) return;

        nint handle = new WindowInteropHelper(this).Handle;
        int darkMode = 1;
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DwmwaUseImmersiveDarkMode, in darkMode, sizeof(int));
        SetCaptionColor(handle, NativeMethods.DwmwaCaptionColor, "AppBackground");
        SetCaptionColor(handle, NativeMethods.DwmwaTextColor, "TextPrimary");
    }

    private void SetCaptionColor(nint handle, int attribute, string resourceKey)
    {
        var color = ((SolidColorBrush)FindResource(resourceKey)).Color;
        int colorRef = color.R | (color.G << 8) | (color.B << 16);
        NativeMethods.DwmSetWindowAttribute(handle, attribute, in colorRef, sizeof(int));
    }
}

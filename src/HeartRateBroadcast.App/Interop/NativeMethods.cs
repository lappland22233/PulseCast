using System.Runtime.InteropServices;

namespace HeartRateBroadcast.App.Interop;

internal static partial class NativeMethods
{
    public static readonly nint HwndTopmost = new(-1);
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpHideWindow = 0x0080;
    public const uint SwpShowWindow = 0x0040;
    public const int GwlExStyle = -20;
    public const long WsExTransparent = 0x20;
    public const long WsExToolWindow = 0x80;
    public const long WsExNoActivate = 0x08000000;
    public const int WmDisplayChange = 0x007E;
    public const int WmSettingChange = 0x001A;
    public const int WmDpiChanged = 0x02E0;
    public const uint MonitorDefaultToNearest = 2;
    public const int DwmwaUseImmersiveDarkMode = 20;
    public const int DwmwaWindowCornerPreference = 33;

    public const int SrcCopy = 0x00CC0020;
    public const uint BiRgb = 0;
    public const uint DibRgbColors = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitMapInfoHeader
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RgbQuad
    {
        public byte RgbBlue;
        public byte RgbGreen;
        public byte RgbRed;
        public byte RgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitMapInfo
    {
        public BitMapInfoHeader BmiHeader;
        public RgbQuad BmiColors;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hWnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static partial nint GetWindowLongPtr(nint hWnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static partial nint SetWindowLongPtr(nint hWnd, int index, nint newLong);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(nint monitor, ref MonitorInfoEx info);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    public static partial nint GetDC(nint hWnd);

    [LibraryImport("user32.dll")]
    public static partial int ReleaseDC(nint hWnd, nint hDC);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint hDC);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleBitmap(nint hDC, int width, int height);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint hDC, nint hObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint hObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint hDC);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BitBlt(nint hdcDest, int xDest, int yDest, int width, int height, nint hdcSrc, int xSrc, int ySrc, int rop);

    [LibraryImport("gdi32.dll")]
    public static partial int GetDIBits(nint hDC, nint hbm, uint start, uint cLines, byte[]? lpvBits, ref BitMapInfo lpbmi, uint usage);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmFlush();

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, int dwAttribute, in int pvAttribute, int cbAttribute);
}

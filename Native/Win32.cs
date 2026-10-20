using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace FileWheel.Native;

[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }

[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }

public static class Win32
{
    public static POINT Cursor()
    {
        GetCursorPos(out var p);
        return p;
    }

    /// <summary>Work area (taskbar excluded) of the monitor under a screen point, in physical pixels, and its DPI scale.</summary>
    public static (RECT WorkArea, RECT Bounds, double Scale) MonitorAt(POINT p)
    {
        var monitor = MonitorFromPoint(p, 2);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        double scale = 1;
        try { if (GetDpiForMonitor(monitor, 0, out uint dx, out _) == 0) scale = dx / 96.0; } catch { }
        return (info.rcWork, info.rcMonitor, scale);
    }

    public static void MakeToolWindow(Window window, bool noActivate)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        long style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style |= WS_EX_TOOLWINDOW;
        if (noActivate) style |= WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    /// <summary>Moves a window to physical-pixel coordinates (correct across mixed-DPI monitors).</summary>
    public static void MoveTo(Window window, int x, int y)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static RECT WindowRect(Window window)
    {
        GetWindowRect(new WindowInteropHelper(window).Handle, out var r);
        return r;
    }

    public static bool IsLeftButtonDown() =>
        (GetAsyncKeyState(GetSystemMetrics(SM_SWAPBUTTON) != 0 ? 0x02 : 0x01) & 0x8000) != 0;

    public static void ForceForeground(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        // Tapping Alt lets a background process take focus legitimately after a tray/IPC request.
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(hwnd);
    }

    const int GWL_EXSTYLE = -20;
    const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    const int SM_SWAPBUTTON = 23;

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}

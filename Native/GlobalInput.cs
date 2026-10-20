using System.Runtime.InteropServices;
using System.Windows.Threading;
using FileWheel.Core;

namespace FileWheel.Native;

/// <summary>
/// Watches for "drag something, then tap a modifier" anywhere in Windows.
/// Hooks run on their own thread so a busy UI can never stall system input.
/// It only signals intent; the dragged files arrive later through the wheel's OLE drop target.
/// </summary>
public sealed class GlobalInput : IDisposable
{
    public event Action<WheelMode>? ModeKey;
    public event Action? EscapePressed;
    public event Action? LeftReleased;

    public bool Paused { get; set; }

    /// <summary>True while the left button is held and the pointer has travelled far enough to be a drag.</summary>
    public bool Dragging => leftDown && moved;

    readonly Dispatcher ui;
    readonly Thread thread;
    readonly HookProc mouseProc, keyboardProc;
    readonly HashSet<int> keysDown = [];
    IntPtr mouseHook, keyboardHook;
    uint threadId;
    volatile bool leftDown, moved;
    POINT start;
    long lastShiftPress = long.MinValue / 2;

    public GlobalInput(Dispatcher ui)
    {
        this.ui = ui;
        mouseProc = OnMouse;
        keyboardProc = OnKeyboard;
        var ready = new ManualResetEventSlim();
        Exception? failure = null;
        thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            var module = GetModuleHandle(null);
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, module, 0);
            if (mouseHook == IntPtr.Zero || keyboardHook == IntPtr.Zero)
                failure = new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            ready.Set();
            if (failure != null) return;
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
            UnhookWindowsHookEx(mouseHook);
            UnhookWindowsHookEx(keyboardHook);
        })
        { IsBackground = true, Name = "FileWheel input hooks" };
        thread.Start();
        ready.Wait();
        if (failure != null) throw failure;
    }

    IntPtr OnMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(data);
            switch ((int)message)
            {
                case WM_LBUTTONDOWN:
                    leftDown = true;
                    moved = false;
                    start = info.pt;
                    break;
                case WM_LBUTTONUP:
                    bool wasDragging = leftDown && moved;
                    leftDown = false;
                    moved = false;
                    if (wasDragging) ui.BeginInvoke(() => LeftReleased?.Invoke());
                    break;
                case WM_MOUSEMOVE when leftDown && !moved:
                    if (Math.Abs(info.pt.X - start.X) > 6 || Math.Abs(info.pt.Y - start.Y) > 6)
                    {
                        moved = true;
                        // Shift held (or just tapped) before the drag began also opens the wheel.
                        bool shiftHeld = (GetAsyncKeyState(0x10) & 0x8000) != 0;
                        if (!Paused && (shiftHeld || Environment.TickCount64 - lastShiftPress < 1000))
                            ui.BeginInvoke(DispatcherPriority.Send, () => ModeKey?.Invoke(WheelMode.Convert));
                    }
                    break;
            }
        }
        return CallNextHookEx(mouseHook, code, message, data);
    }

    IntPtr OnKeyboard(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(data);
            int vk = (int)info.vkCode;
            int msg = (int)message;
            if (msg is WM_KEYDOWN or WM_SYSKEYDOWN)
            {
                bool repeat = !keysDown.Add(vk);
                if (!repeat)
                {
                    if (vk is 0x10 or 0xA0 or 0xA1) lastShiftPress = Environment.TickCount64;
                    if (vk == VK_ESCAPE) ui.BeginInvoke(() => EscapePressed?.Invoke());
                    else if (!Paused && leftDown && moved && Win32.IsLeftButtonDown())
                    {
                        WheelMode? mode = vk switch
                        {
                            0x10 or 0xA0 or 0xA1 => WheelMode.Convert,
                            0x11 or 0xA2 or 0xA3 => WheelMode.Tools,
                            0x12 or 0xA4 or 0xA5 => WheelMode.Actions,
                            _ => null,
                        };
                        if (mode is { } m) ui.BeginInvoke(DispatcherPriority.Send, () => ModeKey?.Invoke(m));
                    }
                }
            }
            else if (msg is WM_KEYUP or WM_SYSKEYUP)
            {
                keysDown.Remove(vk);
            }
        }
        return CallNextHookEx(keyboardHook, code, message, data);
    }

    public void Dispose()
    {
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202;
    const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105, WM_QUIT = 0x12;
    const int VK_ESCAPE = 0x1B;

    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)] struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? module);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, int msg, IntPtr w, IntPtr l);
}

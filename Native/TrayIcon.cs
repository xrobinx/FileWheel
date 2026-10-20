using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FileWheel.Native;

/// <summary>
/// The notification-area icon, straight on Shell_NotifyIcon with a native right-click menu
/// (Windows draws it in its own style). Left click runs <see cref="Clicked"/>.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    public sealed record MenuItem(string Text, Action Run, Func<bool>? Checked = null);

    const int CallbackMessage = 0x8001; // WM_APP + 1
    const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;
    readonly HwndSource window;
    readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    readonly IntPtr icon;
    string tip;
    bool added;

    public event Action? Clicked;

    /// <summary>Menu entries; null is a separator.</summary>
    public List<MenuItem?> Menu { get; } = [];

    public TrayIcon(string tooltip)
    {
        tip = tooltip;
        window = new HwndSource(new HwndSourceParameters("FileWheel tray") { Width = 0, Height = 0, WindowStyle = 0 });
        window.AddHook(Hook);
        // The app's own icon, at the small-icon size Windows uses in the tray.
        ExtractIconEx(Environment.ProcessPath ?? "", 0, IntPtr.Zero, out icon, 1);
        Add();
    }

    public string Text
    {
        get => tip;
        set
        {
            tip = value;
            if (added) Notify(0x1); // NIM_MODIFY
        }
    }

    void Add() => added = Notify(0x0); // NIM_ADD

    bool Notify(uint message)
    {
        var data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = window.Handle,
            uID = 1,
            uFlags = 0x1 | 0x2 | 0x4, // NIF_MESSAGE | NIF_ICON | NIF_TIP
            uCallbackMessage = CallbackMessage,
            hIcon = icon,
            szTip = tip.Length > 127 ? tip[..127] : tip,
        };
        return Shell_NotifyIcon(message, ref data);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            int e = (int)(lParam.ToInt64() & 0xFFFF);
            if (e == WM_LBUTTONUP) Clicked?.Invoke();
            else if (e is WM_RBUTTONUP or WM_CONTEXTMENU) ShowMenu();
            handled = true;
        }
        else if (msg == taskbarCreated)
        {
            // Explorer restarted: put the icon back.
            Add();
        }
        return IntPtr.Zero;
    }

    void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            for (int i = 0; i < Menu.Count; i++)
            {
                var item = Menu[i];
                if (item == null) AppendMenu(menu, 0x800, UIntPtr.Zero, null); // MF_SEPARATOR
                else AppendMenu(menu, item.Checked?.Invoke() == true ? 0x8u : 0u, (UIntPtr)(i + 1), item.Text); // MF_CHECKED
            }
            GetCursorPos(out var at);
            // Required so the menu closes when you click elsewhere.
            SetForegroundWindow(window.Handle);
            int chosen = TrackPopupMenuEx(menu, 0x100 | 0x2, at.X, at.Y, window.Handle, IntPtr.Zero); // TPM_RETURNCMD | TPM_RIGHTBUTTON
            PostMessage(window.Handle, 0, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && Menu[chosen - 1] is { } picked) picked.Run();
        }
        finally { DestroyMenu(menu); }
    }

    public void Dispose()
    {
        if (added) Notify(0x2); // NIM_DELETE
        added = false;
        if (icon != IntPtr.Zero) DestroyIcon(icon);
        window.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT point);
}

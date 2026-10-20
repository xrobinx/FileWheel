using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FileWheel.Native;

namespace FileWheel;

/// <summary>
/// End-to-end check of the real gesture against a running FileWheel:
///   FileWheel --dragtest &lt;file&gt; &lt;key: shift|ctrl|alt&gt; &lt;dx&gt; &lt;dy&gt;
/// Opens a small window that starts a genuine OLE file drag, presses the key mid-drag,
/// moves (dx, dy) DIPs from the drag point and releases. The pointer is put back afterwards.
/// </summary>
public static class DragTest
{
    public static int Run(string[] args)
    {
        string file = args[0] == "none" ? "none" : Path.GetFullPath(args[0]);
        byte key = args[1] switch { "ctrl" => 0x11, "alt" => 0x12, _ => 0x10 };
        bool holdFirst = args[1] == "holdshift";
        double dx = double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
        double dy = double.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        var original = Win32.Cursor();
        string result = "no drop";

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new Window
        {
            Title = "FileWheel drag test",
            Width = 180, Height = 140, Left = 120, Top = 120, Topmost = true,
            WindowStyle = WindowStyle.ToolWindow,
            Background = new SolidColorBrush(Color.FromRgb(250, 240, 230)),
            Content = new TextBlock { Text = "Drag source\n" + Path.GetFileName(file), Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap },
        };
        window.MouseLeftButtonDown += (_, _) =>
        {
            if (args[0] == "none") return; // plain mouse drag, no files: the wheel must stay hidden
            var data = new DataObject(DataFormats.FileDrop, new[] { file });
            var effect = DragDrop.DoDragDrop(window, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
            result = "drop effect: " + effect;
        };
        window.Loaded += (_, _) =>
        {
            var center = window.PointToScreen(new Point(window.ActualWidth / 2, window.ActualHeight / 2));
            double scale = VisualTreeHelper.GetDpi(window).DpiScaleX;
            new Thread(() =>
            {
                Thread.Sleep(700);
                int x = (int)center.X, y = (int)center.Y;
                SetCursorPos(x, y);
                Thread.Sleep(150);
                if (holdFirst) { keybd_event(0x10, 0, 0, UIntPtr.Zero); Thread.Sleep(120); }
                Mouse(0x0002); // left down
                for (int i = 1; i <= 10; i++) { MoveTo(x + i * 3, y + i * 2); Thread.Sleep(25); }
                x += 30; y += 20;
                Thread.Sleep(250);
                if (!holdFirst)
                {
                    keybd_event(key, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(60);
                    keybd_event(key, 0, 2, UIntPtr.Zero);
                }
                Thread.Sleep(700);
                // Optional stops on the way ("dx,dy|dx,dy"), resting on each like a user would.
                int cx = x, cy = y;
                foreach (var stop in (args.Length > 4 ? args[4] : "").Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = stop.Split(',');
                    int tx = x + (int)(double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture) * scale);
                    int ty = y + (int)(double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture) * scale);
                    for (int i = 1; i <= 12; i++) { MoveTo(cx + (tx - cx) * i / 12, cy + (ty - cy) * i / 12); Thread.Sleep(30); }
                    cx = tx; cy = ty;
                    for (int i = 0; i < 9; i++) { MoveTo(cx + (i % 2), cy); Thread.Sleep(100); }
                }
                int fx = x + (int)(dx * scale), fy = y + (int)(dy * scale);
                for (int i = 1; i <= 12; i++) { MoveTo(cx + (fx - cx) * i / 12, cy + (fy - cy) * i / 12); Thread.Sleep(30); }
                Thread.Sleep(500);
                Mouse(0x0004); // left up
                if (holdFirst) keybd_event(0x10, 0, 2, UIntPtr.Zero);
                Thread.Sleep(600);
                SetCursorPos(original.X, original.Y);
                window.Dispatcher.Invoke(() => { Console.WriteLine(result); window.Close(); });
            }) { IsBackground = true }.Start();
        };
        AttachConsole(-1);
        app.Run(window);
        return 0;
    }

    static void Mouse(uint flags, int dx = 0, int dy = 0) => mouse_event(flags, dx, dy, 0, UIntPtr.Zero);

    /// <summary>A real (hook-visible) absolute pointer move across the virtual desktop.</summary>
    static void MoveTo(int x, int y)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77), vw = GetSystemMetrics(78), vh = GetSystemMetrics(79);
        int nx = (int)Math.Round((x - vx) * 65535.0 / (vw - 1)), ny = (int)Math.Round((y - vy) * 65535.0 / (vh - 1));
        mouse_event(0x0001 | 0x8000 | 0x4000, nx, ny, 0, UIntPtr.Zero);
    }

    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
}

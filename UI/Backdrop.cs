using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileWheel.Native;

namespace FileWheel.UI;

/// <summary>
/// Supplies the blurred "what's behind the glass" image. Normally a quick capture of the screen;
/// snapshot tests swap in a fixed picture so renders are reproducible.
/// </summary>
public static class Backdrop
{
    /// <summary>Returns a small (already downscaled) image of the given physical-pixel screen rectangle.</summary>
    public static Func<RECT, BitmapSource?> Provider { get; set; } = CaptureScreen;

    public static BitmapSource? Capture(RECT rect) => rect.Width <= 0 || rect.Height <= 0 ? null : Provider(rect);

    /// <summary>The downscale factor of captured backdrops (1 captured pixel = this many screen pixels).</summary>
    public const int Downscale = 6;

    /// <summary>Snapshot tests replace the real monitor with a fixed virtual screen.</summary>
    public static RECT? VirtualScreen { get; set; }

    /// <summary>Work area, full bounds and DPI scale of the monitor under a point.</summary>
    public static (RECT WorkArea, RECT Bounds, double Scale) Screen(POINT at) =>
        VirtualScreen is { } v ? (v, v, 1) : Win32.MonitorAt(at);

    /// <summary>The part of a monitor capture (taken of <paramref name="bounds"/>) that lies under <paramref name="area"/>.</summary>
    public static Int32Rect Region(RECT bounds, RECT area) => new(
        (area.Left - bounds.Left) / Downscale, (area.Top - bounds.Top) / Downscale,
        Math.Max(1, area.Width / Downscale), Math.Max(1, area.Height / Downscale));

    /// <summary>Copies the screen straight into a 1/6-size bitmap (GDI averages the pixels on the way down).</summary>
    static BitmapSource? CaptureScreen(RECT r)
    {
        int w = Math.Max(1, r.Width / Downscale), h = Math.Max(1, r.Height / Downscale);
        IntPtr screen = GetDC(IntPtr.Zero), memory = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            memory = CreateCompatibleDC(screen);
            bitmap = CreateCompatibleBitmap(screen, w, h);
            previous = SelectObject(memory, bitmap);
            SetStretchBltMode(memory, 4); // HALFTONE: a proper average, not dropped pixels
            SetBrushOrgEx(memory, 0, 0, IntPtr.Zero);
            if (!StretchBlt(memory, 0, 0, w, h, screen, r.Left, r.Top, r.Width, r.Height, 0x00CC0020)) return null; // SRCCOPY
            SelectObject(memory, previous);
            previous = IntPtr.Zero;
            var header = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var pixels = new byte[w * h * 4];
            if (GetDIBits(screen, bitmap, 0, (uint)h, pixels, ref header, 0) == 0) return null;
            return Finish(pixels, w, h);
        }
        catch
        {
            return null; // Secure desktop, locked session, etc. The tint alone still looks right.
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>A touch of extra saturation, like real frosted glass ("vibrancy"), then an opaque bitmap.</summary>
    static BitmapSource Finish(byte[] px, int w, int h)
    {
        const double s = 1.35, lr = 0.3086 * (1 - s), lg = 0.6094 * (1 - s), lb = 0.0820 * (1 - s);
        for (int i = 0; i < px.Length; i += 4)
        {
            double b = px[i], g = px[i + 1], r = px[i + 2];
            px[i] = Byte(r * lr + g * lg + b * (lb + s));
            px[i + 1] = Byte(r * lr + g * (lg + s) + b * lb);
            px[i + 2] = Byte(r * (lr + s) + g * lg + b * lb);
            px[i + 3] = 255;
        }
        var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        source.Freeze();
        return source;
    }

    static byte Byte(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);

    /// <summary>Snapshot mode: crop backdrops out of a fixed picture laid over a virtual screen.</summary>
    public static void UsePicture(string file, int screenWidth, int screenHeight)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(Path.GetFullPath(file));
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        var scaled = new TransformedBitmap(image, new ScaleTransform(screenWidth / (double)image.PixelWidth, screenHeight / (double)image.PixelHeight));
        var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        var all = new byte[width * height * 4];
        bgra.CopyPixels(all, width * 4, 0);
        Provider = r =>
        {
            int x0 = Math.Max(0, r.Left), y0 = Math.Max(0, r.Top), x1 = Math.Min(width, r.Right), y1 = Math.Min(height, r.Bottom);
            if (x1 <= x0 || y1 <= y0) return null;
            int w = Math.Max(1, (x1 - x0) / Downscale), h = Math.Max(1, (y1 - y0) / Downscale);
            var px = new byte[w * h * 4];
            for (int oy = 0; oy < h; oy++)
            for (int ox = 0; ox < w; ox++)
            {
                // Box-average each 6×6 block, like the screen capture's halftone shrink.
                int sx0 = x0 + ox * Downscale, sy0 = y0 + oy * Downscale;
                int sx1 = Math.Min(x1, sx0 + Downscale), sy1 = Math.Min(y1, sy0 + Downscale);
                int b = 0, g = 0, rr = 0, n = 0;
                for (int y = sy0; y < sy1; y++)
                for (int x = sx0; x < sx1; x++)
                {
                    int i = (y * width + x) * 4;
                    b += all[i]; g += all[i + 1]; rr += all[i + 2]; n++;
                }
                int o = (oy * w + ox) * 4;
                px[o] = (byte)(b / Math.Max(1, n));
                px[o + 1] = (byte)(g / Math.Max(1, n));
                px[o + 2] = (byte)(rr / Math.Max(1, n));
            }
            return Finish(px, w, h);
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);
    [DllImport("gdi32.dll")] static extern bool StretchBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);
}

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace FileWheel.UI;

/// <summary>
/// Every color a surface uses. "Glass" palettes are computed from the blurred screen behind the
/// surface: light or dark glass depending on brightness, and a highlight color taken from the most
/// vivid color back there (or the Windows accent color when the background is plain).
/// "Tangerine" is the fixed orange look of the reference video.
/// </summary>
public sealed class Palette
{
    public required bool Dark { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentHover { get; init; }
    public required Color AccentPressed { get; init; }
    public required Color AccentText { get; init; }
    public required Color Ink { get; init; }
    public required Color InkSoft { get; init; }
    public required Color InkMuted { get; init; }
    public required Color Danger { get; init; }

    public required Brush PanelTint { get; init; }
    public required double PanelTintOpacity { get; init; }
    public required Brush ToastTint { get; init; }
    public required double ToastTintOpacity { get; init; }
    public required Brush DiscTint { get; init; }
    public required double DiscTintOpacity { get; init; }

    /// <summary>Chips, cards, segmented controls.</summary>
    public required Color Surface { get; init; }
    public required Color SurfaceBorder { get; init; }
    /// <summary>Recessed preview areas.</summary>
    public required Color Well { get; init; }
    public required Color WellBorder { get; init; }
    public required Color Field { get; init; }
    public required Color SoftFill { get; init; }
    public required Color SoftFillHover { get; init; }
    public required Color Hairline { get; init; }
    public required Color Track { get; init; }
    public required Color CloseFill { get; init; }
    public required Color CloseFillHover { get; init; }

    public required Color WedgeTop { get; init; }
    public required Color WedgeBottom { get; init; }
    public required Color WedgeStroke { get; init; }
    public required Color WedgeInk { get; init; }
    public required Color PillTop { get; init; }
    public required Color PillBottom { get; init; }
    public required Color Ring { get; init; }
    public required Color RimTop { get; init; }
    public required Color RimBottom { get; init; }
    public required Color Sheen { get; init; }

    public Color HoverTop => Shift(Accent, 0.05);
    public Color HoverBottom => Shift(Accent, -0.06);

    // ---------- Choosing ----------

    /// <summary>The palette for a surface whose (downscaled) backdrop is <paramref name="backdrop"/>.</summary>
    public static Palette For(BitmapSource? backdrop, Int32Rect? region = null) =>
        Settings.Current.Theme == "Tangerine" ? Tangerine : Glass(backdrop, region);

    public static Palette Tangerine { get; } = new()
    {
        Dark = false,
        Accent = Theme.Hex("#F2532A"),
        AccentHover = Theme.Hex("#FF6838"),
        AccentPressed = Theme.Hex("#D9441F"),
        AccentText = Theme.Hex("#8A3A1E"),
        Ink = Theme.Hex("#3B2920"),
        InkSoft = Theme.Hex("#6A4535"),
        InkMuted = Theme.Hex("#9A5F48"),
        Danger = Theme.Hex("#8B1A0E"),
        PanelTint = Gradient(90, ("#F6B43C", 0), ("#F49450", 0.42), ("#F27C58", 0.7), ("#F8906E", 1)),
        PanelTintOpacity = 0.9,
        ToastTint = Gradient(45, ("#F9B467", 0), ("#F49066", 1)),
        ToastTintOpacity = 0.82,
        DiscTint = Diagonal(("#F4B23C", 0), ("#F48A4A", 0.5), ("#F25E4C", 1)),
        DiscTintOpacity = 0.8,
        Surface = White(0.22), SurfaceBorder = White(0.25),
        Well = White(0.10), WellBorder = White(0.18),
        Field = Theme.Hex("#B8FFF6F2"),
        SoftFill = White(0.24), SoftFillHover = White(0.36),
        Hairline = Color.FromArgb(36, 0x76, 0x3A, 0x22),
        Track = White(0.30),
        CloseFill = Color.FromArgb(31, 0x5A, 0x2A, 0x1A), CloseFillHover = Color.FromArgb(56, 0x5A, 0x2A, 0x1A),
        WedgeTop = Color.FromArgb(128, 255, 246, 234), WedgeBottom = Color.FromArgb(100, 255, 234, 214),
        WedgeStroke = White(0.5), WedgeInk = Theme.Hex("#4B3127"),
        PillTop = Color.FromArgb(150, 255, 232, 205), PillBottom = Color.FromArgb(120, 255, 214, 178),
        Ring = White(0.62), RimTop = White(0.9), RimBottom = White(0.35), Sheen = White(0.14),
    };

    public static Palette Glass(BitmapSource? backdrop, Int32Rect? region = null)
    {
        var (luma, vivid) = Analyze(backdrop, region);
        var accent = Normalize(vivid ?? WindowsAccent());
        bool dark = luma < 0.48;
        if (dark)
        {
            return new Palette
            {
                Dark = true,
                Accent = accent,
                AccentHover = Shift(accent, 0.06),
                AccentPressed = Shift(accent, -0.06),
                AccentText = WithLightness(accent, 0.74),
                Ink = Theme.Hex("#F5F5F7"),
                InkSoft = Color.FromArgb(220, 245, 245, 247),
                InkMuted = Color.FromArgb(150, 245, 245, 247),
                Danger = Theme.Hex("#FF8A80"),
                PanelTint = Solid("#16161A"), PanelTintOpacity = 0.62,
                ToastTint = Solid("#16161A"), ToastTintOpacity = 0.64,
                DiscTint = Solid("#0E0E12"), DiscTintOpacity = 0.58,
                Surface = White(0.10), SurfaceBorder = White(0.14),
                Well = Color.FromArgb(46, 0, 0, 0), WellBorder = White(0.10),
                Field = White(0.12),
                SoftFill = White(0.12), SoftFillHover = White(0.20),
                Hairline = White(0.12),
                Track = White(0.22),
                CloseFill = White(0.12), CloseFillHover = White(0.22),
                WedgeTop = White(0.13), WedgeBottom = White(0.06),
                WedgeStroke = White(0.28), WedgeInk = Theme.Hex("#F5F5F7"),
                PillTop = Color.FromArgb(92, 0, 0, 0), PillBottom = Color.FromArgb(118, 0, 0, 0),
                Ring = White(0.38), RimTop = White(0.55), RimBottom = White(0.14), Sheen = White(0.08),
            };
        }
        return new Palette
        {
            Dark = false,
            Accent = accent,
            AccentHover = Shift(accent, 0.06),
            AccentPressed = Shift(accent, -0.06),
            AccentText = WithLightness(accent, 0.36),
            Ink = Theme.Hex("#1C1C1E"),
            InkSoft = Color.FromArgb(205, 28, 28, 30),
            InkMuted = Color.FromArgb(145, 28, 28, 30),
            Danger = Theme.Hex("#C42B1C"),
            PanelTint = Solid("#FFFFFF"), PanelTintOpacity = 0.56,
            ToastTint = Solid("#FFFFFF"), ToastTintOpacity = 0.58,
            DiscTint = Solid("#FFFFFF"), DiscTintOpacity = 0.24,
            Surface = White(0.42), SurfaceBorder = White(0.7),
            Well = Color.FromArgb(14, 0, 0, 0), WellBorder = White(0.5),
            Field = White(0.72),
            SoftFill = White(0.48), SoftFillHover = White(0.7),
            Hairline = Color.FromArgb(26, 0, 0, 0),
            Track = Color.FromArgb(34, 0, 0, 0),
            CloseFill = Color.FromArgb(22, 0, 0, 0), CloseFillHover = Color.FromArgb(40, 0, 0, 0),
            WedgeTop = White(0.50), WedgeBottom = White(0.34),
            WedgeStroke = White(0.8), WedgeInk = Theme.Hex("#1C1C1E"),
            PillTop = White(0.66), PillBottom = White(0.5),
            Ring = White(0.85), RimTop = White(0.95), RimBottom = White(0.4), Sheen = White(0.16),
        };
    }

    // ---------- Looking at the backdrop ----------

    /// <summary>Average brightness (0–1) and the most vivid hue family behind a surface, if there is one.</summary>
    public static (double Luma, Color? Vivid) Analyze(BitmapSource? image, Int32Rect? region = null)
    {
        if (image == null) return (0.6, null);
        try
        {
            var rect = region ?? new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight);
            rect = Clip(rect, image.PixelWidth, image.PixelHeight);
            if (rect.Width <= 0 || rect.Height <= 0) return (0.6, null);
            var source = new FormatConvertedBitmap(new CroppedBitmap(image, rect), PixelFormats.Bgra32, null, 0);
            var pixels = new byte[rect.Width * rect.Height * 4];
            source.CopyPixels(pixels, rect.Width * 4, 0);

            const int Bins = 24;
            var weight = new double[Bins];
            var sumR = new double[Bins];
            var sumG = new double[Bins];
            var sumB = new double[Bins];
            double luma = 0;
            int count = 0;
            int step = Math.Max(1, pixels.Length / 4 / 12000) * 4;
            for (int i = 0; i < pixels.Length; i += step)
            {
                double b = pixels[i] / 255.0, g = pixels[i + 1] / 255.0, r = pixels[i + 2] / 255.0;
                luma += 0.2126 * r + 0.7152 * g + 0.0722 * b;
                count++;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), chroma = max - min;
                if (chroma < 0.12) continue;
                double hue = max == r ? ((g - b) / chroma % 6 + 6) % 6 : max == g ? (b - r) / chroma + 2 : (r - g) / chroma + 4;
                int bin = (int)(hue / 6 * Bins) % Bins;
                double w = chroma * chroma;
                weight[bin] += w;
                sumR[bin] += r * w;
                sumG[bin] += g * w;
                sumB[bin] += b * w;
            }
            if (count == 0) return (0.6, null);
            // Merge each bin with its neighbours so a gradient's hue family wins over a stray pixel.
            int best = -1;
            double bestWeight = 0;
            for (int i = 0; i < Bins; i++)
            {
                double w = weight[i] + 0.5 * (weight[(i + 1) % Bins] + weight[(i + Bins - 1) % Bins]);
                if (w > bestWeight) { bestWeight = w; best = i; }
            }
            Color? vivid = null;
            if (best >= 0 && weight[best] > 0 && bestWeight / count > 0.025)
                vivid = Color.FromRgb((byte)(sumR[best] / weight[best] * 255), (byte)(sumG[best] / weight[best] * 255), (byte)(sumB[best] / weight[best] * 255));
            return (luma / count, vivid);
        }
        catch { return (0.6, null); }
    }

    static Int32Rect Clip(Int32Rect r, int w, int h)
    {
        int x = Math.Clamp(r.X, 0, w), y = Math.Clamp(r.Y, 0, h);
        return new Int32Rect(x, y, Math.Clamp(r.Width, 0, w - x), Math.Clamp(r.Height, 0, h - y));
    }

    /// <summary>The user's Windows accent color (Settings → Personalization → Colors).</summary>
    static Color WindowsAccent()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int abgr)
                return Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
        }
        catch { }
        return Theme.Hex("#0A84FF");
    }

    /// <summary>Keeps the highlight vivid and readable with white text on it.</summary>
    static Color Normalize(Color c)
    {
        var (h, s, l) = ToHsl(c);
        return FromHsl(h, Math.Clamp(Math.Max(s, 0.62), 0, 0.95), Math.Clamp(l, 0.45, 0.56));
    }

    public static Color Shift(Color c, double delta)
    {
        var (h, s, l) = ToHsl(c);
        var shifted = FromHsl(h, s, Math.Clamp(l + delta, 0, 1));
        return Color.FromArgb(c.A, shifted.R, shifted.G, shifted.B);
    }

    static Color WithLightness(Color c, double l)
    {
        var (h, s, _) = ToHsl(c);
        return FromHsl(h, s, l);
    }

    static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d < 1e-6) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? ((g - b) / d + (g < b ? 6 : 0)) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    static Color FromHsl(double h, double s, double l)
    {
        double Channel(double p, double q, double t)
        {
            t = (t % 1 + 1) % 1;
            if (t < 1 / 6.0) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
            return p;
        }
        if (s < 1e-6) { byte v = (byte)Math.Round(l * 255); return Color.FromRgb(v, v, v); }
        double q2 = l < 0.5 ? l * (1 + s) : l + s - l * s, p2 = 2 * l - q2;
        return Color.FromRgb((byte)Math.Round(Channel(p2, q2, h + 1 / 3.0) * 255), (byte)Math.Round(Channel(p2, q2, h) * 255), (byte)Math.Round(Channel(p2, q2, h - 1 / 3.0) * 255));
    }

    // ---------- Brush helpers ----------

    static Color White(double alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), 255, 255, 255);

    static Brush Solid(string hex)
    {
        var b = new SolidColorBrush(Theme.Hex(hex));
        b.Freeze();
        return b;
    }

    static Brush Gradient(double angle, params (string Hex, double At)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        if (angle == 45) { b.StartPoint = new Point(0, 0); b.EndPoint = new Point(1, 1); }
        foreach (var (hex, at) in stops) b.GradientStops.Add(new GradientStop(Theme.Hex(hex), at));
        b.Freeze();
        return b;
    }

    static Brush Diagonal(params (string Hex, double At)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0.85, 0), EndPoint = new Point(0.15, 1) };
        foreach (var (hex, at) in stops) b.GradientStops.Add(new GradientStop(Theme.Hex(hex), at));
        b.Freeze();
        return b;
    }

    /// <summary>Publishes this palette to a window's resources so the XAML styles pick it up.</summary>
    public void ApplyTo(ResourceDictionary resources)
    {
        void Set(string key, Color c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            resources[key] = brush;
        }
        Set("Accent", Accent);
        Set("AccentHover", AccentHover);
        Set("AccentPressed", AccentPressed);
        Set("Ink", Ink);
        Set("InkSoft", InkSoft);
        Set("InkMuted", InkMuted);
        Set("Hairline", Hairline);
        Set("FieldFill", Field);
        Set("SoftFill", SoftFill);
        Set("SoftFillHover", SoftFillHover);
        Set("SoftBorder", SurfaceBorder);
        Set("Track", Track);
        Set("CloseFill", CloseFill);
        Set("CloseFillHover", CloseFillHover);
    }
}

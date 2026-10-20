using System.Windows;
using System.Windows.Controls;
using Orientation = ImageMagick.Orientation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileWheel.Core;
using ImageMagick;

namespace FileWheel.UI.Editors;

public sealed record PreviewImage(BitmapSource Bitmap, int SourceWidth, int SourceHeight);

/// <summary>Fast, orientation-correct previews of any image Magick can read.</summary>
public static class Preview
{
    static readonly Dictionary<string, string> stills = [];

    /// <summary>For a video, a frame (a second in) to show and edit on; any other file is itself.</summary>
    public static string Still(string path)
    {
        if (Formats.Kind(path) != FileKind.Video) return path;
        lock (stills)
        {
            if (stills.TryGetValue(path, out var png) && File.Exists(png)) return png;
            var info = Media.Probe(path);
            png = Path.Combine(Output.WorkFolder, "still-" + Guid.NewGuid().ToString("N") + ".png");
            Media.Frame(path, Math.Min(1, info.Duration / 3), png);
            stills[path] = png;
            return png;
        }
    }

    public static (int Width, int Height) SourceSize(string path)
    {
        path = Still(path);
        using var ping = new MagickImage();
        ping.Ping(path);
        bool swap = ping.Orientation is Orientation.LeftTop or Orientation.RightTop or Orientation.RightBottom or Orientation.LeftBottom;
        return swap ? ((int)ping.Height, (int)ping.Width) : ((int)ping.Width, (int)ping.Height);
    }

    public static MagickImage LoadMagick(string path, int maxPixels)
    {
        path = Still(path);
        var settings = new MagickReadSettings();
        if (Formats.Ext(path) == "jpg") settings.SetDefine(MagickFormat.Jpeg, "size", $"{maxPixels * 2}x{maxPixels * 2}");
        if (Formats.Ext(path) == "svg") settings.Density = new Density(144);
        var image = new MagickImage(path, settings);
        image.AutoOrient();
        if (image.Width > maxPixels || image.Height > maxPixels)
        {
            image.FilterType = FilterType.Triangle;
            image.Resize(new MagickGeometry((uint)maxPixels, (uint)maxPixels));
        }
        return image;
    }

    public static PreviewImage Load(string path, int maxPixels = 1400)
    {
        var (w, h) = SourceSize(path);
        using var image = LoadMagick(path, maxPixels);
        return new PreviewImage(Converter.ToBitmap(image), w, h);
    }

    /// <summary>Largest size of the given aspect that fits in a box.</summary>
    public static Size Fit(double width, double height, double boxWidth, double boxHeight)
    {
        double scale = Math.Min(boxWidth / width, boxHeight / height);
        return new Size(Math.Max(1, width * scale), Math.Max(1, height * scale));
    }

    /// <summary>A subtle "Loading…" placeholder for panels whose preview decodes in the background.</summary>
    public static TextBlock LoadingLabel() => new()
    {
        Text = "Loading preview…",
        FontFamily = Theme.UiFont,
        FontSize = 12,
        Foreground = Theme.Brush(Theme.InkMuted),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}

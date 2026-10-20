using ImageMagick;

namespace FileWheel.Core;

public sealed class Adjustments
{
    public double Brightness { get; set; }
    public double Contrast { get; set; }
    public double Saturation { get; set; }
    public double Warmth { get; set; }
    public int Rotation { get; set; }
    public bool FlipHorizontal { get; set; }
    public bool FlipVertical { get; set; }
    /// <summary>Output scale in percent (100 = unchanged).</summary>
    public double Scale { get; set; } = 100;

    public bool IsIdentity => Brightness == 0 && Contrast == 0 && Saturation == 0 && Warmth == 0 &&
                              Rotation % 360 == 0 && !FlipHorizontal && !FlipVertical && Math.Abs(Scale - 100) < 0.01;
}

public enum RedactStyle { Box, Blur, Pixelate }

/// <summary>A rectangle in 0–1 image coordinates.</summary>
public readonly record struct NormRect(double X, double Y, double W, double H)
{
    public MagickGeometry ToPixels(uint width, uint height)
    {
        int x = (int)Math.Round(Math.Clamp(X, 0, 1) * width), y = (int)Math.Round(Math.Clamp(Y, 0, 1) * height);
        uint w = (uint)Math.Clamp(Math.Round(W * width), 1, width - x), h = (uint)Math.Clamp(Math.Round(H * height), 1, height - y);
        return new MagickGeometry(x, y, w, h);
    }
}

public sealed record MetaEntry(string Group, string Name, string Value);

public static class ImageTools
{
    public static string OutputExt(string source)
    {
        var ext = Formats.Ext(source);
        return ext is "svg" or "gif" or "ico" or "heic" && !Formats.CanEncodeImage(ext) || ext is "svg" ? "png" : ext;
    }

    public static string Crop(string source, NormRect rect)
    {
        using var image = Converter.LoadImage(source);
        image.Crop(rect.ToPixels(image.Width, image.Height));
        image.ResetPage();
        var ext = OutputExt(source);
        return Output.Save(source, " Cropped", ext, temp => Converter.WriteImage(image, ext, temp));
    }

    public static void Adjust(IMagickImage<byte> image, Adjustments a)
    {
        if (a.Rotation % 360 != 0) { image.Rotate(a.Rotation); image.ResetPage(); }
        if (a.FlipHorizontal) image.Flop();
        if (a.FlipVertical) image.Flip();
        if (Math.Abs(a.Scale - 100) > 0.01)
            image.Resize(new Percentage(Math.Clamp(a.Scale, 1, 400)));
        if (a.Brightness != 0 || a.Contrast != 0)
            image.BrightnessContrast(new Percentage(a.Brightness * 0.6), new Percentage(a.Contrast * 0.6));
        if (a.Saturation != 0)
            image.Modulate(new Percentage(100), new Percentage(100 + a.Saturation), new Percentage(100));
        if (a.Warmth != 0)
        {
            image.Evaluate(Channels.Red, EvaluateOperator.Multiply, 1 + a.Warmth / 500.0);
            image.Evaluate(Channels.Blue, EvaluateOperator.Multiply, 1 - a.Warmth / 500.0);
        }
    }

    public static string Edit(string source, Adjustments a)
    {
        using var image = Converter.LoadImage(source);
        Adjust(image, a);
        var ext = OutputExt(source);
        return Output.Save(source, " Edited", ext, temp => Converter.WriteImage(image, ext, temp));
    }

    public static void Redact(IMagickImage<byte> image, IEnumerable<(NormRect Rect, RedactStyle Style)> regions)
    {
        foreach (var (rect, style) in regions)
        {
            var g = rect.ToPixels(image.Width, image.Height);
            switch (style)
            {
                case RedactStyle.Box:
                    using (var box = new MagickImage(MagickColors.Black, g.Width, g.Height))
                        image.Composite(box, g.X, g.Y, CompositeOperator.Over);
                    break;
                case RedactStyle.Blur:
                    using (var part = image.CloneArea(g))
                    {
                        // Downscale first so the blur destroys detail rather than just softening it.
                        var small = Math.Max(1, Math.Min(part.Width, part.Height) / 24);
                        part.Resize(new MagickGeometry(Math.Max(1, part.Width / small), Math.Max(1, part.Height / small)) { IgnoreAspectRatio = true });
                        part.Resize(new MagickGeometry(g.Width, g.Height) { IgnoreAspectRatio = true });
                        part.Blur(0, Math.Max(4, Math.Min(g.Width, g.Height) / 10.0));
                        image.Composite(part, g.X, g.Y, CompositeOperator.Over);
                    }
                    break;
                default:
                    using (var part = image.CloneArea(g))
                    {
                        uint block = (uint)Math.Max(6, Math.Min(image.Width, image.Height) / 60);
                        part.Sample(new MagickGeometry(Math.Max(1, g.Width / block), Math.Max(1, g.Height / block)) { IgnoreAspectRatio = true });
                        part.Sample(new MagickGeometry(g.Width, g.Height) { IgnoreAspectRatio = true });
                        image.Composite(part, g.X, g.Y, CompositeOperator.Over);
                    }
                    break;
            }
        }
    }

    public static string SaveRedacted(string source, IEnumerable<(NormRect, RedactStyle)> regions)
    {
        using var image = Converter.LoadImage(source);
        Redact(image, regions);
        image.Strip();
        var ext = OutputExt(source);
        return Output.Save(source, " Redacted", ext, temp => Converter.WriteImage(image, ext, temp));
    }

    // ---------- Compress ----------

    public static string CompressExt(string source, string format) => format switch
    {
        "jpg" => "jpg",
        "webp" => Formats.CanEncodeImage("webp") ? "webp" : "jpg",
        _ => Formats.Ext(source) is "jpg" or "webp" or "avif" or "heic" or "png" ? Formats.Ext(source) : "jpg",
    };

    public static byte[] CompressBytes(IMagickImage<byte> source, string ext, int quality, int maxDimension)
    {
        using var image = source.Clone();
        if (maxDimension > 0 && Math.Max(image.Width, image.Height) > maxDimension)
            image.Resize(new MagickGeometry((uint)maxDimension, (uint)maxDimension));
        image.Strip();
        image.Quality = (uint)Math.Clamp(quality, 5, 100);
        if (ext is "jpg")
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaAction.Remove);
            image.Settings.Interlace = Interlace.Jpeg;
        }
        if (ext == "png")
        {
            // Lossy palette reduction scaled by quality, then maximum zlib compression.
            if (quality < 95)
                image.Quantize(new QuantizeSettings { Colors = (uint)Math.Clamp(16 + quality * 2.4, 16, 256), DitherMethod = DitherMethod.FloydSteinberg });
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", "9");
        }
        return image.ToByteArray(Formats.MagickFormatFor(ext));
    }

    public static string Compress(string source, string format, int quality, int maxDimension)
    {
        using var image = Converter.LoadImage(source);
        var ext = CompressExt(source, format);
        var bytes = CompressBytes(image, ext, quality, maxDimension);
        return Output.SaveBytes(source, " Compressed", ext, bytes);
    }

    // ---------- Metadata ----------

    public static List<MetaEntry> ReadMetadata(string path)
    {
        var list = new List<MetaEntry>();
        var info = new FileInfo(path);
        list.Add(new("File", "Name", info.Name));
        list.Add(new("File", "Size", Formats.SizeText(info.Length) + $"  ({info.Length:N0} bytes)"));
        list.Add(new("File", "Created", info.CreationTime.ToString("g")));
        list.Add(new("File", "Modified", info.LastWriteTime.ToString("g")));
        list.Add(new("File", "Folder", info.DirectoryName ?? ""));

        switch (Formats.Kind(path))
        {
            case FileKind.Image:
                using (var image = new MagickImage())
                {
                    image.Ping(path);
                    list.Add(new("Image", "Dimensions", $"{image.Width} × {image.Height} px"));
                    list.Add(new("Image", "Format", image.Format.ToString().ToUpperInvariant()));
                    list.Add(new("Image", "Color space", image.ColorSpace.ToString()));
                    list.Add(new("Image", "Bit depth", image.Depth + "-bit"));
                    if (image.Density.X > 0) list.Add(new("Image", "Resolution", $"{image.Density.X:0} × {image.Density.Y:0} {(image.Density.Units == DensityUnit.PixelsPerCentimeter ? "ppcm" : "dpi")}"));
                    if (image.HasAlpha) list.Add(new("Image", "Transparency", "Yes"));
                }
                try
                {
                    using var full = new MagickImage(path, new MagickReadSettings { Width = 1, Height = 1 });
                    var exif = full.GetExifProfile();
                    if (exif != null)
                        foreach (var v in exif.Values)
                        {
                            var text = FormatExif(v);
                            if (!string.IsNullOrWhiteSpace(text)) list.Add(new(v.Tag.ToString().StartsWith("GPS") ? "Location" : "Camera", Spaced(v.Tag.ToString()), text));
                        }
                    if (full.GetIptcProfile() is { } iptc)
                        foreach (var v in iptc.Values) list.Add(new("IPTC", Spaced(v.Tag.ToString()), v.Value));
                    if (full.GetXmpProfile() != null) list.Add(new("Other", "XMP data", "Present"));
                    if (full.GetColorProfile() is { } icc) list.Add(new("Other", "Color profile", icc.Description ?? "Embedded"));
                }
                catch { }
                break;
            case FileKind.Pdf:
                try
                {
                    using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
                    var i = doc.Information;
                    list.Add(new("Document", "Pages", doc.NumberOfPages.ToString()));
                    list.Add(new("Document", "PDF version", doc.Version.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)));
                    var first = doc.GetPage(1);
                    list.Add(new("Document", "Page size", $"{first.Width / 72:0.##} × {first.Height / 72:0.##} in"));
                    void Add(string name, string? value) { if (!string.IsNullOrWhiteSpace(value)) list.Add(new("Properties", name, value)); }
                    Add("Title", i.Title); Add("Author", i.Author); Add("Subject", i.Subject); Add("Keywords", i.Keywords);
                    Add("Creator", i.Creator); Add("Producer", i.Producer); Add("Created", i.CreationDate); Add("Modified", i.ModifiedDate);
                }
                catch (Exception e) { list.Add(new("Document", "Error", e.Message)); }
                break;
            case FileKind.Audio:
            case FileKind.Video:
                try
                {
                    var m = Media.Probe(path);
                    if (m.Duration > 0) list.Add(new("Media", "Duration", TimeSpan.FromSeconds(m.Duration).ToString(m.Duration >= 3600 ? @"h\:mm\:ss" : @"m\:ss")));
                    if (m.Width > 0) list.Add(new("Media", "Video size", $"{m.Width} × {m.Height}"));
                    foreach (var line in m.Raw.Split('\n').Select(l => l.Trim()))
                    {
                        if (line.StartsWith("Stream #")) list.Add(new("Streams", line.Contains("Video:") ? "Video" : line.Contains("Audio:") ? "Audio" : "Stream", line[(line.IndexOf(':', line.IndexOf(':') + 1) + 1)..].Trim()));
                        else if (line.Contains(" : ") && !line.StartsWith("Duration"))
                        {
                            var parts = line.Split(" : ", 2);
                            if (parts[0].Trim() is { Length: > 0 and < 30 } key && key != "Metadata") list.Add(new("Tags", key, parts[1].Trim()));
                        }
                    }
                }
                catch { }
                break;
        }
        return list;
    }

    public static bool CanStripMetadata(string path) => Formats.Kind(path) is FileKind.Image or FileKind.Pdf or FileKind.Audio or FileKind.Video;

    public static string StripMetadata(string path)
    {
        switch (Formats.Kind(path))
        {
            case FileKind.Image:
                using (var image = Converter.LoadImage(path))
                {
                    image.Strip();
                    var ext = OutputExt(path);
                    return Output.Save(path, " Clean", ext, temp => Converter.WriteImage(image, ext, temp, ext == "jpg" ? 95 : 0));
                }
            case FileKind.Pdf:
                return PdfTools.StripMetadata(path);
            default:
                return Media.StripMetadata(path, CancellationToken.None);
        }
    }

    static string FormatExif(IExifValue value)
    {
        try
        {
            var v = value.GetValue();
            return v switch
            {
                null => "",
                Rational r => r.Denominator == 1 ? r.Numerator.ToString() : r.ToDouble() < 1 ? $"{r.Numerator}/{r.Denominator}" : r.ToDouble().ToString("0.##"),
                Rational[] rs => string.Join(", ", rs.Select(r => r.ToDouble().ToString("0.#####"))),
                byte[] b => b.Length > 24 ? $"{b.Length} bytes" : BitConverter.ToString(b),
                Array a => string.Join(", ", a.Cast<object>()),
                _ => v.ToString()?.Trim('\0', ' ') ?? "",
            };
        }
        catch { return ""; }
    }

    static string Spaced(string name) => System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " ");
}

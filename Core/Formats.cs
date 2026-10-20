using ImageMagick;

namespace FileWheel.Core;

public enum FileKind { Image, Pdf, Text, Delimited, Subtitle, Office, Audio, Video, Ebook, Archive, Folder, Other }

/// <summary>
/// Share (apps), Same (Copy / Rename for a file's own format), Speed, Channels and Visualize are small
/// rings opened from a slice of another ring.
/// </summary>
public enum WheelMode { Convert, Tools, Actions, Share, Same, Speed, Channels, Visualize }

public enum Tool { Compress, Metadata, Edit, Annotate, AddBackground, Crop, Redact, Rotate, Split, Extract, Merge, Trim, Mute, Speed, Snapshot, Normalize, Visualize, Channels, Bleep, Organize, Unpack }

public enum FileAction { Share, Float, Copy, Move, CopyPath, Terminal }

/// <summary>One slice of the wheel. <paramref name="Hint"/> is what the middle pill says while it's hovered (defaults to the label).</summary>
public sealed record WheelItem(string Id, string Label, string? Icon = null, string? Hint = null);

/// <summary>Knows which conversions, tools and actions apply to a selection.</summary>
public static class Formats
{
    static readonly string[] ImageExts = ["jpg", "jpeg", "png", "webp", "bmp", "tif", "tiff", "gif", "ico", "avif", "heic", "heif", "svg", "jfif", "psd", "tga", "jp2"];
    static readonly string[] AudioExts = ["mp3", "wav", "flac", "ogg", "opus", "m4a", "aac", "aiff", "aif", "wma"];
    static readonly string[] VideoExts = ["mp4", "mov", "mkv", "webm", "avi", "wmv", "m4v", "mpg", "mpeg", "flv", "3gp"];
    static readonly string[] WordExts = ["doc", "docx", "odt", "rtf"];
    static readonly string[] SheetExts = ["xls", "xlsx", "ods"];
    static readonly string[] SlideExts = ["ppt", "pptx", "odp"];

    // Display order of image targets, matching the reference wheel (PNG at the top, then clockwise).
    static readonly string[] ImageTargetOrder = ["png", "jpg", "webp", "heic", "tiff", "avif", "bmp", "gif", "ico", "pdf", "docx"];

    /// <summary>The wheel item for a file's own format: it opens the Copy / Rename ring.</summary>
    public const string SameFormat = "same";
    public const string SameCopy = "same:copy";
    public const string SameRename = "same:rename";

    public static string Ext(string path)
    {
        // "Photos.tar.gz" is a TAR.GZ, not just a GZ.
        if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return "tgz";
        return Canonical(Path.GetExtension(path).TrimStart('.').ToLowerInvariant());
    }

    public static string Canonical(string ext) => ext switch
    {
        "jpeg" or "jfif" => "jpg",
        "tif" => "tiff",
        "heif" => "heic",
        "aif" => "aiff",
        "htm" => "html",
        "markdown" => "md",
        _ => ext,
    };

    public static FileKind Kind(string path)
    {
        if (Directory.Exists(path)) return FileKind.Folder;
        var ext = Ext(path);
        if (ImageExts.Contains(ext)) return FileKind.Image;
        if (ext == "pdf") return FileKind.Pdf;
        if (ext is "txt" or "md" or "html" or "log" or "json" or "xml") return FileKind.Text;
        if (ext is "csv" or "tsv") return FileKind.Delimited;
        if (ext is "srt" or "vtt" or "ass") return FileKind.Subtitle;
        if (WordExts.Contains(ext) || SheetExts.Contains(ext) || SlideExts.Contains(ext)) return FileKind.Office;
        if (AudioExts.Contains(ext)) return FileKind.Audio;
        if (VideoExts.Contains(ext)) return FileKind.Video;
        if (ext == "epub") return FileKind.Ebook;
        if (Archives.InputExts.Contains(ext)) return FileKind.Archive;
        return FileKind.Other;
    }

    public static bool IsImage(string path) => Kind(path) == FileKind.Image;

    // ---------- Engines ----------

    public static string? Ffmpeg => FindEngine("ffmpeg.exe");

    public static string? LibreOffice => new[]
    {
        Path.Combine(AppContext.BaseDirectory, "engines", "LibreOffice", "program", "soffice.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LibreOffice", "program", "soffice.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "LibreOffice", "program", "soffice.exe"),
    }.FirstOrDefault(File.Exists);

    static string? FindEngine(string name) => new[]
    {
        Path.Combine(AppContext.BaseDirectory, "engines", name),
        Path.Combine(AppContext.BaseDirectory, name),
    }.FirstOrDefault(File.Exists);

    static readonly Lazy<HashSet<string>> imageEncoders = new(ProbeImageEncoders, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Warms the encoder probe so the first wheel opens instantly.</summary>
    public static void Warm() => _ = imageEncoders.Value;

    public static bool CanEncodeImage(string ext) => imageEncoders.Value.Contains(ext);

    static HashSet<string> ProbeImageEncoders()
    {
        var result = new HashSet<string>();
        foreach (var ext in new[] { "png", "jpg", "webp", "bmp", "tiff", "gif", "ico", "avif", "heic" })
        {
            try
            {
                using var sample = new MagickImage(MagickColors.Coral, 16, 16);
                var bytes = sample.ToByteArray(MagickFormatFor(ext));
                using var decoded = new MagickImage(bytes);
                if (decoded.Width == 16) result.Add(ext);
            }
            catch
            {
                // Encoder not compiled into this Magick build; try Windows' own codecs below.
                if (ext == "heic" && WicCodecs.CanEncodeHeif()) result.Add(ext);
            }
        }
        return result;
    }

    public static MagickFormat MagickFormatFor(string ext) => Canonical(ext) switch
    {
        "jpg" => MagickFormat.Jpeg,
        "tiff" => MagickFormat.Tiff,
        "heic" => MagickFormat.Heic,
        "avif" => MagickFormat.Avif,
        "png" => MagickFormat.Png,
        "webp" => MagickFormat.WebP,
        "bmp" => MagickFormat.Bmp,
        "gif" => MagickFormat.Gif,
        "ico" => MagickFormat.Ico,
        _ => Enum.Parse<MagickFormat>(ext, true),
    };

    // ---------- Wheels ----------

    public static IReadOnlyList<WheelItem> Items(WheelMode mode, IReadOnlyList<string> paths) => mode switch
    {
        WheelMode.Convert => ConvertItems(paths),
        WheelMode.Tools => Tools(paths).Select(t => new WheelItem(t.ToString(), ToolLabel(t), t.ToString())).ToList(),
        WheelMode.Actions => Actions(paths).Select(a => new WheelItem(a.ToString(), ActionLabel(a), a.ToString())).ToList(),
        WheelMode.Same =>
        [
            new WheelItem(SameCopy, "COPY", "Copy", "Copy"),
            new WheelItem(SameRename, "RENAME", "Pen", "Rename"),
        ],
        WheelMode.Speed => [.. new[] { 0.25, 0.5, 0.75, 1.25, 1.5, 2, 4 }.Select(f => new WheelItem("speed:" + f.ToString(System.Globalization.CultureInfo.InvariantCulture), f.ToString(System.Globalization.CultureInfo.InvariantCulture) + "×", null, f.ToString(System.Globalization.CultureInfo.InvariantCulture) + "× speed"))],
        WheelMode.Channels =>
        [
            new WheelItem("ch:mono", "MONO", null, "Mono"),
            new WheelItem("ch:stereo", "STEREO", null, "Stereo"),
            new WheelItem("ch:swap", "SWAP L/R", null, "Swap sides"),
            new WheelItem("ch:left", "LEFT", null, "Left only"),
            new WheelItem("ch:right", "RIGHT", null, "Right only"),
        ],
        WheelMode.Visualize =>
        [
            new WheelItem("viz:image", "IMAGE", "Image", "Waveform PNG"),
            new WheelItem("viz:video", "VIDEO", "Visualize", "Waveform MP4"),
        ],
        _ => [],
    };

    /// <summary>The tools that open a small ring of choices instead of a panel.</summary>
    public static WheelMode? RingFor(Tool tool) => tool switch
    {
        Tool.Speed => WheelMode.Speed,
        Tool.Channels => WheelMode.Channels,
        Tool.Visualize => WheelMode.Visualize,
        _ => null,
    };

    public static List<string> ConvertTargets(IReadOnlyList<string> paths)
    {
        HashSet<string>? common = null;
        List<string> order = [];
        foreach (var path in paths)
        {
            var targets = TargetsFor(path);
            if (common == null) { common = [.. targets]; order = targets; }
            else common.IntersectWith(targets);
        }
        return common == null ? [] : order.Where(common.Contains).ToList();
    }

    /// <summary>
    /// The conversions, plus the files' own format as a "copy or edit" choice when they all share one,
    /// placed where that format sits in the usual order.
    /// </summary>
    static List<WheelItem> ConvertItems(IReadOnlyList<string> paths)
    {
        var items = ConvertTargets(paths).Select(t => new WheelItem(t, TargetLabel(t))).ToList();
        if (paths.Count == 0 || paths.Any(p => Directory.Exists(p))) return items;
        var exts = paths.Select(Ext).Distinct().ToList();
        if (exts.Count != 1 || exts[0].Length == 0 || Kind(paths[0]) == FileKind.Other) return items;
        var own = exts[0];
        var order = OrderFor(paths[0]);
        int rank = order.IndexOf(own);
        int index = rank < 0 ? items.Count : items.Count(i => order.IndexOf(i.Id) is var k && k >= 0 && k < rank);
        items.Insert(index, new WheelItem(SameFormat, TargetLabel(own), null, "Copy or rename"));
        return items;
    }

    static string TargetLabel(string ext) => ext == "tgz" ? "TAR.GZ" : ext.ToUpperInvariant();

    static List<string> TargetsFor(string path)
    {
        var ext = Ext(path);
        return OrderFor(path).Where(t => t != ext).ToList();
    }

    /// <summary>Every format a file of this kind can become, in wheel order (it may include its own format).</summary>
    static List<string> OrderFor(string path)
    {
        var ext = Ext(path);
        return Kind(path) switch
        {
            FileKind.Image => ImageTargetOrder.Where(t => t is "pdf" or "docx" || CanEncodeImage(t)).ToList(),
            FileKind.Pdf => ["docx", "pptx", "xlsx", "epub", "jpg", "png", "webp", "tiff", "txt", "md", "html", "pdf"],
            FileKind.Text => ext switch
            {
                "md" => ["html", "pdf", "docx", "epub", "txt", "rtf", "md"],
                "html" => ["pdf", "docx", "epub", "md", "txt", "rtf", "html"],
                "json" => Tabular.IsJsonTable(path) ? ["csv", "xlsx", "tsv", "html", "md", "txt", "json"] : ["txt", "pdf", "json"],
                "xml" => ["txt", "pdf", "xml"],
                _ => ["pdf", "docx", "epub", "html", "md", "rtf", "txt"],
            },
            FileKind.Delimited => [.. (MsOffice.Excel || LibreOffice != null ? new[] { "pdf" } : []), "xlsx", "csv", "tsv", "json", "html", "md"],
            FileKind.Subtitle => Ffmpeg != null ? ["srt", "vtt", "ass", "txt"] : ext == "ass" ? [] : ["srt", "vtt", "txt"],
            FileKind.Office => OfficeTargets(ext),
            FileKind.Audio when Ffmpeg != null => ["mp3", "wav", "m4a", "aac", "flac", "ogg", "opus", "aiff", "wma"],
            FileKind.Video when Ffmpeg != null => ["mp4", "mov", "mkv", "webm", "avi", "wmv", "gif", "mp3", "m4a", "wav"],
            FileKind.Ebook => ["pdf", "docx", "txt", "md", "html", "rtf", "epub"],
            FileKind.Archive or FileKind.Folder => Archives.Targets(),
            _ => [],
        };
    }

    /// <summary>Office documents: Microsoft Office (or LibreOffice) for faithful PDFs, and built-in readers for text and data.</summary>
    static List<string> OfficeTargets(string ext)
    {
        bool lo = LibreOffice != null;
        var list = new List<string>();
        if (WordExts.Contains(ext))
        {
            if (MsOffice.Word || lo) list.AddRange(["pdf", "docx", "odt", "rtf"]);
            if (MsOffice.Word || lo || ext == "docx") list.AddRange(["txt", "md", "html", "epub"]);
            if (MsOffice.Word || lo) list.AddRange(["png", "jpg"]);
        }
        else if (SheetExts.Contains(ext))
        {
            if (MsOffice.Excel || lo) list.AddRange(["pdf", "xlsx", "ods"]);
            if (MsOffice.Excel || lo || ext == "xlsx") list.AddRange(["csv", "tsv", "json", "html", "md"]);
        }
        else
        {
            if (MsOffice.PowerPoint || lo) list.AddRange(["pdf", "pptx", "odp"]);
            if (MsOffice.PowerPoint) list.AddRange(["png", "jpg"]);
        }
        if (!list.Contains(ext)) list.Add(ext);
        return list;
    }

    /// <summary>The kind of an Office file, for picking an engine.</summary>
    public static string OfficeFamily(string ext) => WordExts.Contains(ext) ? "word" : SheetExts.Contains(ext) ? "sheet" : "slides";

    /// <summary>True when there are real editing tools for these files (more than just Metadata).</summary>
    public static bool HasTools(IReadOnlyList<string> paths) => Tools(paths).Any(t => t != Tool.Metadata);

    public static List<Tool> Tools(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return [];
        var kinds = paths.Select(Kind).Distinct().ToList();
        if (kinds.Count != 1) return [Tool.Metadata];
        bool single = paths.Count == 1;
        return kinds[0] switch
        {
            // Same order as the reference wheel: Compress at the top, then clockwise.
            FileKind.Image when single => [Tool.Compress, Tool.Metadata, Tool.Edit, Tool.Annotate, Tool.AddBackground, Tool.Crop, Tool.Redact],
            FileKind.Image => [Tool.Compress, Tool.Metadata, Tool.Edit, Tool.AddBackground, Tool.Crop],
            FileKind.Pdf when single => [Tool.Compress, Tool.Metadata, Tool.Organize, Tool.Rotate, Tool.Split, Tool.Extract],
            FileKind.Pdf => [Tool.Merge, Tool.Compress, Tool.Metadata, Tool.Rotate, Tool.Split],
            FileKind.Video when single && Ffmpeg != null => [Tool.Compress, Tool.Metadata, Tool.Mute, Tool.Trim, Tool.Crop, Tool.Speed, Tool.Snapshot, Tool.Split, Tool.Redact],
            FileKind.Video when Ffmpeg != null => [Tool.Compress, Tool.Metadata, Tool.Mute, Tool.Speed],
            FileKind.Audio when single && Ffmpeg != null => [Tool.Compress, Tool.Metadata, Tool.Normalize, Tool.Visualize, Tool.Trim, Tool.Channels, Tool.Bleep],
            FileKind.Audio when Ffmpeg != null => [Tool.Compress, Tool.Metadata, Tool.Normalize, Tool.Channels],
            FileKind.Archive => [Tool.Unpack],
            FileKind.Folder => [],
            _ => [Tool.Metadata],
        };
    }

    public static List<FileAction> Actions(IReadOnlyList<string> paths) =>
        paths.Count == 0 ? [] : [FileAction.Share, FileAction.Float, FileAction.Copy, FileAction.Move, FileAction.CopyPath, FileAction.Terminal];

    public static string ToolLabel(Tool tool) => tool switch
    {
        Tool.AddBackground => "ADD BG",
        Tool.Unpack => "EXTRACT",
        _ => tool.ToString().ToUpperInvariant(),
    };

    public static string ActionLabel(FileAction action) => action switch
    {
        FileAction.CopyPath => "COPY PATH",
        _ => action.ToString().ToUpperInvariant(),
    };

    public static string ToolTitle(Tool tool) => tool switch
    {
        Tool.Crop => "Crop",
        Tool.AddBackground => "Add BG",
        Tool.Compress => "Compress",
        Tool.Metadata => "Metadata",
        Tool.Edit => "Edit Image",
        Tool.Annotate => "Annotate",
        Tool.Redact => "Redact",
        Tool.Rotate => "Rotate Pages",
        Tool.Split => "Split",
        Tool.Extract => "Extract Pages",
        Tool.Merge => "Merge PDFs",
        Tool.Trim => "Trim",
        Tool.Mute => "Remove Audio",
        Tool.Organize => "Organize Pages",
        Tool.Unpack => "Extract Archive",
        Tool.Snapshot => "Save a Frame",
        Tool.Normalize => "Normalize Loudness",
        _ => tool.ToString(),
    };

    public static string SizeText(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return mb < 10 ? $"{mb:0.0} MB" : $"{mb:0} MB";
        return $"{mb / 1024.0:0.0} GB";
    }

    public static long TotalSize(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (var p in paths)
        {
            try
            {
                if (File.Exists(p)) total += new FileInfo(p).Length;
                else if (Directory.Exists(p)) return -1;
            }
            catch { }
        }
        return total;
    }
}

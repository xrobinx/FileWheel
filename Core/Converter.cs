using System.Text;
using System.Windows.Media.Imaging;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace FileWheel.Core;

public sealed class JobResult
{
    public List<string> Outputs { get; } = [];
    public List<string> Errors { get; } = [];
    public bool Cancelled { get; set; }
}

/// <summary>Converts files to another format. Results are always new files saved beside the source.</summary>
public static class Converter
{
    /// <summary>Converts every file, reporting overall progress (0–1) and files finished.</summary>
    public static JobResult Run(IReadOnlyList<string> files, string target, Action<double, int> progress, CancellationToken ct)
    {
        var result = new JobResult();
        for (int i = 0; i < files.Count; i++)
        {
            if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
            int index = i;
            try
            {
                result.Outputs.AddRange(ConvertOne(files[i], target, f => progress((index + Math.Clamp(f, 0, 1)) / files.Count, index), ct));
            }
            catch (OperationCanceledException) { result.Cancelled = true; break; }
            catch (Exception e) { result.Errors.Add($"{Path.GetFileName(files[i])}: {Friendly(e)}"); }
            progress((i + 1.0) / files.Count, i + 1);
        }
        return result;
    }

    public static string Friendly(Exception e) => e switch
    {
        MagickException m => "The image couldn't be read or written (" + m.Message.Split('`', '@')[0].Trim().TrimEnd(':') + ").",
        UnauthorizedAccessException => "Windows denied access to this location.",
        _ => e.Message,
    };

    public static List<string> ConvertOne(string source, string target, Action<double> progress, CancellationToken ct)
    {
        if (!File.Exists(source) && !Directory.Exists(source)) throw new IOException("The file no longer exists.");
        target = Formats.Canonical(target);
        var kind = Formats.Kind(source);
        var ext = Formats.Ext(source);
        switch (kind)
        {
            case FileKind.Image:
                return [ImageTo(source, target, ct)];
            case FileKind.Pdf:
                return PdfTo(source, target, progress, ct);
            case FileKind.Text when ext == "json" && target is "csv" or "tsv" or "xlsx" or "html" or "md":
                return [Tabular.Save(source, Tabular.ReadJson(source), target, ct)];
            case FileKind.Text:
                return [Documents.Save(source, TextDoc(source, ext), target)];
            case FileKind.Delimited:
                return [Tabular.Save(source, TextDocuments.ParseDelimited(File.ReadAllText(source), ext == "tsv" ? '\t' : ','), target, ct)];
            case FileKind.Subtitle:
                return [Subtitles(source, ext, target, ct)];
            case FileKind.Office:
                return Office(source, ext, target, progress, ct);
            case FileKind.Audio:
            case FileKind.Video:
                return [Media.Convert(source, target, progress, ct)];
            case FileKind.Ebook:
                return [Documents.Save(source, Epub.ReadDoc(source), target)];
            case FileKind.Archive:
            case FileKind.Folder:
                return [Archives.Pack(source, target, ct)];
            default:
                throw new IOException("There's no converter for this kind of file.");
        }
    }

    /// <summary>A text file as a document: Markdown keeps its structure, web pages their headings, lists and local pictures, JSON/XML stay as code.</summary>
    static Doc TextDoc(string source, string ext)
    {
        var title = Path.GetFileNameWithoutExtension(source);
        if (ext == "html") return HtmlReader.ReadFile(source);
        var text = File.ReadAllText(source);
        if (ext == "md") return Doc.FromMarkdown(text, title);
        if (ext is "json" or "xml")
        {
            var code = new Doc { Title = title };
            code.Blocks.Add(new CodeBlock(text.Replace("\r", "").TrimEnd()));
            return code;
        }
        return Doc.FromPlainText(text, title);
    }

    // ---------- Images ----------

    public static MagickImage LoadImage(string path)
    {
        var settings = new MagickReadSettings();
        if (Formats.Ext(path) == "svg") settings.Density = new Density(192);
        var image = new MagickImage(path, settings);
        image.AutoOrient();
        return image;
    }

    static string ImageTo(string source, string target, CancellationToken ct)
    {
        if (target == "pdf") return ImagesToPdf(source, [source]);
        using var image = LoadImage(source);
        ct.ThrowIfCancellationRequested();
        if (target == "docx")
        {
            var (bytes, ext) = Formats.Ext(source) == "jpg" ? (File.ReadAllBytes(source), "jpg") : (image.ToByteArray(MagickFormat.Png), "png");
            var docx = new DocxWriter();
            docx.Picture(bytes, ext, (int)image.Width, (int)image.Height);
            return Output.Save(source, "", "docx", docx.Save);
        }
        return Output.Save(source, "", target, temp => WriteImage(image, target, temp));
    }

    /// <summary>Encodes with sensible quality settings and checks the result decodes.</summary>
    public static void WriteImage(IMagickImage<byte> image, string ext, string path, int quality = 0)
    {
        ext = Formats.Canonical(ext);
        using var copy = image.Clone();
        if (ext is "jpg" or "bmp")
        {
            copy.BackgroundColor = MagickColors.White;
            copy.Alpha(AlphaAction.Remove);
        }
        if (ext == "ico" && (copy.Width > 256 || copy.Height > 256)) copy.Resize(256, 256);
        copy.Quality = (uint)(quality > 0 ? quality : ext switch { "jpg" => 92, "webp" => 90, "avif" or "heic" => 80, _ => 92 });
        if (ext == "heic" && !MagickCanWrite(MagickFormat.Heic))
        {
            using var fs = File.Create(path);
            WicCodecs.EncodeHeif(ToBitmap(copy), fs);
            return;
        }
        copy.Write(path, Formats.MagickFormatFor(ext));
        using var check = new MagickImage();
        check.Ping(path);
        if (check.Width == 0) throw new IOException("The encoder produced an empty image.");
    }

    static bool MagickCanWrite(MagickFormat format) =>
        MagickNET.SupportedFormats.Any(f => f.Format == format && f.SupportsWriting);

    public static BitmapSource ToBitmap(IMagickImage<byte> image)
    {
        using var ms = new MemoryStream(image.ToByteArray(MagickFormat.Png));
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public static string ImagesToPdf(string nameSource, IReadOnlyList<string> images, string suffix = "")
    {
        using var doc = new PdfDocument();
        foreach (var path in images)
        {
            using var image = LoadImage(path);
            bool jpeg = Formats.Ext(path) == "jpg" && image.Orientation is ImageMagick.Orientation.Undefined or ImageMagick.Orientation.TopLeft;
            try
            {
                // Embed JPEGs as they are (no quality loss)…
                AddImagePage(doc, jpeg ? File.ReadAllBytes(path) : image.ToByteArray(MagickFormat.Png), image);
            }
            catch (Exception) when (jpeg)
            {
                // …unless the PDF library can't read that JPEG flavor (e.g. CMYK or unusual markers): re-encode it.
                image.ColorSpace = ColorSpace.sRGB;
                image.Quality = 95;
                AddImagePage(doc, image.ToByteArray(MagickFormat.Jpeg), image);
            }
        }
        return SavePdf(doc, nameSource, suffix);
    }

    public static void AddImagePage(PdfDocument doc, byte[] data, IMagickImage<byte> info)
    {
        double dpi = info.Density.X >= 72 && info.Density.Units != DensityUnit.Undefined
            ? (info.Density.Units == DensityUnit.PixelsPerCentimeter ? info.Density.X * 2.54 : info.Density.X)
            : 96;
        using var ms = new MemoryStream(data);
        using var image = XImage.FromStream(ms);
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(info.Width * 72.0 / dpi);
        page.Height = XUnit.FromPoint(info.Height * 72.0 / dpi);
        using var gfx = XGraphics.FromPdfPage(page);
        gfx.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
    }

    public static string SavePdf(PdfDocument doc, string nameSource, string suffix)
    {
        if (doc.PageCount == 0) throw new IOException("The PDF has no pages.");
        return Output.Save(nameSource, suffix, "pdf", temp => doc.Save(temp));
    }

    // ---------- PDF ----------

    static List<string> PdfTo(string source, string target, Action<double> progress, CancellationToken ct)
    {
        if (target == "txt")
        {
            using var doc = UglyToad.PdfPig.PdfDocument.Open(source);
            var pages = doc.GetPages().Select(p => ContentOrderTextExtractor.GetText(p).Trim()).ToList();
            if (pages.All(string.IsNullOrWhiteSpace)) throw new IOException("This PDF has no selectable text (it may be a scan).");
            return [Output.SaveBytes(source, "", target, Encoding.UTF8.GetBytes(string.Join(Environment.NewLine + Environment.NewLine, pages)))];
        }
        if (target == "docx")
        {
            // Word rebuilds a PDF faithfully — layout, fonts, tables, pictures. Without Word, the built-in reader keeps paragraphs, headings and pictures.
            if (MsOffice.Word) return [WithWord(source, progress, temp => OfficeEngine.Convert(source, "docx", temp, ct), "docx")];
            return [Documents.Save(source, PdfDocReader.Read(source, progress, ct), "docx")];
        }
        if (target is "html" or "md" or "epub")
        {
            // Same structure as the Word version: headings, lists, tables and pictures, written as a web page, Markdown or e-book.
            return [Documents.Save(source, PdfStructure(source, progress, ct), target)];
        }
        if (target == "xlsx") return [Output.Save(source, "", "xlsx", temp => Tabular.WriteXlsx(PdfTable(source), temp))];

        int count = PdfRenderer.PageCount(source);
        if (target == "pptx")
        {
            // One slide per page, each page a crisp picture.
            var deck = new PptxWriter();
            for (int p = 0; p < count; p++)
            {
                ct.ThrowIfCancellationRequested();
                using var page = PdfRenderer.Render(source, p, 150);
                page.BackgroundColor = MagickColors.White;
                page.Alpha(AlphaAction.Remove);
                page.Quality = 90;
                deck.Slide(page.ToByteArray(MagickFormat.Jpeg), "jpg", (int)page.Width, (int)page.Height);
                progress((p + 1.0) / count * 0.95);
            }
            return [Output.Save(source, "", "pptx", deck.Save)];
        }
        if (target == "tiff")
        {
            // One multi-page TIFF, the way scanners and fax tools expect it.
            using var pages = new MagickImageCollection();
            for (int p = 0; p < count; p++)
            {
                ct.ThrowIfCancellationRequested();
                var page = PdfRenderer.Render(source, p, 200);
                page.BackgroundColor = MagickColors.White;
                page.Alpha(AlphaAction.Remove);
                page.Settings.Compression = CompressionMethod.LZW;
                pages.Add(page);
                progress((p + 1.0) / count * 0.9);
            }
            return [Output.Save(source, "", "tiff", temp => pages.Write(temp, MagickFormat.Tiff))];
        }
        var outputs = new List<string>();
        string pad = new('0', count.ToString().Length);
        for (int p = 0; p < count; p++)
        {
            ct.ThrowIfCancellationRequested();
            using var page = PdfRenderer.Render(source, p, 300);
            var suffix = count > 1 ? " Page " + (p + 1).ToString(pad) : "";
            outputs.Add(Output.Save(source, suffix, target, temp => WriteImage(page, target, temp)));
            progress((p + 1.0) / count);
        }
        return outputs;
    }

    /// <summary>
    /// The PDF's text laid out as a table: each line becomes a row, and words line up in columns by
    /// where they start on the page (so real tables come out as real tables).
    /// </summary>
    static List<List<string>> PdfTable(string source)
    {
        using var doc = UglyToad.PdfPig.PdfDocument.Open(source);
        var rows = new List<List<string>>();
        foreach (var page in doc.GetPages())
        {
            var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            if (words.Count == 0) continue;
            double height = words.Select(w => w.BoundingBox.Height).OrderBy(h => h).ElementAt(words.Count / 2);
            // Lines: words whose baselines sit within half a line of each other, top to bottom.
            var lines = new List<List<UglyToad.PdfPig.Content.Word>>();
            foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
            {
                var line = lines.LastOrDefault();
                if (line != null && Math.Abs(line[0].BoundingBox.Bottom - w.BoundingBox.Bottom) <= height * 0.5) line.Add(w);
                else lines.Add([w]);
            }
            // Cells: a gap wider than about two letters starts a new cell.
            var cellLines = lines.Select(line =>
            {
                var cells = new List<(double Left, string Text)>();
                UglyToad.PdfPig.Content.Word? previous = null;
                foreach (var w in line.OrderBy(w => w.BoundingBox.Left))
                {
                    double letter = previous == null ? 0 : previous.BoundingBox.Width / Math.Max(1, previous.Text.Length);
                    if (previous != null && w.BoundingBox.Left - previous.BoundingBox.Right <= Math.Max(letter * 2.2, height * 0.6))
                        cells[^1] = (cells[^1].Left, cells[^1].Text + " " + w.Text);
                    else cells.Add((w.BoundingBox.Left, w.Text));
                    previous = w;
                }
                return cells;
            }).ToList();
            // Columns: cell starts that line up (within ~a letter) share a column.
            var anchors = new List<double>();
            foreach (var left in cellLines.SelectMany(c => c.Select(x => x.Left)).OrderBy(x => x))
                if (anchors.Count == 0 || left - anchors[^1] > height * 1.2) anchors.Add(left);
            foreach (var cells in cellLines)
            {
                var row = new List<string>();
                foreach (var (left, text) in cells)
                {
                    int column = anchors.FindLastIndex(a => a <= left + height * 1.2);
                    column = Math.Max(column, row.Count == 0 ? 0 : row.Count);
                    while (row.Count < column) row.Add("");
                    if (row.Count == column) row.Add(text);
                    else row[^1] += " " + text;
                }
                rows.Add(row);
            }
            rows.Add([]);
        }
        while (rows.Count > 0 && rows[^1].Count == 0) rows.RemoveAt(rows.Count - 1);
        if (rows.Count == 0) throw new IOException("This PDF has no selectable text (it may be a scan).");
        return rows;
    }

    /// <summary>A PDF's structure: through Word when it's installed (most faithful), otherwise the built-in reader.</summary>
    static Doc PdfStructure(string source, Action<double> progress, CancellationToken ct)
    {
        if (!MsOffice.Word) return PdfDocReader.Read(source, progress, ct);
        var docx = Path.Combine(Output.WorkFolder, Guid.NewGuid().ToString("N") + ".docx");
        try
        {
            WithWord(source, progress, temp => OfficeEngine.Convert(source, "docx", temp, ct), null, docx);
            var doc = DocxReader.Read(docx);
            doc.Title = Path.GetFileNameWithoutExtension(source);
            return doc;
        }
        finally { Output.TryDelete(docx); }
    }

    /// <summary>
    /// Runs a Word conversion of a PDF with the progress bar moving on an estimate (Word takes about a
    /// second a page). Saves beside the source as <paramref name="ext"/>, or into <paramref name="into"/>.
    /// </summary>
    static string WithWord(string source, Action<double> progress, Action<string> convert, string? ext, string? into = null)
    {
        int pages = 1;
        try { pages = PdfRenderer.PageCount(source); } catch { }
        double expected = 6 + pages * 1.1;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var pulse = new Timer(_ => progress(0.9 * (1 - Math.Exp(-clock.Elapsed.TotalSeconds / expected))), null, 400, 400);
        if (into != null)
        {
            convert(into);
            return into;
        }
        return Output.Save(source, "", ext!, convert);
    }

    // ---------- Office (Microsoft Office or LibreOffice, plus built-in readers) ----------

    static List<string> Office(string source, string ext, string target, Action<double> progress, CancellationToken ct)
    {
        var family = Formats.OfficeFamily(ext);
        if (target is "png" or "jpg" && family != "slides")
        {
            // Pages as pictures: Office makes a PDF, then each page is drawn at print quality.
            var pdf = OfficeEngine.ToTemp(source, "pdf", ct);
            try
            {
                int count = PdfRenderer.PageCount(pdf);
                string pad = new('0', Math.Max(2, count.ToString().Length));
                var outputs = new List<string>();
                for (int p = 0; p < count; p++)
                {
                    ct.ThrowIfCancellationRequested();
                    using var page = PdfRenderer.Render(pdf, p, 200);
                    var suffix = count > 1 ? " Page " + (p + 1).ToString(pad) : "";
                    outputs.Add(Output.Save(source, suffix, target, temp => WriteImage(page, target, temp)));
                    progress((p + 1.0) / count);
                }
                return outputs;
            }
            finally { Output.TryDelete(pdf); }
        }
        if (family == "word" && target is "txt" or "md" or "html" or "epub")
        {
            // Read the Word file itself (other Word formats go through Word first): headings, lists, tables and pictures come along.
            var docx = ext == "docx" ? source : OfficeEngine.ToTemp(source, "docx", ct);
            try
            {
                var doc = DocxReader.Read(docx);
                doc.Title = Path.GetFileNameWithoutExtension(source);
                return [Documents.Save(source, doc, target)];
            }
            finally { if (docx != source) Output.TryDelete(docx); }
        }
        if (family == "sheet" && target is "csv" or "tsv" or "json" or "html" or "md")
        {
            var xlsx = ext == "xlsx" ? source : OfficeEngine.ToTemp(source, "xlsx", ct);
            try { return [Tabular.Save(source, Tabular.ReadXlsx(xlsx), target, ct)]; }
            finally { if (xlsx != source) Output.TryDelete(xlsx); }
        }
        if (family == "slides" && target is "png" or "jpg")
            return OfficeEngine.SlidesToImages(source, target, progress, ct);
        // Office doesn't report progress, and a big workbook can take a minute or more: keep the bar
        // moving with an estimate from the file size so it never looks stuck.
        double mb = new FileInfo(source).Length / 1048576.0;
        double expected = family == "sheet" ? 8 + 45 * mb : 6 + 12 * mb;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var pulse = new Timer(_ => progress(0.9 * (1 - Math.Exp(-clock.Elapsed.TotalSeconds / expected))), null, 400, 400);
        return [Output.Save(source, "", target, temp => OfficeEngine.Convert(source, target, temp, ct))];
    }

    // ---------- Subtitles ----------

    static string Subtitles(string source, string ext, string target, CancellationToken ct)
    {
        if (ext != "ass" && target != "ass")
            return Output.SaveBytes(source, "", target, Encoding.UTF8.GetBytes(TextDocuments.ConvertSubtitles(File.ReadAllText(source), target)));
        if (target != "txt") return Media.Encode(source, "", target, [], 0, _ => { }, ct);
        // ASS → plain text: let ffmpeg read the styling, then strip the timings.
        var srt = Path.Combine(Output.WorkFolder, Guid.NewGuid().ToString("N") + ".srt");
        try
        {
            Processes.Run(Formats.Ffmpeg!, ["-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", source, srt], ct, TimeSpan.FromMinutes(1));
            return Output.SaveBytes(source, "", "txt", Encoding.UTF8.GetBytes(TextDocuments.ConvertSubtitles(File.ReadAllText(srt), "txt")));
        }
        finally { Output.TryDelete(srt); }
    }
}

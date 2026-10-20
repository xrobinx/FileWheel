using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace FileWheel.Core;

public static class PdfTools
{
    static PdfDocument OpenImport(string path) => PdfReader.Open(path, PdfDocumentOpenMode.Import);

    public static string Rotate(string source, int degrees, ISet<int>? pages = null)
    {
        using var input = OpenImport(source);
        using var output = new PdfDocument();
        for (int i = 0; i < input.PageCount; i++)
        {
            var page = output.AddPage(input.Pages[i]);
            if (pages == null || pages.Contains(i + 1)) page.Rotate = ((page.Rotate + degrees) % 360 + 360) % 360;
        }
        return Converter.SavePdf(output, source, " Rotated");
    }

    /// <summary>"Organize": the pages in a new order, each with its own extra turn; pages left out are dropped.</summary>
    public static string Organize(string source, IReadOnlyList<(int Page, int Turn)> pages)
    {
        if (pages.Count == 0) throw new IOException("Keep at least one page.");
        using var input = OpenImport(source);
        using var output = new PdfDocument();
        foreach (var (index, turn) in pages)
        {
            var page = output.AddPage(input.Pages[index]);
            if (turn != 0) page.Rotate = ((page.Rotate + turn) % 360 + 360) % 360;
        }
        return Converter.SavePdf(output, source, " Organized");
    }

    public static List<string> Split(string source, Action<double> progress, CancellationToken ct)
    {
        using var input = OpenImport(source);
        var outputs = new List<string>();
        string pad = new('0', input.PageCount.ToString().Length);
        for (int i = 0; i < input.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var one = new PdfDocument();
            one.AddPage(input.Pages[i]);
            outputs.Add(Converter.SavePdf(one, source, " Page " + (i + 1).ToString(pad)));
            progress((i + 1.0) / input.PageCount);
        }
        return outputs;
    }

    /// <summary>Parses "1-3, 5, 8-" into 1-based page numbers within <paramref name="count"/>.</summary>
    public static List<int> ParseRange(string text, int count)
    {
        var pages = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split('-', StringSplitOptions.TrimEntries);
            if (bits.Length == 1 && int.TryParse(bits[0], out var single)) { if (single >= 1 && single <= count) pages.Add(single); continue; }
            if (bits.Length != 2) throw new FormatException($"“{part}” isn't a page range.");
            int a = bits[0].Length == 0 ? 1 : int.Parse(bits[0]);
            int b = bits[1].Length == 0 ? count : int.Parse(bits[1]);
            if (a > b) (a, b) = (b, a);
            for (int p = Math.Max(1, a); p <= Math.Min(count, b); p++) pages.Add(p);
        }
        return pages.Distinct().ToList();
    }

    public static string Extract(string source, IReadOnlyList<int> pages, string label)
    {
        using var input = OpenImport(source);
        using var output = new PdfDocument();
        foreach (var p in pages) output.AddPage(input.Pages[p - 1]);
        return Converter.SavePdf(output, source, " Pages " + label);
    }

    public static string Merge(IReadOnlyList<string> files, Action<double> progress, CancellationToken ct)
    {
        using var output = new PdfDocument();
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var input = OpenImport(files[i]);
            foreach (var page in input.Pages) output.AddPage(page);
            progress((i + 1.0) / files.Count);
        }
        return Converter.SavePdf(output, files[0], " Merged");
    }

    /// <summary>Rebuilds each page as a JPEG at <paramref name="dpi"/>. Best for scans and image-heavy PDFs.</summary>
    public static string Compress(string source, int dpi, int quality, Action<double> progress, CancellationToken ct)
    {
        int count = PdfRenderer.PageCount(source);
        using var output = new PdfDocument();
        for (int i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            using var page = PdfRenderer.Render(source, i, dpi);
            page.Quality = (uint)quality;
            Converter.AddImagePage(output, page.ToByteArray(MagickFormat.Jpeg), page);
            progress((i + 1.0) / count);
        }
        return Converter.SavePdf(output, source, " Compressed");
    }

    public static string StripMetadata(string source)
    {
        using var input = OpenImport(source);
        using var output = new PdfDocument();
        foreach (var page in input.Pages) output.AddPage(page);
        output.Info.Title = output.Info.Author = output.Info.Subject = output.Info.Keywords = output.Info.Creator = "";
        return Converter.SavePdf(output, source, " Clean");
    }
}

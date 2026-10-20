using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;

namespace FileWheel.Core;

/// <summary>Reads a .docx into a <see cref="Doc"/>: headings, emphasis, lists, tables and pictures.</summary>
public static class DocxReader
{
    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    static readonly XNamespace V = "urn:schemas-microsoft-com:vml";
    static readonly XNamespace Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static Doc Read(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XDocument? Load(string name) => zip.GetEntry(name) is { } e ? XDocument.Load(e.Open()) : null;
        var body = Load("word/document.xml")?.Root?.Element(W + "body") ?? throw new IOException("This isn't a Word document.");

        var targets = Load("word/_rels/document.xml.rels")?.Root?.Elements(Pkg + "Relationship")
            .ToDictionary(e => (string)e.Attribute("Id")!, e => (string)e.Attribute("Target")!) ?? [];
        var headingLevels = HeadingStyles(Load("word/styles.xml"));
        var bulletLists = BulletNumbering(Load("word/numbering.xml"));

        byte[]? Media(string? id)
        {
            if (id == null || !targets.TryGetValue(id, out var target)) return null;
            var name = target.StartsWith('/') ? target.TrimStart('/') : "word/" + target;
            name = string.Join('/', name.Split('/').Aggregate(new List<string>(), (l, p) => { if (p == "..") { if (l.Count > 0) l.RemoveAt(l.Count - 1); } else l.Add(p); return l; }));
            if (zip.GetEntry(name) is not { } entry) return null;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }

        var doc = new Doc { Title = Path.GetFileNameWithoutExtension(path) };
        void Blocks(XElement parent)
        {
            foreach (var e in parent.Elements())
            {
                if (e.Name == W + "p") Paragraph(e);
                else if (e.Name == W + "tbl") doc.Blocks.Add(new TableBlock(e.Elements(W + "tr")
                    .Select(tr => tr.Elements(W + "tc").Select(tc => string.Join("\n", tc.Descendants(W + "p").Select(p => Doc.PlainText(Runs(p))).Where(t => t.Length > 0))).ToList())
                    .Where(r => r.Count > 0).ToList()));
                else if (e.Name == W + "sdt") { if (e.Element(W + "sdtContent") is { } content) Blocks(content); }
            }
        }

        void Paragraph(XElement p)
        {
            var props = p.Element(W + "pPr");
            var style = (string?)props?.Element(W + "pStyle")?.Attribute(W + "val") ?? "";
            int level = headingLevels.TryGetValue(style, out var l) ? l : 0;
            if (props?.Element(W + "outlineLvl") is { } outline && int.TryParse((string?)outline.Attribute(W + "val"), out var o) && o < 6) level = o + 1;
            string? list = null;
            if (props?.Element(W + "numPr") is { } num)
            {
                var id = (string?)num.Element(W + "numId")?.Attribute(W + "val");
                var ilvl = (string?)num.Element(W + "ilvl")?.Attribute(W + "val") ?? "0";
                if (id != null && id != "0") list = bulletLists.Contains(id + ":" + ilvl) ? "bullet" : "number";
            }

            // Text and pictures in order: a picture splits the paragraph around it.
            var runs = new List<Run>();
            bool pageBreak = false;
            void FlushText()
            {
                if (runs.Count == 0 || Doc.PlainText(runs).Trim().Length == 0) { runs.Clear(); return; }
                if (level > 0) doc.Blocks.Add(new Heading(level, [.. runs.Select(r => r with { Bold = false })]));
                else doc.Blocks.Add(new Paragraph([.. runs], list));
                runs.Clear();
            }
            foreach (var run in p.Descendants(W + "r"))
            {
                if (run.Ancestors(W + "txbxContent").Any()) continue;
                var rp = run.Element(W + "rPr");
                bool bold = On(rp?.Element(W + "b")), italic = On(rp?.Element(W + "i"));
                foreach (var e in run.Elements())
                {
                    if (e.Name == W + "t") runs.Add(new Run(e.Value, bold, italic));
                    else if (e.Name == W + "tab") runs.Add(new Run("\t", bold, italic));
                    else if (e.Name == W + "br" || e.Name == W + "cr")
                    {
                        if ((string?)e.Attribute(W + "type") == "page") pageBreak = true;
                        else runs.Add(new Run("\n", bold, italic));
                    }
                    else if (e.Name == W + "drawing" || e.Name == W + "pict" || e.Name == W + "object")
                    {
                        var id = (string?)e.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed")
                                 ?? (string?)e.Descendants(V + "imagedata").FirstOrDefault()?.Attribute(R + "id");
                        if (Media(id) is not { } bytes) continue;
                        var extent = e.Descendants(WP + "extent").FirstOrDefault();
                        int w = (int)((long?)extent?.Attribute("cx") ?? 0) / 9525, h = (int)((long?)extent?.Attribute("cy") ?? 0) / 9525;
                        if (Pictures.Make(bytes, w, h) is { } picture)
                        {
                            FlushText();
                            doc.Blocks.Add(picture);
                        }
                    }
                }
            }
            FlushText();
            if (pageBreak) doc.Blocks.Add(new PageBreak());
        }

        List<Run> Runs(XElement p) => p.Descendants(W + "r").Where(r => !r.Ancestors(W + "txbxContent").Any())
            .SelectMany(r => r.Elements().Select(e => e.Name == W + "t" ? e.Value : e.Name == W + "tab" ? "\t" : e.Name == W + "br" ? "\n" : ""))
            .Where(t => t.Length > 0).Select(t => new Run(t)).ToList();

        Blocks(body);
        return doc;
    }

    static bool On(XElement? flag) => flag != null && (string?)flag.Attribute(W + "val") is null or "1" or "true" or "on";

    /// <summary>Style id → heading level, from the style's name ("heading 2", "Title") or outline level.</summary>
    static Dictionary<string, int> HeadingStyles(XDocument? styles)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Title"] = 1 };
        for (int i = 1; i <= 6; i++) map["Heading" + i] = i;
        foreach (var s in styles?.Root?.Elements(W + "style") ?? [])
        {
            var id = (string?)s.Attribute(W + "styleId");
            if (id == null) continue;
            var name = ((string?)s.Element(W + "name")?.Attribute(W + "val") ?? "").ToLowerInvariant();
            if (name == "title") map[id] = 1;
            else if (name.StartsWith("heading ") && int.TryParse(name[8..], out var n) && n is >= 1 and <= 6) map[id] = n;
            else if (s.Element(W + "pPr")?.Element(W + "outlineLvl") is { } o && int.TryParse((string?)o.Attribute(W + "val"), out var lvl) && lvl < 6) map[id] = lvl + 1;
        }
        return map;
    }

    /// <summary>"numId:level" pairs whose list format is a bullet (everything else is numbered).</summary>
    static HashSet<string> BulletNumbering(XDocument? numbering)
    {
        var bullets = new HashSet<string>();
        if (numbering?.Root == null) return bullets;
        var abstracts = numbering.Root.Elements(W + "abstractNum").ToDictionary(
            a => (string)a.Attribute(W + "abstractNumId")!,
            a => a.Elements(W + "lvl").Where(l => (string?)l.Element(W + "numFmt")?.Attribute(W + "val") == "bullet").Select(l => (string)l.Attribute(W + "ilvl")!).ToHashSet());
        foreach (var num in numbering.Root.Elements(W + "num"))
        {
            var id = (string?)num.Attribute(W + "numId");
            var abs = (string?)num.Element(W + "abstractNumId")?.Attribute(W + "val");
            if (id == null || abs == null || !abstracts.TryGetValue(abs, out var levels)) continue;
            foreach (var level in levels) bullets.Add(id + ":" + level);
        }
        return bullets;
    }
}

/// <summary>Reads HTML/XHTML (web pages, EPUB chapters) into a <see cref="Doc"/>, pictures included.</summary>
public static class HtmlReader
{
    /// <summary>Adds the blocks of an XHTML body. <paramref name="loadImage"/> resolves an img src to bytes.</summary>
    public static void AddXhtml(Doc doc, XElement body, Func<string, byte[]?> loadImage)
    {
        foreach (var node in body.Nodes()) Block(doc, node, loadImage, null);
    }

    static readonly HashSet<string> BlockTags = ["p", "div", "section", "article", "blockquote", "header", "footer", "main", "aside", "figure", "figcaption", "nav", "li", "dd", "dt", "center", "body"];

    static void Block(Doc doc, XNode node, Func<string, byte[]?> loadImage, string? list)
    {
        if (node is XText text)
        {
            if (text.Value.Trim().Length > 0) doc.Blocks.Add(new Paragraph([new Run(Collapse(text.Value).Trim())], list));
            return;
        }
        if (node is not XElement e) return;
        var tag = e.Name.LocalName.ToLowerInvariant();
        switch (tag)
        {
            case "script" or "style" or "head" or "title":
                return;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                var heading = Inline(e, false, false).Where(r => r.Text.Length > 0).ToList();
                if (Doc.PlainText(heading).Trim().Length > 0) doc.Blocks.Add(new Heading(tag[1] - '0', Trim(heading)));
                foreach (var img in e.Descendants().Where(d => d.Name.LocalName is "img" or "image")) Image(doc, img, loadImage);
                return;
            case "img" or "image" or "svg":
                foreach (var img in tag == "svg" ? e.Descendants().Where(d => d.Name.LocalName == "image") : [e]) Image(doc, img, loadImage);
                return;
            case "ul" or "ol":
                foreach (var li in e.Elements().Where(c => c.Name.LocalName.Equals("li", StringComparison.OrdinalIgnoreCase)))
                    Block(doc, li, loadImage, tag == "ol" ? "number" : "bullet");
                return;
            case "table":
                var rows = e.Descendants().Where(d => d.Name.LocalName == "tr")
                    .Select(tr => tr.Elements().Where(c => c.Name.LocalName is "td" or "th").Select(c => Collapse(c.Value).Trim()).ToList())
                    .Where(r => r.Count > 0).ToList();
                if (rows.Count > 0) doc.Blocks.Add(new TableBlock(rows));
                return;
            case "pre":
                doc.Blocks.Add(new CodeBlock(e.Value.TrimEnd()));
                return;
            case "hr":
                return;
        }
        // A block holding other blocks: walk into it. Otherwise it's a paragraph of inline content.
        bool hasBlocks = e.Elements().Any(c => BlockTags.Contains(c.Name.LocalName.ToLowerInvariant()) || c.Name.LocalName is "ul" or "ol" or "table" or "pre"
                                                || c.Name.LocalName.Length == 2 && c.Name.LocalName[0] == 'h' && char.IsDigit(c.Name.LocalName[1]));
        if (hasBlocks)
        {
            foreach (var child in e.Nodes()) Block(doc, child, loadImage, tag == "li" ? list : null);
            return;
        }
        var runs = Trim(Inline(e, false, false).ToList());
        if (Doc.PlainText(runs).Trim().Length > 0) doc.Blocks.Add(new Paragraph(runs, list));
        foreach (var img in e.Descendants().Where(d => d.Name.LocalName is "img" or "image")) Image(doc, img, loadImage);
    }

    static void Image(Doc doc, XElement img, Func<string, byte[]?> loadImage)
    {
        var src = img.Attributes().FirstOrDefault(a => a.Name.LocalName is "src" or "href")?.Value;
        if (string.IsNullOrEmpty(src)) return;
        byte[]? bytes = null;
        if (src.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            var comma = src.IndexOf(',');
            try { if (comma > 0) bytes = Convert.FromBase64String(src[(comma + 1)..]); } catch { }
        }
        else bytes = loadImage(Uri.UnescapeDataString(src.Split('#', '?')[0]));
        if (bytes == null) return;
        int.TryParse(img.Attribute("width")?.Value, out var w);
        int.TryParse(img.Attribute("height")?.Value, out var h);
        if (Pictures.Make(bytes, w, w > 0 ? h : 0) is { } picture) doc.Blocks.Add(picture);
    }

    static IEnumerable<Run> Inline(XElement e, bool bold, bool italic)
    {
        foreach (var node in e.Nodes())
        {
            if (node is XText t) { yield return new Run(Collapse(t.Value), bold, italic); continue; }
            if (node is not XElement c) continue;
            var tag = c.Name.LocalName.ToLowerInvariant();
            if (tag is "br") { yield return new Run("\n", bold, italic); continue; }
            if (tag is "img" or "image" or "script" or "style") continue;
            bool b = bold || tag is "b" or "strong" || (c.Attribute("style")?.Value.Contains("bold") ?? false);
            bool i = italic || tag is "i" or "em";
            foreach (var r in Inline(c, b, i)) yield return r;
        }
    }

    static string Collapse(string s) => Regex.Replace(s, @"[ \t\r\n ]+", " ");

    static List<Run> Trim(List<Run> runs)
    {
        if (runs.Count == 0) return runs;
        runs[0] = runs[0] with { Text = runs[0].Text.TrimStart() };
        runs[^1] = runs[^1] with { Text = runs[^1].Text.TrimEnd() };
        return runs;
    }

    /// <summary>
    /// A web page. Pages that are valid XHTML keep their full structure and local pictures; others go
    /// through a forgiving tag-stripper that still keeps headings, lists and emphasis.
    /// </summary>
    public static Doc ReadFile(string path)
    {
        var html = File.ReadAllText(path);
        var doc = new Doc { Title = Path.GetFileNameWithoutExtension(path) };
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        byte[]? Local(string src)
        {
            try
            {
                if (src.Contains("://")) return null;
                var file = Path.GetFullPath(Path.Combine(folder, src.Replace('/', Path.DirectorySeparatorChar)));
                return File.Exists(file) ? File.ReadAllBytes(file) : null;
            }
            catch { return null; }
        }
        try
        {
            var cleaned = Regex.Replace(html, @"<!DOCTYPE[^>]*>", "", RegexOptions.IgnoreCase);
            var x = XDocument.Parse(cleaned);
            var body = x.Descendants().FirstOrDefault(d => d.Name.LocalName == "body") ?? x.Root!;
            AddXhtml(doc, body, Local);
            if (doc.HasText) return doc;
        }
        catch { /* not XHTML */ }
        return Doc.FromMarkdown(Epub.HtmlToMarkdown(html), doc.Title);
    }
}

/// <summary>
/// Reads a PDF into a <see cref="Doc"/> without Word: text blocks in reading order (joined into
/// paragraphs), headings by font size, bold blocks, pictures where they sit, and the repeating
/// header/footer lines dropped.
/// </summary>
public static partial class PdfDocReader
{
    public static Doc Read(string path, Action<double>? progress = null, CancellationToken ct = default)
    {
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(path);
        var doc = new Doc { Title = Path.GetFileNameWithoutExtension(path) };
        var pages = new List<List<(double Top, Block Block, string Key, double Size)>>();
        var sizes = new List<double>();
        int count = pdf.NumberOfPages, done = 0;

        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var items = new List<(double Top, Block Block, string Key, double Size)>();
            var words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            if (words.Count > 0)
            {
                var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                var ordered = UnsupervisedReadingOrderDetector.Instance.Get(blocks).OrderBy(b => b.ReadingOrder).ToList();
                foreach (var block in ordered)
                {
                    var letters = block.TextLines.SelectMany(l => l.Words).SelectMany(w => w.Letters).ToList();
                    double size = letters.Count == 0 ? 0 : letters.Average(l => l.PointSize);
                    bool bold = letters.Count > 0 && letters.Count(l => (l.FontName ?? "").Contains("Bold", StringComparison.OrdinalIgnoreCase)) > letters.Count * 0.6;
                    var text = Join(block.TextLines.Select(l => l.Text));
                    sizes.AddRange(letters.Select(l => l.PointSize));
                    items.Add((block.BoundingBox.Top, new Paragraph([new Run(text, bold)]), Normalize(text), size));
                }
            }
            foreach (var image in page.GetImages())
            {
                if (image.BoundingBox.Width < 18 || image.BoundingBox.Height < 18) continue;
                var bytes = image.TryGetPng(out var png) ? png : image.RawBytes.ToArray();
                var picture = Pictures.Make(bytes, (int)(image.BoundingBox.Width * 96 / 72), (int)(image.BoundingBox.Height * 96 / 72));
                if (picture != null) items.Add((image.BoundingBox.Top, picture, "", 0));
            }
            pages.Add(items);
            progress?.Invoke(++done / (double)count * 0.8);
        }

        // Lines that repeat at the top or bottom of most pages are running headers and footers.
        var repeated = new HashSet<string>();
        if (pages.Count >= 3)
        {
            var edges = pages.SelectMany(p => p.Where(i => i.Key.Length > 0).Take(1).Concat(p.Where(i => i.Key.Length > 0).TakeLast(1)).Select(i => i.Key).Distinct());
            repeated = edges.GroupBy(k => k).Where(g => g.Count() >= Math.Max(2, pages.Count * 0.4)).Select(g => g.Key).ToHashSet();
        }

        double body = sizes.Count == 0 ? 11 : sizes.OrderBy(s => s).ElementAt(sizes.Count / 2);
        for (int p = 0; p < pages.Count; p++)
        {
            // Text flows on across pages (a page break per original page leaves half-empty pages once reflowed).
            // Text in reading order; each picture goes before the first text that sits lower on the page.
            var text = pages[p].Where(i => i.Block is Paragraph).ToList();
            var pictures = pages[p].Where(i => i.Block is Picture).OrderByDescending(i => i.Top).ToList();
            foreach (var item in text)
            {
                while (pictures.Count > 0 && pictures[0].Top >= item.Top)
                {
                    doc.Blocks.Add(pictures[0].Block);
                    pictures.RemoveAt(0);
                }
                if (repeated.Contains(item.Key)) continue;
                var para = (Paragraph)item.Block;
                var line = Doc.PlainText(para.Runs);
                double size = item.Size;
                if (size >= body * 1.18 && line.Length <= 140) doc.Blocks.Add(new Heading(size >= body * 1.6 ? 1 : size >= body * 1.35 ? 2 : 3, [new Run(line)]));
                else if (BulletText().Match(line) is { Success: true } b) doc.Blocks.Add(new Paragraph([new Run(b.Groups[1].Value, para.Runs[0].Bold)], "bullet"));
                else doc.Blocks.Add(para);
            }
            foreach (var rest in pictures) doc.Blocks.Add(rest.Block);
        }
        while (doc.Blocks.Count > 0 && doc.Blocks[^1] is PageBreak) doc.Blocks.RemoveAt(doc.Blocks.Count - 1);
        return doc;
    }

    /// <summary>Lines of a block as one paragraph, re-joining words hyphenated across lines.</summary>
    static string Join(IEnumerable<string> lines)
    {
        var text = "";
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (text.Length == 0) text = line;
            else if (text.EndsWith('-') && line.Length > 0 && char.IsLower(line[0])) text = text[..^1] + line;
            else text += " " + line;
        }
        return text;
    }

    static string Normalize(string text) => Regex.Replace(text.ToLowerInvariant(), @"\d+", "#").Trim();

    [GeneratedRegexAttribute(@"^[•●▪◦‣\-–]\s+(.*)$")]
    private static partial Regex BulletText();
}

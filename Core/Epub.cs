using System.IO.Compression;
using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FileWheel.Core;

/// <summary>E-books: reads an EPUB (text, structure and pictures) into a <see cref="Doc"/>, and writes a Doc as an EPUB 3 with an EPUB 2 table of contents too.</summary>
public static partial class Epub
{
    // ---------- Reading ----------

    /// <summary>The book in reading order, chapter by chapter, with its pictures.</summary>
    public static Doc ReadDoc(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XDocument Load(string name) => XDocument.Load((zip.GetEntry(name) ?? throw new IOException("This EPUB is missing " + name + ".")).Open());

        var rootfile = Load("META-INF/container.xml").Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value
            ?? throw new IOException("This EPUB has no package file.");
        var opf = Load(rootfile);
        var baseDir = rootfile.Contains('/') ? rootfile[..(rootfile.LastIndexOf('/') + 1)] : "";
        var manifest = opf.Descendants().Where(e => e.Name.LocalName == "item")
            .Where(e => e.Attribute("id") != null && e.Attribute("href") != null)
            .GroupBy(e => e.Attribute("id")!.Value).ToDictionary(g => g.Key, g => g.First().Attribute("href")!.Value);
        var spine = opf.Descendants().Where(e => e.Name.LocalName == "itemref").Select(e => e.Attribute("idref")?.Value).OfType<string>().ToList();

        var doc = new Doc { Title = opf.Descendants().FirstOrDefault(e => e.Name.LocalName == "title")?.Value.Trim() ?? Path.GetFileNameWithoutExtension(path) };
        foreach (var id in spine)
        {
            if (!manifest.TryGetValue(id, out var href)) continue;
            var chapterPath = Resolve(baseDir, Uri.UnescapeDataString(href.Split('#')[0]));
            if (zip.GetEntry(chapterPath) is not { } entry) continue;
            string xhtml;
            using (var reader = new StreamReader(entry.Open())) xhtml = reader.ReadToEnd();
            var chapterDir = chapterPath.Contains('/') ? chapterPath[..(chapterPath.LastIndexOf('/') + 1)] : "";
            byte[]? Image(string src)
            {
                if (zip.GetEntry(Resolve(chapterDir, src)) is not { } img) return null;
                using var s = img.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            try
            {
                var x = XDocument.Parse(Regex.Replace(xhtml, @"<!DOCTYPE[^>]*>", "", RegexOptions.IgnoreCase));
                var body = x.Descendants().FirstOrDefault(d => d.Name.LocalName == "body");
                if (body != null) HtmlReader.AddXhtml(doc, body, Image);
            }
            catch
            {
                // Not well-formed: keep at least the text and structure.
                doc.Blocks.AddRange(Doc.FromMarkdown(HtmlToMarkdown(xhtml)).Blocks);
            }
        }
        if (!doc.HasText && !doc.Blocks.OfType<Picture>().Any()) throw new IOException("This EPUB has no readable text.");
        return doc;
    }

    static string Resolve(string baseDir, string href)
    {
        var parts = new List<string>();
        foreach (var part in (baseDir + href).Split('/'))
        {
            if (part == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (part.Length > 0 && part != ".") parts.Add(part);
        }
        return string.Join('/', parts);
    }

    /// <summary>A chapter's (X)HTML as Markdown — the forgiving path for pages that aren't well-formed.</summary>
    public static string HtmlToMarkdown(string html)
    {
        var s = Regex.Replace(html, @"<(head|script|style)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<h([1-6])[^>]*>(.*?)</h\1>", m => "\n\n" + new string('#', int.Parse(m.Groups[1].Value)) + " " + Regex.Replace(m.Groups[2].Value, "<[^>]+>", "").Trim() + "\n\n", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li[^>]*>", "\n- ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(p|div|blockquote|ul|ol|table|tr|section)>", "\n\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</?(b|strong)(\s[^>]*)?>", "**", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"[ \t ]+", " ");
        s = Regex.Replace(s, @" *\n *", "\n");
        s = Regex.Replace(s, @"\*\*\s*\*\*", "");
        return BlankLines().Replace(s, "\n\n").Trim();
    }

    // ---------- Writing ----------

    public static void Write(string markdown, string title, string path) => Write(Doc.FromMarkdown(markdown, title), path);

    /// <summary>Writes the document as an EPUB: one chapter per top-level heading, pictures as image files.</summary>
    public static void Write(Doc doc, string path)
    {
        var chapters = Chapters(doc);
        var id = "urn:uuid:" + Guid.NewGuid();
        var title = doc.Title.Length > 0 ? doc.Title : "Untitled";
        string X(string s) => SecurityElement.Escape(s) ?? "";

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        // The mimetype must come first, uncompressed.
        Add(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        Add(zip, "META-INF/container.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
            "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
        Add(zip, "OEBPS/style.css",
            "body{font-family:serif;line-height:1.55;margin:0 5%}h1,h2,h3,h4{font-family:sans-serif;line-height:1.25}pre{white-space:pre-wrap;font-size:.9em}" +
            "img{max-width:100%;height:auto}p.picture{text-align:center}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:3px 6px}");

        var manifest = new StringBuilder();
        var spine = new StringBuilder();
        var nav = new StringBuilder();
        var ncx = new StringBuilder();
        int pictures = 0;
        for (int i = 0; i < chapters.Count; i++)
        {
            var (heading, part) = chapters[i];
            var file = $"chapter{i + 1:000}.xhtml";
            var body = part.ToHtmlBody((pic, _) =>
            {
                var name = $"images/img{++pictures:0000}.{(pic.Ext == "jpg" ? "jpg" : "png")}";
                var entry = zip.CreateEntry("OEBPS/" + name, CompressionLevel.NoCompression);
                using (var s = entry.Open()) s.Write(pic.Data);
                manifest.Append($"<item id=\"img{pictures}\" href=\"{name}\" media-type=\"{pic.Mime}\"/>");
                return name;
            });
            Add(zip, "OEBPS/" + file,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" lang=\"en\">" +
                $"<head><meta charset=\"UTF-8\"/><title>{X(heading)}</title><link rel=\"stylesheet\" href=\"style.css\"/></head><body>\n{body}</body></html>");
            manifest.Append($"<item id=\"c{i + 1}\" href=\"{file}\" media-type=\"application/xhtml+xml\"/>");
            spine.Append($"<itemref idref=\"c{i + 1}\"/>");
            nav.Append($"<li><a href=\"{file}\">{X(heading)}</a></li>");
            ncx.Append($"<navPoint id=\"n{i + 1}\" playOrder=\"{i + 1}\"><navLabel><text>{X(heading)}</text></navLabel><content src=\"{file}\"/></navPoint>");
        }
        Add(zip, "OEBPS/nav.xhtml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" lang=\"en\">" +
            $"<head><meta charset=\"UTF-8\"/><title>{X(title)}</title></head><body><nav epub:type=\"toc\" id=\"toc\"><h1>Contents</h1><ol>{nav}</ol></nav></body></html>");
        Add(zip, "OEBPS/toc.ncx",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\">" +
            $"<head><meta name=\"dtb:uid\" content=\"{id}\"/></head><docTitle><text>{X(title)}</text></docTitle><navMap>{ncx}</navMap></ncx>");
        Add(zip, "OEBPS/content.opf",
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\">" +
            "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
            $"<dc:identifier id=\"bookid\">{id}</dc:identifier><dc:title>{X(title)}</dc:title><dc:language>en</dc:language>" +
            $"<meta property=\"dcterms:modified\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</meta></metadata>" +
            "<manifest><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>" +
            "<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/><item id=\"css\" href=\"style.css\" media-type=\"text/css\"/>" +
            $"{manifest}</manifest><spine toc=\"ncx\">{spine}</spine></package>");
    }

    /// <summary>Splits at level-1 headings (or level 2 when there's at most one level 1); long heading-less documents are cut into parts.</summary>
    static List<(string Heading, Doc Part)> Chapters(Doc doc)
    {
        int level = doc.Blocks.OfType<Heading>().Count(h => h.Level == 1) >= 2 ? 1 : doc.Blocks.OfType<Heading>().Count(h => h.Level == 2) >= 2 ? 2 : 0;
        var chapters = new List<(string, Doc)>();
        var current = new Doc();
        string name = "Start";
        void Flush()
        {
            if (current.HasText || current.Blocks.OfType<Picture>().Any()) chapters.Add((name, current));
            current = new Doc();
        }
        int count = 0;
        foreach (var block in doc.Blocks.Where(b => b is not PageBreak))
        {
            if (level > 0 && block is Heading h && h.Level <= level)
            {
                Flush();
                name = Doc.PlainText(h.Runs).Trim();
            }
            else if (level == 0 && count > 0 && count % 80 == 0)
            {
                Flush();
                name = $"Part {chapters.Count + 1}";
            }
            current.Blocks.Add(block);
            count++;
        }
        Flush();
        if (chapters.Count == 0) throw new IOException("There's no text to put in the book.");
        if (level == 0 && chapters.Count == 1) chapters[0] = (doc.Title.Length > 0 ? doc.Title : "Text", chapters[0].Item2);
        else if (level == 0) chapters[0] = ("Part 1", chapters[0].Item2);
        return chapters;
    }

    static void Add(ZipArchive zip, string name, string content, CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        using var s = entry.Open();
        s.Write(Encoding.UTF8.GetBytes(content));
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}

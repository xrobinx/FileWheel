using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace FileWheel.Core;

/// <summary>A run of text with its emphasis.</summary>
public sealed record Run(string Text, bool Bold = false, bool Italic = false);

public abstract record Block;
public sealed record Heading(int Level, List<Run> Runs) : Block;
/// <summary>A paragraph; <paramref name="List"/> is "bullet" or "number" for list items.</summary>
public sealed record Paragraph(List<Run> Runs, string? List = null) : Block;
/// <summary>A picture with its display size in pixels (96 per inch).</summary>
public sealed record Picture(byte[] Data, string Ext, int Width, int Height) : Block
{
    public string Mime => Ext switch { "jpg" or "jpeg" => "image/jpeg", "gif" => "image/gif", _ => "image/png" };
}
public sealed record TableBlock(List<List<string>> Rows) : Block;
public sealed record CodeBlock(string Text) : Block;
public sealed record PageBreak : Block;

/// <summary>
/// A simple structured document — headings, paragraphs with bold/italic, lists, tables, pictures —
/// that every reader (Word, PDF, EPUB, Markdown, HTML) fills and every writer turns into a file, so
/// pictures and structure survive a conversion instead of collapsing into plain text.
/// </summary>
public sealed partial class Doc
{
    public string Title { get; set; } = "";
    public List<Block> Blocks { get; } = [];

    public static string PlainText(IEnumerable<Run> runs) => string.Concat(runs.Select(r => r.Text));

    public bool HasText => Blocks.Any(b => b switch
    {
        Heading h => PlainText(h.Runs).Trim().Length > 0,
        Paragraph p => PlainText(p.Runs).Trim().Length > 0,
        TableBlock t => t.Rows.Count > 0,
        CodeBlock c => c.Text.Trim().Length > 0,
        _ => false,
    });

    // ---------- Markdown in ----------

    /// <summary>Reads Markdown: # headings, paragraphs, - and 1. lists, ``` code, | tables |, **bold**, *italic*, ![](images).</summary>
    public static Doc FromMarkdown(string md, string title = "")
    {
        var doc = new Doc { Title = title };
        var paragraph = new List<string>();
        var code = new StringBuilder();
        var table = new List<List<string>>();
        bool inCode = false;

        void Flush()
        {
            if (paragraph.Count > 0) { doc.Blocks.Add(new Paragraph(Inline(string.Join(" ", paragraph)))); paragraph.Clear(); }
            if (table.Count > 0) { doc.Blocks.Add(new TableBlock([.. table])); table.Clear(); }
        }

        foreach (var raw in md.Replace("\r", "").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```"))
            {
                Flush();
                if (inCode) { doc.Blocks.Add(new CodeBlock(code.ToString().TrimEnd('\n'))); code.Clear(); }
                inCode = !inCode;
                continue;
            }
            if (inCode) { code.Append(raw).Append('\n'); continue; }
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) { Flush(); continue; }
            if (HeadingLine().Match(line) is { Success: true } h)
            {
                Flush();
                doc.Blocks.Add(new Heading(h.Groups[1].Length, Inline(h.Groups[2].Value)));
                continue;
            }
            if (line.TrimStart().StartsWith('|') && line.TrimEnd().EndsWith('|'))
            {
                if (paragraph.Count > 0) { doc.Blocks.Add(new Paragraph(Inline(string.Join(" ", paragraph)))); paragraph.Clear(); }
                var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim().Replace("\\|", "|")).ToList();
                if (!cells.All(c => Regex.IsMatch(c, @"^:?-{2,}:?$"))) table.Add(cells);
                continue;
            }
            if (ImageLine().Match(line.Trim()) is { Success: true } img && DataUri(img.Groups[1].Value) is { } picture)
            {
                Flush();
                doc.Blocks.Add(picture);
                continue;
            }
            if (BulletLine().Match(line) is { Success: true } b)
            {
                Flush();
                doc.Blocks.Add(new Paragraph(Inline(b.Groups[1].Value), "bullet"));
                continue;
            }
            if (NumberLine().Match(line) is { Success: true } n)
            {
                Flush();
                doc.Blocks.Add(new Paragraph(Inline(n.Groups[1].Value), "number"));
                continue;
            }
            if (table.Count > 0) Flush();
            paragraph.Add(line.Trim());
        }
        if (inCode && code.Length > 0) doc.Blocks.Add(new CodeBlock(code.ToString().TrimEnd('\n')));
        Flush();
        return doc;
    }

    /// <summary>Plain text: each block of lines is a paragraph.</summary>
    public static Doc FromPlainText(string text, string title = "")
    {
        var doc = new Doc { Title = title };
        foreach (var block in Regex.Split(text.Replace("\r", ""), @"\n\s*\n"))
            foreach (var line in block.Split('\n').Where(l => l.Trim().Length > 0))
                doc.Blocks.Add(new Paragraph([new Run(line.TrimEnd())]));
        return doc;
    }

    /// <summary>**bold**, *italic*, `code` and [links](url) inside a line.</summary>
    static List<Run> Inline(string text)
    {
        var runs = new List<Run>();
        text = Regex.Replace(text, @"\[([^\]]+)\]\(([^)\s]+)\)", "$1");
        int i = 0;
        foreach (Match m in InlineMarks().Matches(text))
        {
            if (m.Index > i) runs.Add(new Run(text[i..m.Index]));
            if (m.Groups[1].Success) runs.Add(new Run(m.Groups[1].Value, Bold: true, Italic: true));
            else if (m.Groups[2].Success) runs.Add(new Run(m.Groups[2].Value, Bold: true));
            else if (m.Groups[3].Success) runs.Add(new Run(m.Groups[3].Value, Italic: true));
            else runs.Add(new Run(m.Groups[4].Value));
            i = m.Index + m.Length;
        }
        if (i < text.Length) runs.Add(new Run(text[i..]));
        return runs.Count == 0 ? [new Run("")] : runs;
    }

    static Picture? DataUri(string uri)
    {
        var m = Regex.Match(uri, @"^data:image/(png|jpe?g|gif|webp);base64,(.+)$");
        if (!m.Success) return null;
        try
        {
            var bytes = Convert.FromBase64String(m.Groups[2].Value);
            return Pictures.Make(bytes);
        }
        catch { return null; }
    }

    // ---------- Out ----------

    /// <summary>Markdown, with pictures embedded as data URIs so the file stays self-contained.</summary>
    public string ToMarkdown()
    {
        var sb = new StringBuilder();
        int number = 0;
        foreach (var block in Blocks)
        {
            if (block is not Paragraph { List: "number" }) number = 0;
            switch (block)
            {
                case Heading h: sb.Append(new string('#', Math.Clamp(h.Level, 1, 6))).Append(' ').Append(MdRuns(h.Runs).Replace("**", "")).Append("\n\n"); break;
                case Paragraph { List: "bullet" } p: sb.Append("- ").Append(MdRuns(p.Runs)).Append('\n'); break;
                case Paragraph { List: "number" } p: sb.Append(++number).Append(". ").Append(MdRuns(p.Runs)).Append('\n'); break;
                case Paragraph p: sb.Append(MdRuns(p.Runs)).Append("\n\n"); break;
                case Picture pic: sb.Append($"![](data:{pic.Mime};base64,{Convert.ToBase64String(pic.Data)})\n\n"); break;
                case TableBlock t: sb.Append(TextDocuments.RowsTo(t.Rows, "md", "")).Append('\n'); break;
                case CodeBlock c: sb.Append("```\n").Append(c.Text).Append("\n```\n\n"); break;
            }
        }
        return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim() + "\n";
    }

    static string MdRuns(IEnumerable<Run> runs) => string.Concat(runs.Select(r =>
        r.Text.Trim().Length == 0 ? r.Text :
        r.Bold && r.Italic ? Wrap(r.Text, "***") : r.Bold ? Wrap(r.Text, "**") : r.Italic ? Wrap(r.Text, "*") : r.Text));

    static string Wrap(string text, string mark)
    {
        // Keep spaces outside the marks so Markdown still reads them as emphasis.
        var lead = text[..(text.Length - text.TrimStart().Length)];
        var trail = text[text.TrimEnd().Length..];
        return lead + mark + text.Trim() + mark + trail;
    }

    public string ToPlainText()
    {
        var sb = new StringBuilder();
        int number = 0;
        foreach (var block in Blocks)
        {
            if (block is not Paragraph { List: "number" }) number = 0;
            switch (block)
            {
                case Heading h: sb.Append(PlainText(h.Runs)).Append("\n\n"); break;
                case Paragraph { List: "bullet" } p: sb.Append("• ").Append(PlainText(p.Runs)).Append('\n'); break;
                case Paragraph { List: "number" } p: sb.Append(++number).Append(". ").Append(PlainText(p.Runs)).Append('\n'); break;
                case Paragraph p: sb.Append(PlainText(p.Runs)).Append("\n\n"); break;
                case TableBlock t: sb.Append(string.Join("\n", t.Rows.Select(r => string.Join("\t", r)))).Append("\n\n"); break;
                case CodeBlock c: sb.Append(c.Text).Append("\n\n"); break;
            }
        }
        return Regex.Replace(sb.ToString(), @"\n{3,}", "\n\n").Trim() + "\n";
    }

    /// <summary>The body as (X)HTML; pictures come from <paramref name="imageSource"/> (a data URI by default).</summary>
    public string ToHtmlBody(Func<Picture, int, string>? imageSource = null)
    {
        imageSource ??= (p, _) => $"data:{p.Mime};base64,{Convert.ToBase64String(p.Data)}";
        var sb = new StringBuilder();
        string? openList = null;
        int pictureIndex = 0;
        void CloseList()
        {
            if (openList != null) sb.Append(openList == "number" ? "</ol>\n" : "</ul>\n");
            openList = null;
        }
        foreach (var block in Blocks)
        {
            if (block is Paragraph { List: { } kind })
            {
                if (openList != kind) { CloseList(); sb.Append(kind == "number" ? "<ol>\n" : "<ul>\n"); openList = kind; }
                sb.Append("<li>").Append(HtmlRuns(((Paragraph)block).Runs)).Append("</li>\n");
                continue;
            }
            CloseList();
            switch (block)
            {
                case Heading h: int l = Math.Clamp(h.Level, 1, 6); sb.Append($"<h{l}>{HtmlRuns(h.Runs)}</h{l}>\n"); break;
                case Paragraph p: sb.Append("<p>").Append(HtmlRuns(p.Runs)).Append("</p>\n"); break;
                case Picture pic:
                    sb.Append($"<p class=\"picture\"><img src=\"{imageSource(pic, pictureIndex++)}\" width=\"{pic.Width}\" height=\"{pic.Height}\" alt=\"\"/></p>\n");
                    break;
                case TableBlock t:
                    sb.Append("<table>\n");
                    for (int r = 0; r < t.Rows.Count; r++)
                    {
                        var tag = r == 0 ? "th" : "td";
                        sb.Append("<tr>").Append(string.Concat(t.Rows[r].Select(c => $"<{tag}>{Esc(c)}</{tag}>"))).Append("</tr>\n");
                    }
                    sb.Append("</table>\n");
                    break;
                case CodeBlock c: sb.Append("<pre><code>").Append(Esc(c.Text)).Append("</code></pre>\n"); break;
                case PageBreak: sb.Append("<hr/>\n"); break;
            }
        }
        CloseList();
        return sb.ToString();
    }

    public const string Css = "body{font:16px/1.6 system-ui,Segoe UI,sans-serif;max-width:46rem;margin:3rem auto;padding:0 1rem;color:#222}" +
        "img{max-width:100%;height:auto}table{border-collapse:collapse;margin:1rem 0}td,th{border:1px solid #ddd;padding:5px 9px;text-align:left;vertical-align:top}th{background:#f6f2ee}" +
        "pre{background:#f5f2ee;padding:1rem;border-radius:8px;overflow:auto}code{font-family:Cascadia Code,Consolas,monospace}hr{border:0;border-top:1px dashed #ccc;margin:2rem 0}";

    /// <summary>A complete, self-contained web page (pictures embedded).</summary>
    public string ToHtml() =>
        $"<!doctype html>\n<html><head><meta charset=\"utf-8\"><title>{Esc(Title)}</title><style>{Css}</style></head>\n<body>\n{ToHtmlBody()}</body></html>\n";

    static string HtmlRuns(IEnumerable<Run> runs) => string.Concat(runs.Select(r =>
    {
        var t = Esc(r.Text).Replace("\n", "<br/>");
        if (r.Bold) t = "<strong>" + t + "</strong>";
        if (r.Italic) t = "<em>" + t + "</em>";
        return t;
    }));

    static string Esc(string s)
    {
        var clean = new StringBuilder(s.Length);
        foreach (var c in s) if (c is '\t' or '\n' or '\r' || c >= 0x20 && c != 0xFFFE && c != 0xFFFF) clean.Append(c);
        return WebUtility.HtmlEncode(clean.ToString());
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex HeadingLine();
    [GeneratedRegex(@"^\s*[-*+•]\s+(.*)$")] private static partial Regex BulletLine();
    [GeneratedRegex(@"^\s*\d{1,3}[.)]\s+(.*)$")] private static partial Regex NumberLine();
    [GeneratedRegex(@"^!\[[^\]]*\]\(([^)]+)\)$")] private static partial Regex ImageLine();
    [GeneratedRegex(@"\*\*\*(.+?)\*\*\*|\*\*(.+?)\*\*|(?<![\*\w])\*(?!\s)(.+?)(?<!\s)\*(?![\*\w])|`([^`]+)`")] private static partial Regex InlineMarks();
}

/// <summary>Turns image bytes into a <see cref="Picture"/> any writer can use (PNG or JPEG, with its size).</summary>
public static class Pictures
{
    public static Picture? Make(byte[] bytes, int displayWidth = 0, int displayHeight = 0)
    {
        try
        {
            using var image = new ImageMagick.MagickImage(bytes);
            if (image.Width < 2 || image.Height < 2) return null;
            bool jpeg = image.Format is ImageMagick.MagickFormat.Jpeg or ImageMagick.MagickFormat.Jpg;
            bool png = image.Format == ImageMagick.MagickFormat.Png;
            byte[] data = jpeg || png ? bytes : image.ToByteArray(ImageMagick.MagickFormat.Png);
            int w = displayWidth > 0 ? displayWidth : (int)image.Width, h = displayHeight > 0 ? displayHeight : (int)image.Height;
            // Never wider than a page body (6.5 in at 96 dpi), keeping the shape.
            if (w > 624) { h = (int)Math.Round(h * 624.0 / w); w = 624; }
            return new Picture(data, jpeg ? "jpg" : "png", Math.Max(1, w), Math.Max(1, h));
        }
        catch { return null; }
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace FileWheel.Core;

/// <summary>Plain text, Markdown, HTML, delimited data and subtitle conversions.</summary>
public static partial class TextDocuments
{
    static TextDocuments()
    {
        GlobalFontSettings.FontResolver ??= new WindowsFonts();
    }

    /// <summary>Serves Segoe UI (falling back to Arial), or Consolas for code, straight from the Windows fonts folder.</summary>
    internal sealed class WindowsFonts : IFontResolver
    {
        static readonly string Folder = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            bool mono = familyName.Contains("Consolas", StringComparison.OrdinalIgnoreCase);
            string[] candidates = mono ? (bold, italic) switch
            {
                (true, true) => ["consolaz.ttf", "consola.ttf"],
                (true, false) => ["consolab.ttf", "consola.ttf"],
                (false, true) => ["consolai.ttf", "consola.ttf"],
                _ => ["consola.ttf", "cour.ttf"],
            } : (bold, italic) switch
            {
                (true, true) => ["segoeuiz.ttf", "arialbi.ttf"],
                (true, false) => ["segoeuib.ttf", "arialbd.ttf"],
                (false, true) => ["segoeuii.ttf", "ariali.ttf"],
                _ => ["segoeui.ttf", "arial.ttf"],
            };
            var file = candidates.FirstOrDefault(f => File.Exists(Path.Combine(Folder, f)));
            return file == null ? null : new FontResolverInfo(file);
        }

        public byte[]? GetFont(string faceName) => File.ReadAllBytes(Path.Combine(Folder, faceName));
    }

    public static string Convert(string source, string ext, string target)
    {
        var text = File.ReadAllText(source);
        if (ext == "html") text = HtmlToText(text);
        return FromText(source, text, markdown: ext == "md", target);
    }

    /// <summary>Saves <paramref name="text"/> (plain or Markdown) beside <paramref name="source"/> as <paramref name="target"/>.</summary>
    public static string FromText(string source, string text, bool markdown, string target)
    {
        var title = Path.GetFileNameWithoutExtension(source);
        return target switch
        {
            "pdf" => Output.Save(source, "", "pdf", temp => WritePdf(text, markdown, temp)),
            "docx" => Output.Save(source, "", "docx", temp => WriteDocx(text, markdown, temp)),
            "html" => Output.SaveBytes(source, "", "html", Encoding.UTF8.GetBytes(markdown ? MarkdownToHtml(text, title) : PlainToHtml(text, title))),
            "md" or "txt" or "json" or "xml" => Output.SaveBytes(source, "", target, Encoding.UTF8.GetBytes(target == "txt" && markdown ? MarkdownToPlain(text) : text)),
            "rtf" => Output.SaveBytes(source, "", "rtf", Encoding.ASCII.GetBytes(ToRtf(text, markdown))),
            "epub" => Output.Save(source, "", "epub", temp => Epub.Write(markdown ? text : PlainToMarkdown(text), title, temp)),
            _ => throw new IOException("Unsupported text conversion."),
        };
    }

    /// <summary>Markdown without its marks, for plain text.</summary>
    public static string MarkdownToPlain(string md) =>
        string.Join("\n", md.Replace("\r", "").Split('\n').Select(l => HeadingRegex().Match(l) is { Success: true } h ? h.Groups[2].Value : StripInlineMarkdown(l)));

    /// <summary>Plain text for a Markdown-based writer: each line kept as its own paragraph.</summary>
    public static string PlainToMarkdown(string text) =>
        string.Join("\n\n", text.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0));

    /// <summary>A simple Rich Text document (Segoe UI, Markdown headings in bold) that Word and WordPad open.</summary>
    public static string ToRtf(string text, bool markdown)
    {
        var sb = new StringBuilder(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss Segoe UI;}}\viewkind4\uc1\pard\sa120\f0\fs22 ");
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (markdown && HeadingRegex().Match(raw) is { Success: true } h)
                sb.Append($@"\b\fs{(20 - h.Groups[1].Length * 2) * 2} ").Append(RtfEscape(h.Groups[2].Value)).Append(@"\b0\fs22\par ");
            else
                sb.Append(RtfEscape(markdown ? StripInlineMarkdown(raw) : raw)).Append(@"\par ");
        }
        return sb.Append('}').ToString();
    }

    static string RtfEscape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '\\' or '{' or '}') sb.Append('\\').Append(c);
            else if (c == '\t') sb.Append(@"\tab ");
            else if (c > 127) sb.Append(@"\u").Append((short)c).Append('?');
            else if (c >= 32) sb.Append(c);
        }
        return sb.ToString();
    }

    static void WriteDocx(string text, bool markdown, string path)
    {
        var docx = new DocxWriter();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw;
            if (markdown && HeadingRegex().Match(line) is { Success: true } h)
                docx.Paragraph(h.Groups[2].Value, bold: true, points: 22 - h.Groups[1].Length * 2);
            else
                docx.Paragraph(markdown ? StripInlineMarkdown(line) : line);
        }
        docx.Save(path);
    }

    static void WritePdf(string text, bool markdown, string path)
    {
        using var doc = new PdfDocument();
        var body = new XFont("Segoe UI", 10.5);
        double margin = 56, lineGap = 4;
        PdfPage? page = null;
        XGraphics? gfx = null;
        double y = 0, width = 0, bottom = 0;

        void NewPage()
        {
            gfx?.Dispose();
            page = doc.AddPage();
            page.Width = XUnit.FromPoint(612);
            page.Height = XUnit.FromPoint(792);
            gfx = XGraphics.FromPdfPage(page);
            y = margin;
            width = page.Width.Point - margin * 2;
            bottom = page.Height.Point - margin;
        }

        NewPage();
        foreach (var raw in text.Replace("\r", "").Replace("\t", "    ").Split('\n'))
        {
            var font = body;
            var line = raw;
            if (markdown && HeadingRegex().Match(raw) is { Success: true } h)
            {
                font = new XFont("Segoe UI", 20 - h.Groups[1].Length * 2, XFontStyleEx.Bold);
                line = h.Groups[2].Value;
            }
            else if (markdown) line = StripInlineMarkdown(raw);

            double height = font.GetHeight() + lineGap;
            foreach (var wrapped in Wrap(gfx!, line, font, width))
            {
                if (y + height > bottom) NewPage();
                gfx!.DrawString(wrapped, font, XBrushes.Black, new XRect(margin, y, width, height), XStringFormats.TopLeft);
                y += height;
            }
        }
        gfx?.Dispose();
        doc.Save(path);
    }

    static IEnumerable<string> Wrap(XGraphics gfx, string line, XFont font, double width)
    {
        if (line.Length == 0) { yield return ""; yield break; }
        var current = new StringBuilder();
        foreach (var word in Regex.Split(line, @"(?<=\s)"))
        {
            var candidate = current + word;
            if (current.Length > 0 && gfx.MeasureString(candidate.TrimEnd(), font).Width > width)
            {
                yield return current.ToString().TrimEnd();
                current.Clear();
            }
            // Break single words longer than the line.
            var piece = word;
            while (gfx.MeasureString(piece.TrimEnd(), font).Width > width && piece.Length > 1)
            {
                int cut = piece.Length - 1;
                while (cut > 1 && gfx.MeasureString(piece[..cut], font).Width > width) cut--;
                yield return piece[..cut];
                piece = piece[cut..];
            }
            current.Append(piece);
        }
        if (current.Length > 0) yield return current.ToString().TrimEnd();
    }

    public static string PlainToHtml(string text, string title) =>
        $"<!doctype html>\n<html><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title>" +
        "<style>body{font:16px/1.6 system-ui,Segoe UI,sans-serif;max-width:46rem;margin:3rem auto;padding:0 1rem;color:#222}pre{white-space:pre-wrap;font:inherit}</style></head>" +
        $"<body><pre>{WebUtility.HtmlEncode(text)}</pre></body></html>\n";

    public static string MarkdownToHtml(string md, string title) =>
        $"<!doctype html>\n<html><head><meta charset=\"utf-8\"><title>{WebUtility.HtmlEncode(title)}</title>" +
        "<style>body{font:16px/1.6 system-ui,Segoe UI,sans-serif;max-width:46rem;margin:3rem auto;padding:0 1rem;color:#222}pre{background:#f5f2ee;padding:1rem;border-radius:8px;overflow:auto}code{font-family:Cascadia Code,Consolas,monospace}</style></head>" +
        $"<body>\n{MarkdownBody(md)}</body></html>\n";

    /// <summary>The HTML for a Markdown text's body: headings, paragraphs, lists, code. Well-formed XHTML too.</summary>
    public static string MarkdownBody(string md)
    {
        var html = new StringBuilder();
        bool inList = false, inCode = false;
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            html.Append("<p>").Append(Inline(string.Join(" ", paragraph))).Append("</p>\n");
            paragraph.Clear();
        }
        void CloseList()
        {
            if (!inList) return;
            html.Append("</ul>\n");
            inList = false;
        }

        foreach (var raw in md.Replace("\r", "").Split('\n'))
        {
            if (raw.TrimStart().StartsWith("```"))
            {
                FlushParagraph(); CloseList();
                html.Append(inCode ? "</code></pre>\n" : "<pre><code>");
                inCode = !inCode;
                continue;
            }
            if (inCode) { html.Append(WebUtility.HtmlEncode(raw)).Append('\n'); continue; }
            if (string.IsNullOrWhiteSpace(raw)) { FlushParagraph(); CloseList(); continue; }
            if (HeadingRegex().Match(raw) is { Success: true } h)
            {
                FlushParagraph(); CloseList();
                int level = h.Groups[1].Length;
                html.Append($"<h{level}>{Inline(h.Groups[2].Value)}</h{level}>\n");
                continue;
            }
            if (ListRegex().Match(raw) is { Success: true } li)
            {
                FlushParagraph();
                if (!inList) { html.Append("<ul>\n"); inList = true; }
                html.Append($"<li>{Inline(li.Groups[1].Value)}</li>\n");
                continue;
            }
            CloseList();
            paragraph.Add(raw.Trim());
        }
        FlushParagraph(); CloseList();
        if (inCode) html.Append("</code></pre>\n");
        return html.ToString();
    }

    static string Inline(string text)
    {
        var s = WebUtility.HtmlEncode(text);
        s = Regex.Replace(s, @"`([^`]+)`", "<code>$1</code>");
        s = Regex.Replace(s, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
        s = Regex.Replace(s, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "<em>$1</em>");
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)\s]+)\)", "<a href=\"$2\">$1</a>");
        return s;
    }

    static string StripInlineMarkdown(string line)
    {
        var s = Regex.Replace(line, @"\*\*(.+?)\*\*", "$1");
        s = Regex.Replace(s, @"`([^`]+)`", "$1");
        s = Regex.Replace(s, @"\[([^\]]+)\]\(([^)\s]+)\)", "$1 ($2)");
        s = Regex.Replace(s, @"^\s*[-*+]\s+", "• ");
        return s;
    }

    public static string HtmlToText(string html)
    {
        var s = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<br\s*/?>|</p>|</h\d>|</li>|</div>|</tr>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim() + "\n";
    }

    // ---------- Delimited data ----------

    public static string ConvertDelimited(string text, string fromExt, string target) =>
        RowsTo(ParseDelimited(text, fromExt == "tsv" ? '\t' : ','), target, "");

    /// <summary>Rows (the first is the header) as csv, tsv, json, a Markdown table or an HTML table.</summary>
    public static string RowsTo(List<List<string>> rows, string target, string title)
    {
        switch (target)
        {
            case "csv":
            case "tsv":
                char to = target == "tsv" ? '\t' : ',';
                string Escape(string v) => v.IndexOfAny([to, '"', '\r', '\n']) >= 0 ? '"' + v.Replace("\"", "\"\"") + '"' : v;
                return string.Join("\r\n", rows.Select(r => string.Join(to, r.Select(Escape)))) + "\r\n";
            case "json":
                if (rows.Count == 0) return "[]";
                var header = rows[0];
                var items = rows.Skip(1).Select(r =>
                {
                    var o = new Dictionary<string, string>();
                    for (int i = 0; i < header.Count; i++) o[string.IsNullOrEmpty(header[i]) ? $"column{i + 1}" : header[i]] = i < r.Count ? r[i] : "";
                    return o;
                });
                return JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            case "md":
                if (rows.Count == 0) return "";
                int width = rows.Max(r => r.Count);
                string Cell(List<string> r, int i) => i < r.Count ? r[i].Replace("|", "\\|").Replace("\r", "").Replace("\n", "<br>") : "";
                var md = new StringBuilder();
                md.Append("| ").Append(string.Join(" | ", Enumerable.Range(0, width).Select(i => Cell(rows[0], i)))).Append(" |\n");
                md.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", width))).Append('\n');
                foreach (var r in rows.Skip(1)) md.Append("| ").Append(string.Join(" | ", Enumerable.Range(0, width).Select(i => Cell(r, i)))).Append(" |\n");
                return md.ToString();
            default:
                var sb = new StringBuilder("<!doctype html>\n<html><head><meta charset=\"utf-8\"><title>" + WebUtility.HtmlEncode(title) + "</title><style>body{font:14px system-ui,Segoe UI,sans-serif;margin:2rem}table{border-collapse:collapse}td,th{border:1px solid #ddd;padding:6px 10px;text-align:left}th{background:#faf3ec}</style></head><body><table>\n");
                for (int i = 0; i < rows.Count; i++)
                {
                    var cell = i == 0 ? "th" : "td";
                    sb.Append("<tr>").Append(string.Concat(rows[i].Select(v => $"<{cell}>{WebUtility.HtmlEncode(v)}</{cell}>"))).Append("</tr>\n");
                }
                return sb.Append("</table></body></html>\n").ToString();
        }
    }

    public static List<List<string>> ParseDelimited(string text, char separator)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var value = new StringBuilder();
        bool quoted = false;
        text = text.TrimStart('﻿');
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == separator && !quoted) { row.Add(value.ToString()); value.Clear(); }
            else if ((c == '\n' || c == '\r') && !quoted)
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(value.ToString()); value.Clear();
                rows.Add(row); row = [];
            }
            else value.Append(c);
        }
        if (value.Length > 0 || row.Count > 0) { row.Add(value.ToString()); rows.Add(row); }
        return rows;
    }

    // ---------- Subtitles ----------

    public static string ConvertSubtitles(string text, string target)
    {
        text = text.Replace("\r\n", "\n").TrimStart('﻿');
        if (target == "txt")
            return Regex.Replace(Regex.Replace(text, @"(?m)^(WEBVTT[^\n]*|\d+|.*-->.*)\n?", ""), @"<[^>]+>", "").Trim() + "\n";
        if (target == "vtt")
            return "WEBVTT\n\n" + Regex.Replace(Regex.Replace(text, @"(?m)^\d+\n(?=\d{2}:)", ""), @"(\d{2}:\d{2}:\d{2}),(\d{3})", "$1.$2").Trim() + "\n";
        var blocks = Regex.Replace(text, @"\AWEBVTT[^\n]*\n", "").Trim().Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        int n = 0;
        return string.Join("\n\n", blocks.Where(b => b.Contains("-->")).Select(b =>
        {
            var cue = string.Join("\n", b.Split('\n').SkipWhile(l => !l.Contains("-->")));
            cue = Regex.Replace(cue, @"(?m)^(\d{2}:\d{2}\.\d{3})", "00:$1");
            cue = Regex.Replace(cue, @"--> (\d{2}:\d{2}\.\d{3})", "--> 00:$1");
            cue = Regex.Replace(cue, @"(\d{2}:\d{2}:\d{2})\.(\d{3})", "$1,$2");
            return ++n + "\n" + cue;
        })) + "\n";
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^\s*[-*+]\s+(.*)$")]
    private static partial Regex ListRegex();
}

using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace FileWheel.Core;

/// <summary>Saves a <see cref="Doc"/> as any document format, keeping pictures wherever the format can hold them.</summary>
public static class Documents
{
    public static string Save(string source, Doc doc, string target)
    {
        if (!doc.HasText && !doc.Blocks.OfType<Picture>().Any()) throw new IOException("There's no text or pictures to convert.");
        return target switch
        {
            "pdf" => Output.Save(source, "", "pdf", temp => PdfDocWriter.Write(doc, temp)),
            "docx" => Output.Save(source, "", "docx", temp => DocxWriter.Write(doc, temp)),
            "html" => Output.SaveBytes(source, "", "html", Encoding.UTF8.GetBytes(doc.ToHtml())),
            "md" => Output.SaveBytes(source, "", "md", Encoding.UTF8.GetBytes(doc.ToMarkdown())),
            "txt" => Output.SaveBytes(source, "", "txt", Encoding.UTF8.GetBytes(doc.ToPlainText())),
            "rtf" => Output.SaveBytes(source, "", "rtf", Encoding.ASCII.GetBytes(RtfDocWriter.Write(doc))),
            "epub" => Output.Save(source, "", "epub", temp => Epub.Write(doc, temp)),
            _ => throw new IOException($"Can't save documents as {target.ToUpperInvariant()}."),
        };
    }
}

/// <summary>Lays a <see cref="Doc"/> out on Letter pages: wrapped text with mixed bold/italic, headings, lists, tables, code and pictures.</summary>
public static class PdfDocWriter
{
    const double PageW = 612, PageH = 792, Margin = 64;

    public static void Write(Doc doc, string path)
    {
        GlobalFontSettings.FontResolver ??= new TextDocuments.WindowsFonts();
        using var pdf = new PdfDocument();
        pdf.Info.Title = doc.Title;
        var layout = new Layout(pdf);
        int number = 0;
        foreach (var block in doc.Blocks)
        {
            if (block is not Paragraph { List: "number" }) number = 0;
            switch (block)
            {
                case Heading h:
                    double size = h.Level switch { 1 => 20, 2 => 16, 3 => 13.5, _ => 12 };
                    layout.Space(h.Level == 1 ? 14 : 10);
                    layout.Text(h.Runs.Select(r => r with { Bold = true }), size, 0, keepWithNext: true);
                    layout.Space(4);
                    break;
                case Paragraph { List: { } kind } p:
                    var marker = kind == "number" ? $"{++number}." : "•";
                    layout.Text(p.Runs, 10.5, 18, marker: marker);
                    layout.Space(2);
                    break;
                case Paragraph p:
                    layout.Text(p.Runs, 10.5, 0);
                    layout.Space(7);
                    break;
                case Picture pic: layout.Picture(pic); break;
                case TableBlock t: layout.Table(t.Rows); break;
                case CodeBlock c: layout.Code(c.Text); break;
                case PageBreak: layout.NewPage(); break;
            }
        }
        layout.Finish();
        if (pdf.PageCount == 0) pdf.AddPage();
        pdf.Save(path);
    }

    sealed class Layout(PdfDocument pdf)
    {
        XGraphics? gfx;
        double y;
        bool pageHasContent;
        static readonly double Width = PageW - Margin * 2;

        XGraphics G
        {
            get
            {
                if (gfx == null) NewPage(force: true);
                return gfx!;
            }
        }

        public void NewPage(bool force = false)
        {
            if (!force && !pageHasContent) return;
            gfx?.Dispose();
            var page = pdf.AddPage();
            page.Width = XUnit.FromPoint(PageW);
            page.Height = XUnit.FromPoint(PageH);
            gfx = XGraphics.FromPdfPage(page);
            y = Margin;
            pageHasContent = false;
        }

        void Room(double height)
        {
            if (gfx == null) NewPage(force: true);
            else if (y + height > PageH - Margin && pageHasContent) NewPage();
        }

        public void Space(double points) { if (gfx != null && pageHasContent) y += points; }

        static XFont Font(Run r, double size, bool mono = false) =>
            new(mono ? "Consolas" : "Segoe UI", size, (r.Bold, r.Italic) switch { (true, true) => XFontStyleEx.BoldItalic, (true, false) => XFontStyleEx.Bold, (false, true) => XFontStyleEx.Italic, _ => XFontStyleEx.Regular });

        /// <summary>Wraps runs word by word (each word in its own font) inside the column, starting <paramref name="indent"/> in.</summary>
        public void Text(IEnumerable<Run> runs, double size, double indent, string? marker = null, bool keepWithNext = false)
        {
            var words = new List<(string Text, XFont Font, bool NewLine)>();
            foreach (var run in runs)
            {
                var font = Font(run, size);
                var parts = run.Text.Replace("\r", "").Split('\n');
                for (int i = 0; i < parts.Length; i++)
                {
                    if (i > 0) words.Add(("", font, true));
                    foreach (var w in System.Text.RegularExpressions.Regex.Split(parts[i], @"(?<=\s)"))
                        if (w.Length > 0) words.Add((w.Replace("\t", "    "), font, false));
                }
            }
            if (words.Count == 0) return;
            double lineHeight = size * 1.38;
            Room(lineHeight * (keepWithNext ? 3 : 1));
            var g = G;
            double x0 = Margin + indent, max = Width - indent;
            if (marker != null) g.DrawString(marker, Font(new Run(""), size), XBrushes.Black, new XPoint(Margin + indent - 13, y + size));
            var line = new List<(string Text, XFont Font)>();
            double used = 0;
            void Emit()
            {
                Room(lineHeight);
                double x = x0;
                foreach (var (t, f) in line)
                {
                    G.DrawString(t, f, XBrushes.Black, new XPoint(x, y + size));
                    x += G.MeasureString(t, f).Width;
                }
                y += lineHeight;
                pageHasContent = true;
                line.Clear();
                used = 0;
            }
            foreach (var (text, font, newLine) in words)
            {
                if (newLine) { Emit(); continue; }
                double w = g.MeasureString(text, font).Width;
                if (used + w > max && line.Count > 0)
                {
                    Emit();
                    if (text.Trim().Length == 0) continue;
                }
                // A single word wider than the column is cut to fit.
                var piece = text;
                while (G.MeasureString(piece.TrimEnd(), font).Width > max && piece.Length > 1)
                {
                    int cut = piece.Length - 1;
                    while (cut > 1 && G.MeasureString(piece[..cut], font).Width > max) cut--;
                    line.Add((piece[..cut], font));
                    Emit();
                    piece = piece[cut..];
                }
                line.Add((piece, font));
                used += G.MeasureString(piece, font).Width;
            }
            if (line.Count > 0) Emit();
        }

        public void Picture(Picture pic)
        {
            double w = pic.Width * 72.0 / 96, h = pic.Height * 72.0 / 96;
            double scale = Math.Min(1, Math.Min(Width / w, (PageH - Margin * 2) / h));
            w *= scale;
            h *= scale;
            Room(h + 8);
            try
            {
                using var ms = new MemoryStream(pic.Data);
                using var image = XImage.FromStream(ms);
                G.DrawImage(image, Margin + (Width - w) / 2, y, w, h);
                y += h + 10;
                pageHasContent = true;
            }
            catch { /* a picture the PDF library can't embed is skipped */ }
        }

        public void Code(string text)
        {
            var font = new XFont("Consolas", 9);
            foreach (var raw in text.Replace("\r", "").Split('\n'))
            {
                Room(12.5);
                G.DrawRectangle(new XSolidBrush(XColor.FromArgb(244, 241, 238)), Margin - 4, y, Width + 8, 12.5);
                G.DrawString(raw.Replace("\t", "    "), font, XBrushes.Black, new XPoint(Margin, y + 9.5));
                y += 12.5;
                pageHasContent = true;
            }
            y += 8;
        }

        public void Table(List<List<string>> rows)
        {
            if (rows.Count == 0) return;
            int columns = rows.Max(r => r.Count);
            double cw = Width / columns;
            var pen = new XPen(XColor.FromArgb(190, 190, 190), 0.6);
            for (int r = 0; r < rows.Count; r++)
            {
                var font = new XFont("Segoe UI", 9, r == 0 ? XFontStyleEx.Bold : XFontStyleEx.Regular);
                // Wrap each cell, then draw the row as tall as its tallest cell.
                var cells = Enumerable.Range(0, columns).Select(c => Wrap(c < rows[r].Count ? rows[r][c] : "", font, cw - 8)).ToList();
                double height = Math.Max(1, cells.Max(c => c.Count)) * 12 + 6;
                Room(height);
                for (int c = 0; c < columns; c++)
                {
                    double x = Margin + c * cw;
                    if (r == 0) G.DrawRectangle(new XSolidBrush(XColor.FromArgb(242, 242, 242)), x, y, cw, height);
                    G.DrawRectangle(pen, x, y, cw, height);
                    for (int l = 0; l < cells[c].Count; l++) G.DrawString(cells[c][l], font, XBrushes.Black, new XPoint(x + 4, y + 12 + l * 12));
                }
                y += height;
                pageHasContent = true;
            }
            y += 10;
        }

        List<string> Wrap(string text, XFont font, double width)
        {
            var lines = new List<string>();
            foreach (var para in text.Replace("\r", "").Split('\n'))
            {
                var current = "";
                foreach (var word in para.Split(' '))
                {
                    var candidate = current.Length == 0 ? word : current + " " + word;
                    if (current.Length > 0 && G.MeasureString(candidate, font).Width > width) { lines.Add(current); current = word; }
                    else current = candidate;
                }
                lines.Add(current);
            }
            return lines;
        }

        public void Finish() => gfx?.Dispose();
    }
}

/// <summary>Rich Text with bold/italic, headings, lists, tables (as tabs) and embedded pictures — opens in Word and WordPad.</summary>
public static class RtfDocWriter
{
    public static string Write(Doc doc)
    {
        var sb = new StringBuilder(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss Segoe UI;}{\f1\fmodern Consolas;}}\viewkind4\uc1\pard\sa120\f0\fs22 ");
        int number = 0;
        foreach (var block in doc.Blocks)
        {
            if (block is not Paragraph { List: "number" }) number = 0;
            switch (block)
            {
                case Heading h:
                    int size = h.Level switch { 1 => 36, 2 => 30, 3 => 26, _ => 24 };
                    sb.Append($@"\pard\sb200\sa80\b\fs{size} ").Append(Runs(h.Runs, bold: true)).Append(@"\b0\fs22\par\pard\sa120 ");
                    break;
                case Paragraph { List: { } kind } p:
                    sb.Append(@"\pard\li360\fi-240\sa40 ").Append(kind == "number" ? $"{++number}.\\tab " : @"\bullet\tab ").Append(Runs(p.Runs)).Append(@"\par\pard\sa120 ");
                    break;
                case Paragraph p: sb.Append(Runs(p.Runs)).Append(@"\par "); break;
                case TableBlock t:
                    foreach (var row in t.Rows) sb.Append(string.Join(@"\tab ", row.Select(Escape))).Append(@"\par ");
                    break;
                case CodeBlock c: sb.Append(@"{\f1\fs18 ").Append(string.Join(@"\line ", c.Text.Split('\n').Select(Escape))).Append(@"}\par "); break;
                case Picture pic:
                    int twipsW = pic.Width * 15, twipsH = pic.Height * 15; // 96 px per inch, 1440 twips per inch
                    sb.Append($@"{{\pict{(pic.Ext == "jpg" ? @"\jpegblip" : @"\pngblip")}\picwgoal{twipsW}\pichgoal{twipsH} ").Append(Convert.ToHexString(pic.Data)).Append(@"}\par ");
                    break;
                case PageBreak: sb.Append(@"\page "); break;
            }
        }
        return sb.Append('}').ToString();
    }

    static string Runs(IEnumerable<Run> runs, bool bold = false) => string.Concat(runs.Select(r =>
    {
        var t = Escape(r.Text).Replace("\n", @"\line ");
        if (r.Italic) t = @"{\i " + t + "}";
        if (r.Bold && !bold) t = @"{\b " + t + "}";
        return t;
    }));

    static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '\\' or '{' or '}') sb.Append('\\').Append(c);
            else if (c == '\t') sb.Append(@"\tab ");
            else if (c == '\n') sb.Append('\n');
            else if (c > 127) sb.Append(@"\u").Append((short)c).Append('?');
            else if (c >= 32) sb.Append(c);
        }
        return sb.ToString();
    }
}

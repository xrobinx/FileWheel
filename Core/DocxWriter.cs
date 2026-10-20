using System.IO.Compression;
using System.Security;
using System.Text;

namespace FileWheel.Core;

/// <summary>
/// Writes a valid .docx: real Heading 1–4 styles (they show in Word's navigation pane), paragraphs
/// with bold/italic, bullet and numbered lists, bordered tables, code, page breaks and inline pictures.
/// </summary>
public sealed class DocxWriter
{
    const long EmuPerInch = 914400;
    readonly StringBuilder body = new();
    readonly List<(string Name, byte[] Data)> media = [];
    int numberedLists;

    /// <summary>Writes a whole structured document.</summary>
    public static void Write(Doc doc, string path)
    {
        var w = new DocxWriter();
        string? lastList = null;
        int listId = 0;
        foreach (var block in doc.Blocks)
        {
            if (block is Paragraph { List: "number" } && lastList != "number") listId = ++w.numberedLists + 1;
            lastList = block is Paragraph { List: { } k } ? k : null;
            switch (block)
            {
                case Heading h: w.Heading(h.Level, h.Runs); break;
                case Paragraph p: w.Runs(p.Runs, p.List == "bullet" ? 1 : p.List == "number" ? listId : 0); break;
                case Picture pic: w.Picture(pic.Data, pic.Ext, pic.Width, pic.Height); break;
                case TableBlock t: w.Table(t.Rows); break;
                case CodeBlock c: w.Code(c.Text); break;
                case FileWheel.Core.PageBreak: w.PageBreak(); break;
            }
        }
        w.Save(path);
    }

    public void Paragraph(string text, bool bold = false, double points = 0)
    {
        var props = new StringBuilder();
        if (bold) props.Append("<w:b/>");
        if (points > 0) props.Append($"<w:sz w:val=\"{(int)(points * 2)}\"/>");
        var run = props.Length > 0 ? $"<w:rPr>{props}</w:rPr>" : "";
        body.Append($"<w:p><w:r>{run}<w:t xml:space=\"preserve\">{Escape(text)}</w:t></w:r></w:p>");
    }

    public void Heading(int level, IEnumerable<Run> runs) =>
        body.Append($"<w:p><w:pPr><w:pStyle w:val=\"Heading{Math.Clamp(level, 1, 4)}\"/></w:pPr>{RunXml(runs)}</w:p>");

    /// <summary>A paragraph of runs; <paramref name="numId"/> 1 is a bullet list, 2+ a numbered list.</summary>
    public void Runs(IEnumerable<Run> runs, int numId = 0)
    {
        var props = numId > 0 ? $"<w:pPr><w:pStyle w:val=\"ListParagraph\"/><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"{numId}\"/></w:numPr></w:pPr>" : "";
        body.Append($"<w:p>{props}{RunXml(runs)}</w:p>");
    }

    public void Code(string text)
    {
        body.Append("<w:p><w:pPr><w:pStyle w:val=\"Code\"/></w:pPr>");
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) body.Append("<w:r><w:br/></w:r>");
            body.Append($"<w:r><w:t xml:space=\"preserve\">{Escape(lines[i])}</w:t></w:r>");
        }
        body.Append("</w:p>");
    }

    public void Table(IReadOnlyList<IReadOnlyList<string>> rows) => Table(rows.Select(r => r.ToList()).ToList());

    public void Table(List<List<string>> rows)
    {
        if (rows.Count == 0) return;
        int columns = rows.Max(r => r.Count);
        const string Border = "w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"";
        body.Append("<w:tbl><w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/>" +
            $"<w:tblBorders><w:top {Border}/><w:left {Border}/><w:bottom {Border}/><w:right {Border}/><w:insideH {Border}/><w:insideV {Border}/></w:tblBorders>" +
            "<w:tblCellMar><w:left w:w=\"80\" w:type=\"dxa\"/><w:right w:w=\"80\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr><w:tblGrid>");
        for (int c = 0; c < columns; c++) body.Append($"<w:gridCol w:w=\"{9360 / columns}\"/>");
        body.Append("</w:tblGrid>");
        for (int r = 0; r < rows.Count; r++)
        {
            body.Append("<w:tr>");
            for (int c = 0; c < columns; c++)
            {
                var text = c < rows[r].Count ? rows[r][c] : "";
                var shade = r == 0 ? "<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F2F2F2\"/>" : "";
                body.Append($"<w:tc><w:tcPr><w:tcW w:w=\"{9360 / columns}\" w:type=\"dxa\"/>{shade}</w:tcPr><w:p><w:pPr><w:spacing w:after=\"0\"/></w:pPr>")
                    .Append(RunXml([new Run(text, Bold: r == 0)])).Append("</w:p></w:tc>");
            }
            body.Append("</w:tr>");
        }
        body.Append("</w:tbl><w:p/>");
    }

    public void PageBreak() => body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");

    /// <summary>Adds a picture at its display size (96 px per inch), scaled down to fit the printable area (6.5 × 9 in).</summary>
    public void Picture(byte[] data, string ext, int pixelWidth, int pixelHeight)
    {
        int n = media.Count + 1;
        var name = $"image{n}.{(ext is "jpg" or "jpeg" ? "jpeg" : ext)}";
        media.Add((name, data));
        double w = pixelWidth / 96.0, h = pixelHeight / 96.0;
        double fit = Math.Min(1, Math.Min(6.5 / w, 9.0 / h));
        long cx = (long)(w * fit * EmuPerInch), cy = (long)(h * fit * EmuPerInch);
        body.Append(
            $"<w:p><w:pPr><w:jc w:val=\"center\"/></w:pPr><w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\">" +
            $"<wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:docPr id=\"{n}\" name=\"Picture {n}\"/>" +
            "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic>" +
            $"<pic:nvPicPr><pic:cNvPr id=\"{n}\" name=\"{name}\"/><pic:cNvPicPr/></pic:nvPicPr>" +
            $"<pic:blipFill><a:blip r:embed=\"rImg{n}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
            $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
            "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>");
    }

    static string RunXml(IEnumerable<Run> runs)
    {
        var sb = new StringBuilder();
        foreach (var run in runs)
        {
            var props = (run.Bold ? "<w:b/>" : "") + (run.Italic ? "<w:i/>" : "");
            var lines = run.Text.Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (i > 0) sb.Append("<w:r><w:br/></w:r>");
                sb.Append("<w:r>").Append(props.Length > 0 ? $"<w:rPr>{props}</w:rPr>" : "").Append($"<w:t xml:space=\"preserve\">{Escape(lines[i])}</w:t></w:r>");
            }
        }
        return sb.ToString();
    }

    public void Save(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
            "<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
            "<Default Extension=\"gif\" ContentType=\"image/gif\"/>" +
            "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
            "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
            "<Override PartName=\"/word/numbering.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml\"/>" +
            "</Types>");
        Add(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "</Relationships>");
        var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "<Relationship Id=\"rNumbering\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering\" Target=\"numbering.xml\"/>");
        for (int i = 0; i < media.Count; i++)
            rels.Append($"<Relationship Id=\"rImg{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/{media[i].Name}\"/>");
        rels.Append("</Relationships>");
        Add(zip, "word/_rels/document.xml.rels", rels.ToString());
        Add(zip, "word/styles.xml", Styles);
        Add(zip, "word/numbering.xml", Numbering());
        Add(zip, "word/document.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
            "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
            "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
            "xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><w:body>" +
            body +
            "<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:header=\"720\" w:footer=\"720\" w:gutter=\"0\"/></w:sectPr>" +
            "</w:body></w:document>");
        foreach (var (name, data) in media)
        {
            var entry = zip.CreateEntry("word/media/" + name, CompressionLevel.NoCompression);
            using var s = entry.Open();
            s.Write(data);
        }
    }

    static string HeadingStyle(int level, int halfPoints, string color) =>
        $"<w:style w:type=\"paragraph\" w:styleId=\"Heading{level}\"><w:name w:val=\"heading {level}\"/><w:basedOn w:val=\"Normal\"/><w:next w:val=\"Normal\"/><w:qFormat/>" +
        $"<w:pPr><w:keepNext/><w:spacing w:before=\"{(level == 1 ? 360 : 240)}\" w:after=\"80\"/><w:outlineLvl w:val=\"{level - 1}\"/></w:pPr>" +
        $"<w:rPr><w:b/><w:color w:val=\"{color}\"/><w:sz w:val=\"{halfPoints}\"/></w:rPr></w:style>";

    static readonly string Styles =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
        "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\" w:eastAsia=\"Calibri\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/></w:rPr></w:rPrDefault>" +
        "<w:pPrDefault><w:pPr><w:spacing w:after=\"140\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
        "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
        HeadingStyle(1, 36, "1F3864") + HeadingStyle(2, 30, "2F5496") + HeadingStyle(3, 26, "2F5496") + HeadingStyle(4, 23, "2F5496") +
        "<w:style w:type=\"paragraph\" w:styleId=\"ListParagraph\"><w:name w:val=\"List Paragraph\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:spacing w:after=\"40\"/><w:ind w:left=\"720\"/></w:pPr></w:style>" +
        "<w:style w:type=\"paragraph\" w:styleId=\"Code\"><w:name w:val=\"Code\"/><w:basedOn w:val=\"Normal\"/><w:pPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F4F1EE\"/><w:spacing w:after=\"140\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr>" +
        "<w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/><w:sz w:val=\"19\"/></w:rPr></w:style>" +
        "</w:styles>";

    /// <summary>Bullets are list 1; each numbered list gets its own id so it starts again at 1.</summary>
    string Numbering()
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:numbering xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
            "<w:abstractNum w:abstractNumId=\"0\"><w:multiLevelType w:val=\"singleLevel\"/><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"•\"/><w:lvlJc w:val=\"left\"/>" +
            "<w:pPr><w:ind w:left=\"720\" w:hanging=\"360\"/></w:pPr><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\"/></w:rPr></w:lvl></w:abstractNum>" +
            "<w:abstractNum w:abstractNumId=\"1\"><w:multiLevelType w:val=\"singleLevel\"/><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/><w:lvlJc w:val=\"left\"/>" +
            "<w:pPr><w:ind w:left=\"720\" w:hanging=\"360\"/></w:pPr></w:lvl></w:abstractNum>" +
            "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num>");
        for (int i = 0; i < Math.Max(1, numberedLists); i++)
            sb.Append($"<w:num w:numId=\"{i + 2}\"><w:abstractNumId w:val=\"1\"/><w:lvlOverride w:ilvl=\"0\"><w:startOverride w:val=\"1\"/></w:lvlOverride></w:num>");
        return sb.Append("</w:numbering>").ToString();
    }

    static void Add(ZipArchive zip, string name, string xml)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(Encoding.UTF8.GetBytes(xml));
    }

    static string Escape(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
            if (c == '\t' || c >= 0x20 && c != 0xFFFE && c != 0xFFFF) clean.Append(c);
        return SecurityElement.Escape(clean.ToString()) ?? "";
    }
}

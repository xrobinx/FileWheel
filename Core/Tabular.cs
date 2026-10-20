using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace FileWheel.Core;

/// <summary>Rows of cells: reading and writing .xlsx, JSON tables and the plain-text table formats.</summary>
public static class Tabular
{
    static readonly XNamespace M = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    static readonly XNamespace Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Saves <paramref name="rows"/> beside <paramref name="source"/> as csv, tsv, json, html, md, xlsx or pdf.</summary>
    public static string Save(string source, List<List<string>> rows, string target, CancellationToken ct)
    {
        if (rows.Count == 0) throw new IOException("There's no table data in this file.");
        switch (target)
        {
            case "xlsx":
                return Output.Save(source, "", "xlsx", temp => WriteXlsx(rows, temp));
            case "pdf":
                // Lay the table out as a workbook, then let Excel (or LibreOffice) print it.
                var book = Path.Combine(Output.WorkFolder, Guid.NewGuid().ToString("N") + ".xlsx");
                try
                {
                    WriteXlsx(rows, book);
                    return Output.Save(source, "", "pdf", temp => OfficeEngine.Convert(book, "pdf", temp, ct));
                }
                finally { Output.TryDelete(book); }
            default:
                return Output.SaveBytes(source, "", target, Encoding.UTF8.GetBytes(TextDocuments.RowsTo(rows, target, Path.GetFileNameWithoutExtension(source))));
        }
    }

    // ---------- JSON ----------

    /// <summary>True when the file is a JSON array of objects (something that reads naturally as a table).</summary>
    public static bool IsJsonTable(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 8 * 1024 * 1024) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && root.EnumerateArray().Take(50).All(e => e.ValueKind == JsonValueKind.Object);
        }
        catch { return false; }
    }

    /// <summary>A JSON array of objects as rows; nested objects become dotted columns ("address.city").</summary>
    public static List<List<string>> ReadJson(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new IOException("This JSON isn't a list of records.");
        var columns = new List<string>();
        var records = new List<Dictionary<string, string>>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var record = new Dictionary<string, string>();
            Flatten(item, "", record);
            foreach (var key in record.Keys)
                if (!columns.Contains(key)) columns.Add(key);
            records.Add(record);
        }
        var rows = new List<List<string>> { columns };
        rows.AddRange(records.Select(r => columns.Select(c => r.TryGetValue(c, out var v) ? v : "").ToList()));
        return rows;
    }

    static void Flatten(JsonElement e, string prefix, Dictionary<string, string> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) Flatten(p.Value, prefix.Length == 0 ? p.Name : prefix + "." + p.Name, into);
                break;
            case JsonValueKind.String:
                into[prefix.Length == 0 ? "value" : prefix] = e.GetString() ?? "";
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                into[prefix.Length == 0 ? "value" : prefix] = "";
                break;
            default:
                into[prefix.Length == 0 ? "value" : prefix] = e.GetRawText();
                break;
        }
    }

    // ---------- .xlsx ----------

    /// <summary>Reads the first worksheet. Dates come out as yyyy-MM-dd (with a time when there is one).</summary>
    public static List<List<string>> ReadXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XDocument Load(string name) => XDocument.Load((zip.GetEntry(name) ?? throw new IOException("This workbook is missing " + name + ".")).Open());

        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") != null)
            foreach (var si in Load("xl/sharedStrings.xml").Root!.Elements(M + "si"))
                shared.Add(string.Concat(si.Descendants(M + "t").Select(t => t.Value)));

        var dateStyles = new HashSet<int>();
        if (zip.GetEntry("xl/styles.xml") != null)
        {
            var styles = Load("xl/styles.xml").Root!;
            var customDates = styles.Element(M + "numFmts")?.Elements(M + "numFmt")
                .Where(f => IsDateFormat((string?)f.Attribute("formatCode") ?? ""))
                .Select(f => (int?)f.Attribute("numFmtId") ?? -1).ToHashSet() ?? [];
            int i = 0;
            foreach (var xf in styles.Element(M + "cellXfs")?.Elements(M + "xf") ?? [])
            {
                int id = (int?)xf.Attribute("numFmtId") ?? 0;
                if (id is >= 14 and <= 22 or >= 45 and <= 47 || customDates.Contains(id)) dateStyles.Add(i);
                i++;
            }
        }

        var workbook = Load("xl/workbook.xml").Root!;
        var firstSheet = workbook.Element(M + "sheets")?.Elements(M + "sheet").FirstOrDefault() ?? throw new IOException("This workbook has no sheets.");
        var target = "worksheets/sheet1.xml";
        var rid = (string?)firstSheet.Attribute(R + "id");
        if (rid != null && zip.GetEntry("xl/_rels/workbook.xml.rels") != null)
        {
            var rel = Load("xl/_rels/workbook.xml.rels").Root!.Elements(Pkg + "Relationship").FirstOrDefault(e => (string?)e.Attribute("Id") == rid);
            if (rel != null) target = (string)rel.Attribute("Target")!;
        }
        target = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;

        var rows = new List<List<string>>();
        foreach (var row in Load(target).Descendants(M + "row"))
        {
            int rowNumber = (int?)row.Attribute("r") ?? rows.Count + 1;
            while (rows.Count < rowNumber - 1) rows.Add([]);
            var cells = new List<string>();
            foreach (var c in row.Elements(M + "c"))
            {
                int column = ColumnIndex((string?)c.Attribute("r"), cells.Count);
                while (cells.Count < column) cells.Add("");
                var raw = c.Element(M + "v")?.Value ?? "";
                var value = (string?)c.Attribute("t") switch
                {
                    "s" => int.TryParse(raw, out var k) && k >= 0 && k < shared.Count ? shared[k] : "",
                    "inlineStr" => string.Concat(c.Descendants(M + "t").Select(t => t.Value)),
                    "b" => raw == "1" ? "TRUE" : "FALSE",
                    "str" or "e" => raw,
                    _ => dateStyles.Contains((int?)c.Attribute("s") ?? -1) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) ? DateText(serial) : raw,
                };
                cells.Add(value);
            }
            rows.Add(cells);
        }
        // Drop trailing empty rows and pad every row to the same width.
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrEmpty)) rows.RemoveAt(rows.Count - 1);
        int width = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        foreach (var r in rows) while (r.Count < width) r.Add("");
        return rows;
    }

    static bool IsDateFormat(string code)
    {
        var bare = System.Text.RegularExpressions.Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.", "");
        return bare.IndexOfAny(['y', 'd', 'Y', 'D']) >= 0 || (bare.Contains('m') && (bare.Contains('h') || bare.Contains('s')));
    }

    static string DateText(double serial)
    {
        try
        {
            var date = DateTime.FromOADate(serial);
            return date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch { return serial.ToString(CultureInfo.InvariantCulture); }
    }

    static int ColumnIndex(string? reference, int fallback)
    {
        if (string.IsNullOrEmpty(reference)) return fallback;
        int n = 0;
        foreach (var ch in reference)
        {
            if (ch is < 'A' or > 'Z') break;
            n = n * 26 + (ch - 'A' + 1);
        }
        return n == 0 ? fallback : n - 1;
    }

    static string ColumnName(int index)
    {
        var name = "";
        for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }

    /// <summary>Writes a one-sheet workbook. Numbers stay numbers; the first row is bold.</summary>
    public static void WriteXlsx(List<List<string>> rows, string path)
    {
        var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (int r = 0; r < rows.Count; r++)
        {
            sheet.Append($"<row r=\"{r + 1}\">");
            for (int c = 0; c < rows[r].Count; c++)
            {
                var v = rows[r][c];
                if (v.Length == 0) continue;
                var cell = ColumnName(c) + (r + 1);
                var style = r == 0 ? " s=\"1\"" : "";
                if (r > 0 && LooksNumeric(v))
                    sheet.Append($"<c r=\"{cell}\"{style}><v>{v.Trim()}</v></c>");
                else
                    sheet.Append($"<c r=\"{cell}\" t=\"inlineStr\"{style}><is><t xml:space=\"preserve\">{Escape(v)}</t></is></c>");
            }
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
        Add(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Add(zip, "xl/workbook.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Add(zip, "xl/_rels/workbook.xml.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        Add(zip, "xl/styles.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
            "</styleSheet>");
        Add(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
    }

    /// <summary>Plain decimal numbers only, so ids like "007" or phone numbers stay text.</summary>
    static bool LooksNumeric(string v)
    {
        var t = v.Trim();
        if (t.Length == 0 || t.Length > 15) return false;
        if (t.Length > 1 && t[0] == '0' && t[1] != '.') return false;
        if (t.StartsWith("-0") && t.Length > 2 && t[2] != '.') return false;
        return double.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out _);
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
            if (c is '\t' or '\n' or '\r' || c >= 0x20 && c != 0xFFFE && c != 0xFFFF) clean.Append(c);
        return SecurityElement.Escape(clean.ToString()) ?? "";
    }
}

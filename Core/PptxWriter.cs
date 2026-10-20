using System.IO.Compression;
using System.Text;

namespace FileWheel.Core;

/// <summary>
/// Writes a valid PowerPoint deck where every slide is one full-bleed picture (PDF → PPTX).
/// The slide size follows the first picture's shape.
/// </summary>
public sealed class PptxWriter
{
    const long SlideWidth = 12192000; // 13.333 in, PowerPoint's widescreen width in EMU
    readonly List<(byte[] Data, string Ext)> slides = [];
    long slideHeight = 6858000;

    public void Slide(byte[] data, string ext, int pixelWidth, int pixelHeight)
    {
        if (slides.Count == 0) slideHeight = Math.Clamp((long)(SlideWidth * (double)pixelHeight / Math.Max(1, pixelWidth)), 914400, 51206400);
        slides.Add((data, ext == "jpg" ? "jpeg" : ext));
    }

    public void Save(string path)
    {
        if (slides.Count == 0) throw new IOException("There are no pages to put on slides.");
        const string Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
        const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string Head = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var types = new StringBuilder(Head + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
            "<Override PartName=\"/ppt/presentation.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml\"/>" +
            "<Override PartName=\"/ppt/slideMasters/slideMaster1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml\"/>" +
            "<Override PartName=\"/ppt/slideLayouts/slideLayout1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml\"/>" +
            "<Override PartName=\"/ppt/theme/theme1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.theme+xml\"/>");
        for (int i = 1; i <= slides.Count; i++)
            types.Append($"<Override PartName=\"/ppt/slides/slide{i}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.presentationml.slide+xml\"/>");
        Add(zip, "[Content_Types].xml", types.Append("</Types>").ToString());

        Add(zip, "_rels/.rels", Head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{Rel}/officeDocument\" Target=\"ppt/presentation.xml\"/></Relationships>");

        var ids = new StringBuilder();
        var presRels = new StringBuilder(Head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideMaster\" Target=\"slideMasters/slideMaster1.xml\"/>" +
            $"<Relationship Id=\"rId2\" Type=\"{Rel}/theme\" Target=\"theme/theme1.xml\"/>");
        for (int i = 1; i <= slides.Count; i++)
        {
            ids.Append($"<p:sldId id=\"{255 + i}\" r:id=\"rId{i + 2}\"/>");
            presRels.Append($"<Relationship Id=\"rId{i + 2}\" Type=\"{Rel}/slide\" Target=\"slides/slide{i}.xml\"/>");
        }
        Add(zip, "ppt/presentation.xml", Head + $"<p:presentation xmlns:a=\"{A}\" xmlns:r=\"{Rel}\" xmlns:p=\"{P}\">" +
            "<p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>" +
            $"<p:sldIdLst>{ids}</p:sldIdLst><p:sldSz cx=\"{SlideWidth}\" cy=\"{slideHeight}\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/></p:presentation>");
        Add(zip, "ppt/_rels/presentation.xml.rels", presRels.Append("</Relationships>").ToString());

        const string EmptyTree = "<p:cSld><p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
            "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr></p:spTree></p:cSld>";
        Add(zip, "ppt/slideMasters/slideMaster1.xml", Head + $"<p:sldMaster xmlns:a=\"{A}\" xmlns:r=\"{Rel}\" xmlns:p=\"{P}\">" + EmptyTree +
            "<p:clrMap bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"/>" +
            "<p:sldLayoutIdLst><p:sldLayoutId id=\"2147483649\" r:id=\"rId1\"/></p:sldLayoutIdLst></p:sldMaster>");
        Add(zip, "ppt/slideMasters/_rels/slideMaster1.xml.rels", Head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
            $"<Relationship Id=\"rId2\" Type=\"{Rel}/theme\" Target=\"../theme/theme1.xml\"/></Relationships>");
        Add(zip, "ppt/slideLayouts/slideLayout1.xml", Head + $"<p:sldLayout xmlns:a=\"{A}\" xmlns:r=\"{Rel}\" xmlns:p=\"{P}\" type=\"blank\" preserve=\"1\">" +
            EmptyTree.Replace("<p:cSld>", "<p:cSld name=\"Blank\">") + "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        Add(zip, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", Head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideMaster\" Target=\"../slideMasters/slideMaster1.xml\"/></Relationships>");
        Add(zip, "ppt/theme/theme1.xml", Theme(A));

        for (int i = 1; i <= slides.Count; i++)
        {
            var (data, ext) = slides[i - 1];
            var media = zip.CreateEntry($"ppt/media/image{i}.{ext}", CompressionLevel.NoCompression);
            using (var s = media.Open()) s.Write(data);
            Add(zip, $"ppt/slides/slide{i}.xml", Head + $"<p:sld xmlns:a=\"{A}\" xmlns:r=\"{Rel}\" xmlns:p=\"{P}\"><p:cSld><p:spTree>" +
                "<p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr>" +
                "<p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>" +
                $"<p:pic><p:nvPicPr><p:cNvPr id=\"2\" name=\"Page {i}\"/><p:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></p:cNvPicPr><p:nvPr/></p:nvPicPr>" +
                "<p:blipFill><a:blip r:embed=\"rId2\"/><a:stretch><a:fillRect/></a:stretch></p:blipFill>" +
                $"<p:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{SlideWidth}\" cy=\"{slideHeight}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></p:spPr></p:pic>" +
                "</p:spTree></p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");
            Add(zip, $"ppt/slides/_rels/slide{i}.xml.rels", Head + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                $"<Relationship Id=\"rId1\" Type=\"{Rel}/slideLayout\" Target=\"../slideLayouts/slideLayout1.xml\"/>" +
                $"<Relationship Id=\"rId2\" Type=\"{Rel}/image\" Target=\"../media/image{i}.{ext}\"/></Relationships>");
        }
    }

    /// <summary>The smallest complete Office theme (colors, fonts and the three required style lists).</summary>
    static string Theme(string a)
    {
        string Solid(string scheme) => $"<a:solidFill><a:schemeClr val=\"{scheme}\"/></a:solidFill>";
        string Line(int w) => $"<a:ln w=\"{w}\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln>";
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            $"<a:theme xmlns:a=\"{a}\" name=\"FileWheel\"><a:themeElements>" +
            "<a:clrScheme name=\"FileWheel\"><a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>" +
            "<a:dk2><a:srgbClr val=\"1F2937\"/></a:dk2><a:lt2><a:srgbClr val=\"F3F4F6\"/></a:lt2><a:accent1><a:srgbClr val=\"F27C38\"/></a:accent1><a:accent2><a:srgbClr val=\"2F7BF6\"/></a:accent2>" +
            "<a:accent3><a:srgbClr val=\"34C759\"/></a:accent3><a:accent4><a:srgbClr val=\"F7C33A\"/></a:accent4><a:accent5><a:srgbClr val=\"AF52DE\"/></a:accent5><a:accent6><a:srgbClr val=\"FF3B30\"/></a:accent6>" +
            "<a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink><a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>" +
            "<a:fontScheme name=\"FileWheel\"><a:majorFont><a:latin typeface=\"Segoe UI\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>" +
            "<a:minorFont><a:latin typeface=\"Segoe UI\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>" +
            "<a:fmtScheme name=\"FileWheel\"><a:fillStyleLst>" + Solid("phClr") + Solid("phClr") + Solid("phClr") + "</a:fillStyleLst>" +
            "<a:lnStyleLst>" + Line(6350) + Line(12700) + Line(19050) + "</a:lnStyleLst>" +
            "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>" +
            "<a:bgFillStyleLst>" + Solid("phClr") + Solid("phClr") + Solid("phClr") + "</a:bgFillStyleLst></a:fmtScheme>" +
            "</a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>";
    }

    static void Add(ZipArchive zip, string name, string xml)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = entry.Open();
        s.Write(Encoding.UTF8.GetBytes(xml));
    }
}

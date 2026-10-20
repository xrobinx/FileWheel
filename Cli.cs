using System.Runtime.InteropServices;
using FileWheel.Core;

namespace FileWheel;

/// <summary>
/// Command-line entry points used for testing the engines without the UI:
///   FileWheel --convert &lt;target&gt; &lt;files…&gt;
///   FileWheel --convert --probe
/// </summary>
public static class Cli
{
    public static int Convert(string[] args)
    {
        AttachConsole(-1);
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 1 && args[0] == "--probe")
        {
            Console.WriteLine("Image encoders: " + string.Join(", ", new[] { "png", "jpg", "webp", "heic", "tiff", "avif", "bmp", "gif", "ico" }.Where(Formats.CanEncodeImage)));
            Console.WriteLine("ffmpeg: " + (Formats.Ffmpeg ?? "missing"));
            Console.WriteLine("LibreOffice: " + (Formats.LibreOffice ?? "not installed"));
            return 0;
        }
        if (args.Length == 2 && args[0] == "--matrix") return Matrix(args[1]);
        if (args.Length >= 3 && args[0] == "--rename")
        {
            foreach (var r in FileOps.Rename(args.Skip(2).ToList(), args[1])) Console.WriteLine("  → " + r);
            return 0;
        }
        if (args.Length == 1 && args[0] == "--share")
        {
            foreach (var item in UI.Share.Items([])) Console.WriteLine($"{item.Label,-12} {item.Hint}  icon: {(item.Icon?.StartsWith("app:") == true ? (UI.Share.Icon(item.Icon[4..]) is { } i ? $"{i.PixelWidth}px" : "none") : item.Icon)}");
            return 0;
        }
        if (args.Length < 2) { Console.Error.WriteLine("usage: --convert <target> <files...>"); return 2; }
        var target = args[0];
        var files = args.Skip(1).ToList();
        foreach (var f in files)
        {
            Console.WriteLine($"{Path.GetFileName(f)}: convert → [{string.Join(", ", Formats.ConvertTargets([f]))}]  tools → [{string.Join(", ", Formats.Tools([f]))}]");
        }
        if (target == "list") return 0;
        if (target.StartsWith("tool:"))
        {
            var outputs = new List<string>();
            foreach (var f in files)
            {
                outputs.Add(target switch
                {
                    "tool:crop" => ImageTools.Crop(f, new NormRect(0.1, 0.1, 0.5, 0.4)),
                    "tool:compress" => ImageTools.Compress(f, "keep", 60, 0),
                    "tool:edit" => ImageTools.Edit(f, new Adjustments { Rotation = 90, Brightness = 20, Saturation = 30, Scale = 50 }),
                    "tool:redact" => ImageTools.SaveRedacted(f, [(new NormRect(0.1, 0.1, 0.3, 0.2), RedactStyle.Box), (new NormRect(0.5, 0.5, 0.3, 0.3), RedactStyle.Pixelate)]),
                    "tool:strip" => ImageTools.StripMetadata(f),
                    "tool:pdfrotate" => PdfTools.Rotate(f, 90),
                    "tool:pdfcompress" => PdfTools.Compress(f, 130, 66, _ => { }, CancellationToken.None),
                    "tool:bg" => UI.Editors.BackgroundEditor.TestExport(f),
                    "tool:annotate" => UI.Editors.AnnotateEditor.TestExport(f),
                    "tool:meta" => string.Join("\n", ImageTools.ReadMetadata(f).Select(m => $"   {m.Group} · {m.Name}: {m.Value}")),
                    "tool:snapshot" => Media.Snapshot(f, 1.2),
                    "tool:speed" => Media.Speed(f, 2, _ => { }, CancellationToken.None),
                    "tool:slow" => Media.Speed(f, 0.25, _ => { }, CancellationToken.None),
                    "tool:vcrop" => Media.Crop(f, new NormRect(0.1, 0.1, 0.5, 0.6), _ => { }, CancellationToken.None),
                    "tool:split" => string.Join(" | ", Media.Split(f, 1.5, _ => { }, CancellationToken.None)),
                    "tool:vredact" => Media.Redact(f, [(new NormRect(0.05, 0.05, 0.3, 0.3), RedactStyle.Box), (new NormRect(0.4, 0.4, 0.3, 0.3), RedactStyle.Blur), (new NormRect(0.6, 0.1, 0.3, 0.25), RedactStyle.Pixelate)], _ => { }, CancellationToken.None),
                    "tool:normalize" => Media.Normalize(f, _ => { }, CancellationToken.None),
                    "tool:mono" => Media.Channels(f, "mono", _ => { }, CancellationToken.None),
                    "tool:swap" => Media.Channels(f, "swap", _ => { }, CancellationToken.None),
                    "tool:waveimg" => Media.Visualize(f, false, "F27C38", _ => { }, CancellationToken.None),
                    "tool:wavevid" => Media.Visualize(f, true, "F27C38", _ => { }, CancellationToken.None),
                    "tool:bleep" => Media.Bleep(f, 0.5, 1.5, _ => { }, CancellationToken.None),
                    "tool:organize" => PdfTools.Organize(f, [(1, 90), (0, 0)]),
                    "tool:unpack" => Archives.Extract(f, CancellationToken.None),
                    _ => throw new ArgumentException("unknown tool " + target),
                });
            }
            foreach (var o in outputs) Console.WriteLine("  → " + o);
            return 0;
        }
        var result = Converter.Run(files, target, (_, _) => { }, CancellationToken.None);
        foreach (var o in result.Outputs) Console.WriteLine($"  → {o} ({new FileInfo(o).Length:N0} bytes)");
        foreach (var e in result.Errors) Console.WriteLine($"  ✗ {e}");
        return result.Errors.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Converts every file in <paramref name="folder"/> to every format its wheel offers and checks
    /// each result really opens (decodes, parses or plays). Exit code 0 only if all pass.
    /// </summary>
    static int Matrix(string folder)
    {
        int pass = 0, fail = 0;
        foreach (var file in Directory.GetFiles(folder).Concat(Directory.GetDirectories(folder)).OrderBy(f => f).ToList())
        {
            var items = Formats.Items(WheelMode.Convert, [file]);
            Console.WriteLine($"{Path.GetFileName(file)}  wheel: {string.Join(" ", items.Select(i => i.Id == Formats.SameFormat ? "[" + i.Label + "=copy/edit]" : i.Label))}");
            foreach (var target in Formats.ConvertTargets([file]))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var outputs = Converter.ConvertOne(file, target, _ => { }, CancellationToken.None);
                    if (outputs.Count == 0) throw new IOException("no output");
                    foreach (var o in outputs) Verify(o);
                    pass++;
                    Console.WriteLine($"   ok   → {target,-5} {outputs.Count} file(s), {new FileInfo(outputs[0]).Length:N0} bytes, {watch.ElapsedMilliseconds} ms");
                }
                catch (Exception e)
                {
                    fail++;
                    Console.WriteLine($"   FAIL → {target,-5} {Converter.Friendly(e)}");
                }
            }
        }
        Console.WriteLine($"{pass} passed, {fail} failed");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>Opens a result the way its format demands; throws if it's broken.</summary>
    static void Verify(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0) throw new IOException("empty output");
        var ext = Formats.Ext(path);
        switch (ext)
        {
            case "png" or "jpg" or "webp" or "tiff" or "avif" or "bmp" or "gif" or "ico":
                using (var image = new ImageMagick.MagickImage()) { image.Ping(path); if (image.Width == 0) throw new IOException("image doesn't decode"); }
                break;
            case "heic":
                var head = new byte[12];
                using (var fs = File.OpenRead(path)) fs.ReadExactly(head);
                if (System.Text.Encoding.ASCII.GetString(head, 4, 4) != "ftyp") throw new IOException("not a HEIF file");
                break;
            case "pdf":
                using (var pdf = UglyToad.PdfPig.PdfDocument.Open(path)) if (pdf.NumberOfPages == 0) throw new IOException("PDF has no pages");
                break;
            case "zip" or "tar" or "tgz" or "7z" or "rar":
                // An archive is good if it extracts to something.
                var check = Path.Combine(Output.WorkFolder, "verify-" + Guid.NewGuid().ToString("N"));
                var copy = Path.Combine(check, Path.GetFileName(path));
                Directory.CreateDirectory(check);
                try
                {
                    File.Copy(path, copy);
                    var extracted = Archives.Extract(copy, CancellationToken.None);
                    if (!Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories).Any()) throw new IOException("archive is empty");
                }
                finally { try { Directory.Delete(check, true); } catch { } }
                break;
            case "epub":
                if (!Epub.ReadDoc(path).HasText) throw new IOException("e-book has no text");
                break;
            case "docx" or "xlsx" or "pptx" or "odt" or "ods" or "odp":
                using (var zip = System.IO.Compression.ZipFile.OpenRead(path))
                {
                    if (zip.Entries.Count == 0) throw new IOException("empty package");
                    if (ext == "docx" && zip.GetEntry("word/document.xml") == null) throw new IOException("not a Word document");
                }
                if (ext == "xlsx" && Tabular.ReadXlsx(path).Count == 0) throw new IOException("workbook has no rows");
                break;
            case "mp3" or "wav" or "m4a" or "aac" or "flac" or "ogg" or "opus" or "aiff" or "wma" or "mp4" or "mov" or "mkv" or "webm" or "avi" or "wmv":
                var media = Media.Probe(path);
                if (media.Duration <= 0 || !(media.HasAudio || media.HasVideo)) throw new IOException("media doesn't play");
                break;
            case "json":
                using (System.Text.Json.JsonDocument.Parse(File.ReadAllText(path))) { }
                break;
            case "rtf":
                if (!File.ReadAllText(path).StartsWith(@"{\rtf")) throw new IOException("not RTF");
                break;
            default:
                if (File.ReadAllText(path).Trim().Length == 0) throw new IOException("no text");
                break;
        }
    }

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
}

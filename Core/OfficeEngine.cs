using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace FileWheel.Core;

/// <summary>Which Microsoft Office apps are installed (their automation gives the most faithful conversions).</summary>
public static class MsOffice
{
    static readonly ConcurrentDictionary<string, bool> installed = new();

    public static bool Word => Installed("Winword.exe");
    public static bool Excel => Installed("excel.exe");
    public static bool PowerPoint => Installed("powerpnt.exe");

    static bool Installed(string exe) => installed.GetOrAdd(exe, e =>
    {
        // Tests can pretend Office isn't here, to check the built-in fallbacks.
        if (Environment.GetEnvironmentVariable("FILEWHEEL_NO_OFFICE") == "1") return false;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + e);
            return key?.GetValue(null) is string path && File.Exists(path.Trim('"'));
        }
        catch { return false; }
    });
}

/// <summary>
/// Converts Office documents with Microsoft Office when it's installed, otherwise LibreOffice.
/// Office is driven from a separate PowerShell process so a stuck Office can always be stopped.
/// </summary>
public static class OfficeEngine
{

    /// <summary>Writes <paramref name="source"/> converted to <paramref name="target"/> at <paramref name="output"/>.</summary>
    public static void Convert(string source, string target, string output, CancellationToken ct)
    {
        // A PDF goes to Word, which rebuilds it as an editable document (layout, tables, pictures).
        var family = Formats.Ext(source) == "pdf" ? "word" : Formats.OfficeFamily(Formats.Ext(source));
        bool ms = family switch { "word" => MsOffice.Word, "sheet" => MsOffice.Excel, _ => MsOffice.PowerPoint };
        if (ms) RunOffice(family, source, output, FormatCode(family, target), ct);
        else LibreOfficeConvert(source, target, output, ct);
        if (!File.Exists(output) || new FileInfo(output).Length == 0) throw new IOException("Office didn't produce a file.");
    }

    /// <summary>A temp copy of <paramref name="source"/> as <paramref name="target"/>; the caller deletes it.</summary>
    public static string ToTemp(string source, string target, CancellationToken ct)
    {
        var temp = Path.Combine(Output.WorkFolder, Guid.NewGuid().ToString("N") + "." + target);
        Convert(source, target, temp, ct);
        return temp;
    }

    /// <summary>Every slide as a 1920-px-wide picture, saved beside the presentation as "Name Slide 01.png".</summary>
    public static List<string> SlidesToImages(string source, string ext, Action<double> progress, CancellationToken ct)
    {
        var folder = Path.Combine(Output.WorkFolder, "slides-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            RunScript(SlidesScript, [Path.GetFullPath(source), folder, ext.ToUpperInvariant() == "JPG" ? "JPG" : "PNG"], ct, TimeoutFor(source));
            var slides = Directory.GetFiles(folder).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            if (slides.Count == 0) throw new IOException("PowerPoint didn't export any slides.");
            string pad = new('0', Math.Max(2, slides.Count.ToString().Length));
            var outputs = new List<string>();
            for (int i = 0; i < slides.Count; i++)
            {
                var slide = slides[i];
                outputs.Add(Output.Save(source, " Slide " + (i + 1).ToString(pad), ext, temp => File.Copy(slide, temp, true)));
                progress((i + 1.0) / slides.Count);
            }
            return outputs;
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }

    static int FormatCode(string family, string target) => (family, target) switch
    {
        ("word", "pdf") => 17, // wdFormatPDF
        ("word", "docx") => 16, // wdFormatXMLDocument
        ("word", "rtf") => 6, // wdFormatRTF
        ("word", "odt") => 23, // wdFormatOpenDocumentText
        ("sheet", "pdf") => 999, // ExportAsFixedFormat (no leading "-": PowerShell would read it as a switch)
        ("sheet", "xlsx") => 51, // xlOpenXMLWorkbook
        ("sheet", "ods") => 60, // xlOpenDocumentSpreadsheet
        ("slides", "pdf") => 32, // ppSaveAsPDF
        ("slides", "pptx") => 24, // ppSaveAsOpenXMLPresentation
        ("slides", "odp") => 35, // ppSaveAsOpenDocumentPresentation
        _ => throw new IOException($"Office can't save {family} files as {target.ToUpperInvariant()}."),
    };

    static void RunOffice(string family, string source, string output, int code, CancellationToken ct)
    {
        var script = family switch { "word" => WordScript, "sheet" => ExcelScript, _ => PowerPointScript };
        string[] args = [Path.GetFullPath(source), Path.GetFullPath(output), code.ToString()];
        try { RunScript(script, args, ct, TimeoutFor(source)); }
        catch (IOException e) when (IsPassword(e.Message)) { throw new IOException(Friendly(e.Message)); }
        catch (IOException e) when (!ct.IsCancellationRequested && !e.Message.Contains("too long"))
        {
            // Often just the previous Office copy still closing: give it a moment and try once more.
            Thread.Sleep(3000);
            try { RunScript(script, args, ct, TimeoutFor(source)); }
            catch (IOException again) { throw new IOException(Friendly(again.Message)); }
        }
    }

    static bool IsPassword(string message) => message.Contains("password", StringComparison.OrdinalIgnoreCase);

    /// <summary>Office's own error, without PowerShell's wrapping ("Exception calling "Open"… : "the reason"").</summary>
    static string Friendly(string message)
    {
        if (IsPassword(message)) return "This file is password-protected — open it in Office and save a copy without the password.";
        var inner = System.Text.RegularExpressions.Regex.Match(message, "\"([^\"]{8,})\"\\s*$");
        var detail = (inner.Success ? inner.Groups[1].Value : message).Trim().TrimEnd('.');
        return $"Office couldn't convert this file ({detail}).";
    }

    /// <summary>Big workbooks become hundreds of PDF pages: allow 4 minutes plus 4 per MB (at most 30).</summary>
    static TimeSpan TimeoutFor(string source)
    {
        double mb = File.Exists(source) ? new FileInfo(source).Length / 1048576.0 : 0;
        return TimeSpan.FromMinutes(Math.Min(30, 4 + 4 * mb));
    }

    static void RunScript(string script, string[] args, CancellationToken ct, TimeSpan timeout)
    {
        var file = Path.Combine(Output.WorkFolder, Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, script, new UTF8Encoding(true));
        int officeProcess = 0;
        using var done = new CancellationTokenSource();
        // Office pops up its own "Publishing…" progress box even when hidden; keep it off the screen.
        var watcher = Task.Run(async () =>
        {
            while (!done.IsCancellationRequested)
            {
                if (officeProcess != 0) HideProgressBoxes(officeProcess);
                try { await Task.Delay(50, done.Token); } catch (OperationCanceledException) { }
            }
        });
        try
        {
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            Processes.Run(File.Exists(powershell) ? powershell : "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", file, .. args], ct, timeout,
                line => { if (line.StartsWith("OFFICEPID=", StringComparison.Ordinal) && int.TryParse(line.AsSpan(10), out var id)) officeProcess = id; },
                firstErrorLine: true);
        }
        finally
        {
            done.Cancel();
            try { watcher.Wait(500); } catch { }
            Output.TryDelete(file);
            if (officeProcess != 0) EndOffice(officeProcess);
        }
    }

    /// <summary>Moves Office's visible "Publishing…" progress windows far off-screen.</summary>
    static void HideProgressBoxes(int processId)
    {
        EnumWindows((hwnd, _) =>
        {
            if (GetWindowThreadProcessId(hwnd, out var pid) != 0 && pid == processId && IsWindowVisible(hwnd))
            {
                var title = new StringBuilder(64);
                GetWindowText(hwnd, title, 64);
                if (title.ToString().StartsWith("Publishing", StringComparison.OrdinalIgnoreCase))
                    SetWindowPos(hwnd, IntPtr.Zero, -32000, -32000, 0, 0, 0x0001 | 0x0004 | 0x0010); // NOSIZE | NOZORDER | NOACTIVATE
            }
            return true;
        }, IntPtr.Zero);
    }

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// Office runs as its own COM server, so stopping the script doesn't stop it. Each script reports
    /// the Office process it started; give it a moment to quit on its own, then end it.
    /// </summary>
    static void EndOffice(int id)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(id);
            if (!process.WaitForExit(5000)) process.Kill();
        }
        catch { /* already gone */ }
    }

    // Each script opens the file read-only and invisibly, saves, and always closes what it opened. It
    // first prints the id of the Office process it started (OFFICEPID=…) so that one can be ended if
    // anything hangs. PowerPoint is a single shared instance: if it was already running, it's left alone.

    const string WordScript = """
        param([string]$src, [string]$out, [int]$fmt)
        $ErrorActionPreference = 'Stop'
        $before = @(Get-Process WINWORD -ErrorAction SilentlyContinue | ForEach-Object Id)
        $app = New-Object -ComObject Word.Application
        $mine = Get-Process WINWORD -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id } | Select-Object -First 1
        if ($mine) { [Console]::Out.WriteLine('OFFICEPID=' + $mine.Id); [Console]::Out.Flush() }
        $no = 0; $false_ = $false; $true_ = $true; $nopass = 'x'
        try {
            $app.Visible = $false
            $app.DisplayAlerts = 0
            # Word's automation takes these by reference. A dummy password makes protected files fail
            # straight away instead of waiting on an invisible password prompt.
            $doc = $app.Documents.Open([ref]$src, [ref]$false_, [ref]$true_, [ref]$false_, [ref]$nopass)
            try { $doc.SaveAs2([ref]$out, [ref]$fmt) } finally { $doc.Close([ref]$no) }
        } finally {
            $app.Quit([ref]$no)
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($app)
        }
        """;

    const string ExcelScript = """
        param([string]$src, [string]$out, [int]$fmt)
        $ErrorActionPreference = 'Stop'
        $before = @(Get-Process EXCEL -ErrorAction SilentlyContinue | ForEach-Object Id)
        $app = New-Object -ComObject Excel.Application
        $mine = Get-Process EXCEL -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id } | Select-Object -First 1
        if ($mine) { [Console]::Out.WriteLine('OFFICEPID=' + $mine.Id); [Console]::Out.Flush() }
        try {
            $app.Visible = $false
            $app.DisplayAlerts = $false
            $app.ScreenUpdating = $false
            $app.Interactive = $false
            $book = $app.Workbooks.Open($src, 0, $true, [Type]::Missing, 'x')
            try {
                if ($fmt -eq 999) {
                    # Each sheet one page wide (landscape when it's wide), so the PDF reads like the sheet
                    # instead of splitting columns across extra pages.
                    $app.PrintCommunication = $false
                    foreach ($sheet in $book.Worksheets) {
                        try {
                            $setup = $sheet.PageSetup
                            $setup.Zoom = $false
                            $setup.FitToPagesWide = 1
                            $setup.FitToPagesTall = $false
                            if ($sheet.UsedRange.Columns.Count -gt 7) { $setup.Orientation = 2 }
                        } catch { }
                    }
                    $app.PrintCommunication = $true
                    $book.ExportAsFixedFormat(0, $out)
                } else { $book.SaveAs($out, $fmt) }
            } finally { $book.Close($false) }
        } finally {
            $app.Quit()
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($app)
        }
        """;

    const string PowerPointScript = """
        param([string]$src, [string]$out, [int]$fmt)
        $ErrorActionPreference = 'Stop'
        $before = @(Get-Process POWERPNT -ErrorAction SilentlyContinue | ForEach-Object Id)
        $wasRunning = $before.Count -gt 0
        $app = New-Object -ComObject PowerPoint.Application
        $mine = Get-Process POWERPNT -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id } | Select-Object -First 1
        if ($mine) { [Console]::Out.WriteLine('OFFICEPID=' + $mine.Id); [Console]::Out.Flush() }
        try {
            $pres = $app.Presentations.Open($src, -1, 0, 0)
            try { $pres.SaveAs($out, $fmt) } finally { $pres.Close() }
        } finally {
            if (-not $wasRunning) { $app.Quit() }
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($app)
        }
        """;

    const string SlidesScript = """
        param([string]$src, [string]$folder, [string]$kind)
        $ErrorActionPreference = 'Stop'
        $before = @(Get-Process POWERPNT -ErrorAction SilentlyContinue | ForEach-Object Id)
        $wasRunning = $before.Count -gt 0
        $app = New-Object -ComObject PowerPoint.Application
        $mine = Get-Process POWERPNT -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id } | Select-Object -First 1
        if ($mine) { [Console]::Out.WriteLine('OFFICEPID=' + $mine.Id); [Console]::Out.Flush() }
        try {
            $pres = $app.Presentations.Open($src, -1, 0, 0)
            try {
                $w = 1920
                $h = [int]($w * $pres.PageSetup.SlideHeight / $pres.PageSetup.SlideWidth)
                $i = 0
                foreach ($slide in $pres.Slides) {
                    $i++
                    $slide.Export((Join-Path $folder ('Slide{0:D4}.{1}' -f $i, $kind.ToLower())), $kind, $w, $h)
                }
            } finally { $pres.Close() }
        } finally {
            if (-not $wasRunning) { $app.Quit() }
            [void][Runtime.InteropServices.Marshal]::ReleaseComObject($app)
        }
        """;

    static void LibreOfficeConvert(string source, string target, string output, CancellationToken ct)
    {
        var engine = Formats.LibreOffice ?? throw new IOException("Install Microsoft Office or LibreOffice to convert office documents.");
        var folder = Path.Combine(Output.WorkFolder, "office-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Processes.Run(engine,
                ["-env:UserInstallation=" + new Uri(Path.Combine(folder, "profile")).AbsoluteUri, "--headless", "--norestore", "--convert-to", target, "--outdir", folder, source],
                ct, TimeoutFor(source));
            var produced = Directory.GetFiles(folder, "*." + target).FirstOrDefault() ?? throw new IOException("The office converter didn't produce a file.");
            File.Copy(produced, output, true);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
    }
}

/// <summary>The text of a .docx as Markdown or plain text (see <see cref="DocxReader"/>).</summary>
public static class WordText
{
    public static string Read(string docx, bool markdown)
    {
        var doc = DocxReader.Read(docx);
        if (!doc.HasText) throw new IOException("This Word document has no text.");
        return markdown ? doc.ToMarkdown() : doc.ToPlainText();
    }
}

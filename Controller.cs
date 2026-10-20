using System.Diagnostics;
using System.Windows;
using FileWheel.Core;
using FileWheel.Native;
using FileWheel.UI;
using FileWheel.UI.Editors;

namespace FileWheel;

/// <summary>Turns a wheel choice into work: a conversion with a progress toast, an editor panel, or a file action.</summary>
public static class Controller
{
    public static void Handle(IReadOnlyList<string> files, WheelMode mode, WheelItem item, POINT at)
    {
        try
        {
            switch (mode)
            {
                case WheelMode.Convert: Convert(files, item, at); break;
                case WheelMode.Tools: OpenTool(files, Enum.Parse<Tool>(item.Id), at); break;
                case WheelMode.Share: UI.Share.Send(files, item, at); break;
                case WheelMode.Same: SameFormat(files, item, at); break;
                case WheelMode.Speed or WheelMode.Channels or WheelMode.Visualize: MediaOption(files, item, at); break;
                default: RunAction(files, Enum.Parse<FileAction>(item.Id), at); break;
            }
        }
        catch (Exception e)
        {
            Program.Log(e.ToString());
            var toast = new ToastWindow("Something went wrong", null, at);
            toast.Present();
            toast.Fail("Something went wrong", Converter.Friendly(e));
        }
    }

    static void Convert(IReadOnlyList<string> files, WheelItem item, POINT at)
    {
        if (item.Id == Formats.SameFormat)
        {
            // Normally the wheel opens the Copy / Edit ring itself; this is the fallback.
            App.Current?.OpenWheelAt(files, WheelMode.Same, at, back: () => App.Current?.OpenWheelAt(files, WheelMode.Convert, at));
            return;
        }
        int total = files.Count;
        _ = ToastWindow.Run(
            $"Converting to {item.Label}", at,
            (ct, report) =>
            {
                report(0.04, $"0 of {total} saved");
                return Converter.Run(files, item.Id, (f, done) => report(Math.Max(0.04, f), $"{done} of {total} saved"), ct);
            },
            result =>
            {
                if (result.Outputs.Count == 0 && result.Errors.Count > 0) throw new IOException(result.Errors[0].Split(": ", 2).Last());
                var text = $"{Math.Min(total, total - result.Errors.Count)} of {total} saved";
                if (result.Errors.Count > 0) text += $" · {result.Errors.Count} failed";
                return ($"Converted to {item.Label}", text, result.Outputs.FirstOrDefault());
            },
            "Couldn't convert");
    }

    /// <summary>The Copy / Rename ring for files dropped on their own format.</summary>
    static void SameFormat(IReadOnlyList<string> files, WheelItem item, POINT at)
    {
        if (item.Id == Formats.SameRename)
        {
            new RenamePanel(files, at).Present();
            return;
        }
        var sources = files.ToList();
        _ = ToastWindow.Run(sources.Count == 1 ? "Making a copy" : $"Copying {sources.Count} files", at,
            (ct, report) =>
            {
                var outputs = new List<string>();
                for (int i = 0; i < sources.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    outputs.Add(FileOps.Duplicate(sources[i]));
                    report((i + 1.0) / sources.Count, $"{i + 1} of {sources.Count} saved");
                }
                return outputs;
            },
            outputs => ("Copy saved", outputs.Count == 1 ? Path.GetFileName(outputs[0]) : $"{outputs.Count} copies saved", outputs.FirstOrDefault()),
            "Couldn't copy");
    }

    /// <summary>A choice from the Speed, Channels or Visualize ring: runs straight away with a progress toast.</summary>
    static void MediaOption(IReadOnlyList<string> files, WheelItem item, POINT at)
    {
        var parts = item.Id.Split(':', 2);
        var (heading, done, work) = parts[0] switch
        {
            "speed" => ($"Changing speed to {item.Label}", "Speed changed",
                (Func<string, Action<double>, CancellationToken, string>)((f, p, ct) => Media.Speed(f, double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture), p, ct))),
            "ch" => ($"Making {item.Hint?.ToLowerInvariant()}", "Channels changed",
                (f, p, ct) => Media.Channels(f, parts[1], p, ct)),
            _ => (parts[1] == "image" ? "Drawing the waveform" : "Making a waveform video", "Waveform saved",
                (f, p, ct) => Media.Visualize(f, parts[1] == "video", ColorHex(Theme.Current.Accent), p, ct)),
        };
        RunEach(files, heading, done, work, at);
    }

    static string ColorHex(System.Windows.Media.Color c) => $"{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Runs a per-file job behind a progress toast; the toast offers "Show in folder" for the first result.</summary>
    static void RunEach(IReadOnlyList<string> files, string heading, string done, Func<string, Action<double>, CancellationToken, string> work, POINT at)
    {
        _ = ToastWindow.Run(heading, at,
            (ct, report) =>
            {
                var outputs = new List<string>();
                for (int i = 0; i < files.Count; i++)
                {
                    int index = i;
                    outputs.Add(work(files[i], p => report((index + p) / files.Count, $"{(index + p) / files.Count:P0}"), ct));
                }
                return outputs;
            },
            outputs => (done, outputs.Count == 1 ? Path.GetFileName(outputs[0]) : $"{outputs.Count} files saved", outputs.FirstOrDefault()),
            "Couldn't finish");
    }

    public static void OpenTool(IReadOnlyList<string> files, Tool tool, POINT at)
    {
        var kind = Formats.Kind(files[0]);
        if (Formats.RingFor(tool) is { } ring)
        {
            // Normally the wheel opens these rings itself; this is the fallback.
            App.Current?.OpenWheelAt(files, ring, at, back: () => App.Current?.OpenWheelAt(files, WheelMode.Tools, at));
            return;
        }
        switch (tool)
        {
            case Tool.Normalize:
                RunEach(files, "Normalizing loudness", "Normalized", Media.Normalize, at);
                return;
            case Tool.Unpack:
                RunEach(files, files.Count == 1 ? "Extracting" : $"Extracting {files.Count} archives", "Extracted", (f, _, ct) => Archives.Extract(f, ct), at);
                return;
        }
        GlassPanel? panel = tool switch
        {
            Tool.Crop => new CropEditor(files),
            Tool.Redact => new RedactEditor(files[0]),
            Tool.Split when kind == FileKind.Video => new TrimPanel(files[0], TimeTool.Split),
            Tool.Snapshot => new TrimPanel(files[0], TimeTool.Snapshot),
            Tool.Bleep => new TrimPanel(files[0], TimeTool.Bleep),
            Tool.Organize => new PdfOrganizePanel(files[0]),
            Tool.AddBackground => new BackgroundEditor(files),
            Tool.Compress when Formats.Kind(files[0]) == FileKind.Image => new CompressEditor(files),
            Tool.Compress when Formats.Kind(files[0]) == FileKind.Pdf => new PdfCompressPanel(files),
            Tool.Compress => new MediaCompressPanel(files),
            Tool.Edit => new AdjustEditor(files),
            Tool.Annotate => new AnnotateEditor(files[0]),
            Tool.Metadata => new MetadataPanel(files),
            Tool.Rotate => new PdfRotatePanel(files),
            Tool.Extract => new PdfExtractPanel(files[0]),
            Tool.Split => new PdfSplitPanel(files),
            Tool.Merge => new PdfMergePanel(files),
            Tool.Trim => new TrimPanel(files[0]),
            _ => null,
        };
        if (panel != null)
        {
            panel.Anchor = at;
            panel.Present();
            return;
        }
        if (tool == Tool.Mute)
        {
            _ = ToastWindow.Run("Removing audio", at,
                (ct, report) => files.Select(f => Media.Mute(f, p => report(p, "Working…"), ct)).ToList(),
                outputs => ("Audio removed", $"{outputs.Count} of {files.Count} saved", outputs.FirstOrDefault()),
                "Couldn't remove audio");
        }
    }

    static void RunAction(IReadOnlyList<string> files, FileAction action, POINT at)
    {
        switch (action)
        {
            case FileAction.CopyPath:
                Clipboard.SetText(string.Join(Environment.NewLine, files.Select(f => files.Count > 1 || f.Contains(' ') ? $"\"{f}\"" : f)));
                ToastWindow.Flash(files.Count == 1 ? "Path copied" : $"{files.Count} paths copied", Shorten(files[0]), at);
                break;
            case FileAction.Terminal:
                OpenTerminal(Directory.Exists(files[0]) ? files[0] : Path.GetDirectoryName(files[0])!);
                break;
            case FileAction.Float:
                foreach (var f in files.Take(4)) new FloatWindow(f, at).Present();
                break;
            case FileAction.Share:
                // Normally the wheel swaps to the app ring itself; this is the fallback.
                App.Current?.OpenWheelAt(files, WheelMode.Share, at, back: () => App.Current?.OpenWheelAt(files, WheelMode.Actions, at));
                break;
            case FileAction.Copy:
            case FileAction.Move:
                CopyOrMove(files, action == FileAction.Move, at);
                break;
        }
    }

    static string Shorten(string path) => path.Length <= 46 ? path : "…" + path[^45..];

    static void OpenTerminal(string folder)
    {
        try
        {
            var wt = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
            wt.ArgumentList.Add("-d");
            wt.ArgumentList.Add(folder);
            Process.Start(wt);
        }
        catch
        {
            Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = folder });
        }
    }

    static void CopyOrMove(IReadOnlyList<string> files, bool move, POINT at)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = move ? "Move to…" : "Copy to…" };
        if (picker.ShowDialog() != true) return;
        var destination = picker.FolderName;
        string verb = move ? "Moving" : "Copying";
        _ = ToastWindow.Run($"{verb} {(files.Count == 1 ? Path.GetFileName(files[0]) : files.Count + " items")}", at,
            (ct, report) =>
            {
                var outputs = new List<string>();
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    outputs.Add(FileOps.Transfer(files[i], destination, move));
                    report((i + 1.0) / files.Count, $"{i + 1} of {files.Count} done");
                }
                return outputs;
            },
            outputs => (move ? "Moved" : "Copied", $"{outputs.Count} of {files.Count} to {Path.GetFileName(destination.TrimEnd('\\'))}", outputs.FirstOrDefault()),
            move ? "Couldn't move" : "Couldn't copy");
    }
}

public static class FileOps
{
    /// <summary>
    /// Renames each file in place, keeping its extension. One file gets <paramref name="name"/>; several
    /// get "name 1", "name 2"… in the order given. Never overwrites another file.
    /// </summary>
    public static List<string> Rename(IReadOnlyList<string> files, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new IOException("Type a name first.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException("A name can't contain \\ / : * ? \" < > |");
        var renamed = new List<string>();
        for (int i = 0; i < files.Count; i++)
        {
            var source = Path.GetFullPath(files[i]);
            var dir = Path.GetDirectoryName(source)!;
            var stem = files.Count == 1 ? name : $"{name} {i + 1}";
            var target = Path.Combine(dir, stem + Path.GetExtension(source));
            if (string.Equals(source, target, StringComparison.Ordinal)) { renamed.Add(source); continue; }
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            {
                // Only the capitals change: go through a temporary name so Windows sees a change.
                var step = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".tmp");
                File.Move(source, step);
                File.Move(step, target);
            }
            else
            {
                if (File.Exists(target) || Directory.Exists(target))
                {
                    if (files.Count == 1) throw new IOException($"“{Path.GetFileName(target)}” already exists here.");
                    target = Output.AvailablePath(dir, stem, Path.GetExtension(source).TrimStart('.'));
                }
                File.Move(source, target, overwrite: false);
            }
            renamed.Add(target);
        }
        return renamed;
    }

    /// <summary>"Report.pdf" → "Report copy.pdf" (or "Report copy (2).pdf") in the same folder, never overwriting.</summary>
    public static string Duplicate(string source)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(source))!;
        var target = Output.AvailablePath(dir, Path.GetFileNameWithoutExtension(source) + " copy", Path.GetExtension(source).TrimStart('.'));
        File.Copy(source, target, overwrite: false);
        return target;
    }

    /// <summary>Copies or moves a file or folder into <paramref name="folder"/>, never overwriting.</summary>
    public static string Transfer(string source, string folder, bool move)
    {
        bool isDir = Directory.Exists(source);
        var name = Path.GetFileName(source.TrimEnd('\\'));
        var stem = isDir ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isDir ? "" : Path.GetExtension(name);
        var target = Path.Combine(folder, name);
        for (int n = 2; File.Exists(target) || Directory.Exists(target); n++) target = Path.Combine(folder, $"{stem} ({n}){ext}");

        if (string.Equals(Path.GetFullPath(source).TrimEnd('\\'), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Can't put a folder inside itself.");
        if (isDir)
        {
            if (move && Path.GetPathRoot(source) == Path.GetPathRoot(target)) Directory.Move(source, target);
            else
            {
                CopyDirectory(source, target);
                if (move) Directory.Delete(source, true);
            }
        }
        else if (move) File.Move(source, target, false);
        else File.Copy(source, target, false);
        return target;
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), false);
        foreach (var dir in Directory.GetDirectories(from)) CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}

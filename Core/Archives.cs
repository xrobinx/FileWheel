using System.Formats.Tar;
using System.IO.Compression;

namespace FileWheel.Core;

/// <summary>
/// ZIP, TAR, TAR.GZ, GZ, 7Z and RAR: extract them, convert between them, or pack a folder into one.
/// ZIP/TAR/GZ use .NET itself; 7Z uses Windows' built-in tar (libarchive); RAR uses WinRAR when it's
/// installed (it can also create RAR files), otherwise Windows' tar reads it.
/// </summary>
public static class Archives
{
    public static readonly string[] InputExts = ["zip", "tar", "tgz", "gz", "7z", "rar"];

    static string? WinRar(string exe)
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var path = Path.Combine(root, "WinRAR", exe);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static string? RarExe => WinRar("Rar.exe");
    static string? UnRarExe => WinRar("UnRAR.exe") ?? RarExe;

    /// <summary>Windows 10 1803+ ships bsdtar (libarchive): it reads 7Z and RAR and writes 7Z.</summary>
    static string? SystemTar
    {
        get
        {
            var path = Path.Combine(Environment.SystemDirectory, "tar.exe");
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>The archive formats this PC can write, in wheel order.</summary>
    public static List<string> Targets()
    {
        var list = new List<string> { "zip", "tar", "tgz" };
        if (SystemTar != null) list.Add("7z");
        if (RarExe != null) list.Add("rar");
        return list;
    }

    /// <summary>"Photos.tar.gz" → "Photos"; "Report.zip" → "Report"; a folder keeps its whole name.</summary>
    public static string Stem(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (Directory.Exists(path)) return name;
        foreach (var double_ in new[] { ".tar.gz", ".tar.bz2", ".tar.xz" })
            if (name.EndsWith(double_, StringComparison.OrdinalIgnoreCase)) return name[..^double_.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    public static string FileExt(string target) => target == "tgz" ? "tar.gz" : target;

    // ---------- Extract ----------

    /// <summary>Extracts next to the archive into a new folder named after it (never merging into an existing one).</summary>
    public static string Extract(string archive, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(archive))!;
        var ext = Formats.Ext(archive);
        if (ext == "gz")
        {
            // A single compressed file: "notes.txt.gz" → "notes.txt".
            var inner = Path.GetFileNameWithoutExtension(archive);
            var target = Output.AvailablePath(dir, Path.GetFileNameWithoutExtension(inner), Path.GetExtension(inner).TrimStart('.'));
            var temp = Path.Combine(Output.WorkFolder, Guid.NewGuid().ToString("N"));
            try
            {
                using (var input = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
                using (var output = File.Create(temp)) input.CopyTo(output);
                File.Move(temp, target, overwrite: false);
                return target;
            }
            finally { Output.TryDelete(temp); }
        }

        var folder = AvailableFolder(dir, Stem(archive));
        // Unpack beside the final folder first, so nothing half-finished ever shows under the real name.
        var partial = Path.Combine(dir, "." + Path.GetFileName(folder) + ".extracting-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(partial);
        try
        {
            UnpackInto(archive, partial, ct);
            Directory.Move(partial, folder);
            return folder;
        }
        catch
        {
            try { Directory.Delete(partial, true); } catch { }
            throw;
        }
    }

    static string AvailableFolder(string dir, string name)
    {
        var candidate = Path.Combine(dir, name);
        for (int n = 2; Directory.Exists(candidate) || File.Exists(candidate); n++) candidate = Path.Combine(dir, $"{name} ({n})");
        return candidate;
    }

    /// <summary>Unpacks every entry of <paramref name="archive"/> into the empty folder <paramref name="into"/>.</summary>
    static void UnpackInto(string archive, string into, CancellationToken ct)
    {
        switch (Formats.Ext(archive))
        {
            case "zip":
                ZipFile.ExtractToDirectory(archive, into, overwriteFiles: false);
                break;
            case "tar":
                TarFile.ExtractToDirectory(archive, into, overwriteFiles: false);
                break;
            case "tgz":
                using (var gz = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
                    TarFile.ExtractToDirectory(gz, into, overwriteFiles: false);
                break;
            case "gz":
                var inner = Path.Combine(into, Path.GetFileNameWithoutExtension(archive));
                using (var input = new GZipStream(File.OpenRead(archive), CompressionMode.Decompress))
                using (var output = File.Create(inner)) input.CopyTo(output);
                break;
            case "rar" when UnRarExe is { } unrar:
                // x = keep folders, -o- = never overwrite, -p- = don't ask for a password (fail instead), -y = yes to all.
                Processes.Run(unrar, ["x", "-o-", "-p-", "-y", "-idq", archive, into.TrimEnd('\\') + "\\"], ct, TimeSpan.FromHours(1));
                break;
            default:
                var tar = SystemTar ?? throw new IOException("Windows can't open this kind of archive on this PC.");
                // bsdtar refuses absolute paths and "..", so nothing lands outside the folder.
                Processes.Run(tar, ["-xf", archive, "-C", into], ct, TimeSpan.FromHours(1));
                break;
        }
        if (!Directory.EnumerateFileSystemEntries(into).Any()) throw new IOException("The archive is empty.");
    }

    // ---------- Pack / convert ----------

    /// <summary>
    /// Packs a folder (with the folder itself as the top entry) or re-packs an archive's contents as
    /// <paramref name="target"/> (zip, tar, tgz, 7z, rar). Saved beside the source.
    /// </summary>
    public static string Pack(string source, string target, CancellationToken ct)
    {
        bool folder = Directory.Exists(source);
        string? scratch = null;
        try
        {
            string contents;
            if (folder) contents = Path.GetFullPath(source).TrimEnd('\\');
            else
            {
                scratch = Path.Combine(Output.WorkFolder, "unpack-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(scratch);
                UnpackInto(source, scratch, ct);
                contents = scratch;
            }
            var temp = Path.Combine(Output.WorkFolder, Guid.NewGuid().ToString("N") + "." + FileExt(target));
            try
            {
                Write(contents, includeFolder: folder, target, temp, ct);
                if (!File.Exists(temp) || new FileInfo(temp).Length == 0) throw new IOException("Nothing was produced.");
                return Output.PublishNamed(temp, Path.GetDirectoryName(Path.GetFullPath(source.TrimEnd('\\')))!, Stem(source), FileExt(target));
            }
            finally { Output.TryDelete(temp); }
        }
        finally
        {
            if (scratch != null) try { Directory.Delete(scratch, true); } catch { }
        }
    }

    static void Write(string folder, bool includeFolder, string target, string output, CancellationToken ct)
    {
        var parent = Path.GetDirectoryName(folder)!;
        var name = Path.GetFileName(folder);
        switch (target)
        {
            case "zip":
                ZipFile.CreateFromDirectory(folder, output, CompressionLevel.Optimal, includeBaseDirectory: includeFolder);
                break;
            case "tar":
                TarFile.CreateFromDirectory(folder, output, includeBaseDirectory: includeFolder);
                break;
            case "tgz":
                using (var file = File.Create(output))
                using (var gz = new GZipStream(file, CompressionLevel.Optimal))
                    TarFile.CreateFromDirectory(folder, gz, includeBaseDirectory: includeFolder);
                break;
            case "7z":
            {
                var tar = SystemTar ?? throw new IOException("Windows' tar isn't available to make 7Z files.");
                var args = new List<string> { "--format", "7zip", "-cf", output, "-C", includeFolder ? parent : folder };
                if (includeFolder) args.Add(name);
                else args.AddRange(Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName)!);
                Processes.Run(tar, args, ct, TimeSpan.FromHours(1));
                break;
            }
            case "rar":
            {
                var rar = RarExe ?? throw new IOException("Install WinRAR to make RAR files.");
                // -ep1: names relative to the folder given; -r: recurse; -idq: quiet.
                Processes.Run(rar, ["a", "-r", "-ep1", "-idq", "-y", output, includeFolder ? folder : Path.Combine(folder, "*")], ct, TimeSpan.FromHours(1));
                break;
            }
            default:
                throw new IOException($"Can't make {target.ToUpperInvariant()} archives.");
        }
    }
}

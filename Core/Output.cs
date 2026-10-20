namespace FileWheel.Core;

/// <summary>Saves results beside the source without ever overwriting an existing file.</summary>
public static class Output
{
    public static string WorkFolder
    {
        get
        {
            // The system temp folder: Office, run as its own process, can always read and write there.
            var dir = Path.Combine(Path.GetTempPath(), "FileWheel");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string AvailablePath(string directory, string stem, string ext)
    {
        var candidate = Path.Combine(directory, $"{stem}.{ext}");
        for (int n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
            candidate = Path.Combine(directory, $"{stem} ({n}).{ext}");
        return candidate;
    }

    /// <summary>
    /// Writes into a private temp file, then moves it next to <paramref name="source"/> as
    /// "<c>Name{suffix}.{ext}</c>". The desktop only ever sees the finished file.
    /// </summary>
    public static string Save(string source, string suffix, string ext, Action<string> write, string? directory = null)
    {
        var temp = Path.Combine(WorkFolder, $"{Guid.NewGuid():N}.{ext}");
        try
        {
            write(temp);
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                throw new IOException("Nothing was produced.");
            return Publish(temp, source, suffix, ext, directory);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public static string SaveBytes(string source, string suffix, string ext, byte[] bytes, string? directory = null) =>
        Save(source, suffix, ext, temp => File.WriteAllBytes(temp, bytes), directory);

    public static string Publish(string temp, string source, string suffix, string ext, string? directory = null)
    {
        var dir = directory ?? Path.GetDirectoryName(Path.GetFullPath(source.TrimEnd('\\', '/')))!;
        var name = source.TrimEnd('\\', '/');
        // A folder keeps its whole name ("My.Photos" stays "My.Photos").
        var stem = (Directory.Exists(name) ? Path.GetFileName(name) : Path.GetFileNameWithoutExtension(name)) + suffix;
        return PublishNamed(temp, dir, stem, ext);
    }

    /// <summary>Moves a finished temp file to "<c>dir\stem.ext</c>" (or "stem (2).ext"…), never overwriting.</summary>
    public static string PublishNamed(string temp, string dir, string stem, string ext)
    {
        while (true)
        {
            var target = AvailablePath(dir, stem, ext);
            try
            {
                File.Move(temp, target, overwrite: false);
                return target;
            }
            catch (IOException) when (File.Exists(target))
            {
                // Lost a race with another writer; pick the next free name.
            }
        }
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

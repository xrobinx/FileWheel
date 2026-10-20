using System.Text.Json;
using Microsoft.Win32;

namespace FileWheel;

public sealed class Settings
{
    public bool Paused { get; set; }

    /// <summary>"Glass" (takes on the colors behind it) or "Tangerine" (the orange look).</summary>
    public string Theme { get; set; } = "Glass";

    /// <summary>The apps on the Share ring, in order. Null: the messaging apps found on this PC.</summary>
    public List<SavedApp>? ShareApps { get; set; }

    public sealed class SavedApp
    {
        public string Name { get; set; } = "";
        /// <summary>The app's id in the Start menu's app list (shell:AppsFolder).</summary>
        public string Id { get; set; } = "";
    }

    static Settings? current;
    static string FilePath => Path.Combine(Program.DataFolder, "settings.json");

    public static Settings Current
    {
        get
        {
            if (current != null) return current;
            try { current = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)); } catch { }
            return current ??= new Settings();
        }
    }

    public static void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true })); } catch { }
    }

    // ---------- Windows integration ----------

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue("FileWheel") is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue("FileWheel", $"\"{Environment.ProcessPath}\" --background");
            else key.DeleteValue("FileWheel", false);
        }
    }

    static string SendToShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "FileWheel.lnk");

    public static bool InSendTo
    {
        get => File.Exists(SendToShortcut);
        set
        {
            if (!value) { try { File.Delete(SendToShortcut); } catch { } return; }
            var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("Windows Script Host is unavailable.");
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(SendToShortcut);
            link.TargetPath = Environment.ProcessPath;
            link.Description = "Open the FileWheel for these files";
            link.IconLocation = Environment.ProcessPath + ",0";
            link.Save();
        }
    }
}

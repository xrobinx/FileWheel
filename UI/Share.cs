using System.Collections.Specialized;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Imaging;
using FileWheel.Core;
using FileWheel.Native;

namespace FileWheel.UI;

/// <summary>
/// The Share ring: your chosen apps (by default the messaging and mail apps found on this PC), plus
/// Copy and Custom. Dropping files on an app copies them all, exactly like Explorer's Copy, and
/// opens the app — pick a chat and press Ctrl+V. Classic Outlook gets a new email with the file attached.
/// </summary>
public static class Share
{
    public const string Prefix = "share:";
    public const int MaxApps = 9;

    enum How { Paste, Attach }

    sealed record Target(string Key, string Name, string Id, How How, BitmapSource? Icon);

    // Display name in the Start menu → name on the wheel. First match wins, in this order around the ring.
    static readonly (string Match, string Label)[] Known =
    [
        ("WhatsApp", "WhatsApp"), ("Telegram", "Telegram"), ("Outlook", "Outlook"), ("Microsoft Teams", "Teams"),
        ("Teams", "Teams"), ("Discord", "Discord"), ("Slack", "Slack"), ("Messenger", "Messenger"), ("Signal", "Signal"),
        ("Gmail", "Gmail"), ("Instagram", "Instagram"), ("Zoom", "Zoom"), ("Skype", "Skype"), ("Thunderbird", "Thunderbird"),
        ("LINE", "LINE"), ("Viber", "Viber"), ("WeChat", "WeChat"), ("Mail", "Mail"),
    ];

    static readonly string[] NotApps = ["uninstall", "help", "readme", "release notes", "update", "setup", "settings", "add-in", "manual", "admin", "addin"];

    static List<Target>? targets;
    static readonly object gate = new();

    /// <summary>Finds the apps (and their icons) in the background so the first Share ring opens instantly.</summary>
    public static void Warm()
    {
        var thread = new Thread(() => { try { Load(); } catch (Exception e) { Program.Log("Share apps: " + e.Message); } })
        { IsBackground = true, Name = "FileWheel share apps" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>Saves the ring's apps (null goes back to the ones found automatically) and rebuilds it.</summary>
    public static void SaveChoice(List<Settings.SavedApp>? apps)
    {
        Settings.Current.ShareApps = apps;
        Settings.Save();
        lock (gate) targets = null;
    }

    /// <summary>The apps on the ring right now, as (name, id).</summary>
    public static List<Settings.SavedApp> CurrentChoice() =>
        Load().Select(t => new Settings.SavedApp { Name = t.Name, Id = t.Id }).ToList();

    /// <summary>The messaging and mail apps installed here, in ring order.</summary>
    public static List<Settings.SavedApp> Detect(IReadOnlyList<ShellApps.InstalledApp> apps)
    {
        var found = new List<Settings.SavedApp>();
        foreach (var (match, label) in Known)
        {
            if (found.Count >= MaxApps - 1) break;
            if (found.Any(t => t.Name == label)) continue;
            var app = apps
                .Where(a => Matches(a.Name, match) && !NotApps.Any(n => a.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))
                // Prefer classic Outlook (it can attach files directly), then the shortest name.
                .OrderBy(a => IsClassicOutlook(a.ParsingName) ? 0 : 1)
                .ThenBy(a => a.Name.Length)
                .FirstOrDefault();
            if (app != null) found.Add(new Settings.SavedApp { Name = label, Id = app.ParsingName });
        }
        return found;
    }

    static List<Target> Load()
    {
        lock (gate)
        {
            if (targets != null) return targets;
            var chosen = Settings.Current.ShareApps ?? Detect(ShellApps.List());
            targets = chosen.Take(MaxApps).Select((a, i) => new Target(
                "app" + i, a.Name, a.Id, IsClassicOutlook(a.Id) ? How.Attach : How.Paste, ShellApps.Icon(a.Id, 64))).ToList();
            Program.Log("Share apps: " + (targets.Count == 0 ? "none" : string.Join(", ", targets.Select(t => t.Name))));
            return targets;
        }
    }

    static bool Matches(string name, string match) =>
        name.Equals(match, StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(match + " ", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith(match + "(", StringComparison.OrdinalIgnoreCase);

    static bool IsClassicOutlook(string id) => id.EndsWith("OUTLOOK.EXE", StringComparison.OrdinalIgnoreCase);

    /// <summary>A name short enough for a slice: "Microsoft Teams (work or school)" → "Teams".</summary>
    public static string ShortName(string name)
    {
        var s = name;
        int paren = s.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0) s = s[..paren];
        if (s.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) && s.Length > 12) s = s[10..];
        if (s.Length > 11 && s.Contains(' ')) s = s[..s.IndexOf(' ')];
        return s;
    }

    /// <summary>The ring: your apps, then Copy and Custom (choose the apps).</summary>
    public static IReadOnlyList<WheelItem> Items(IReadOnlyList<string> files)
    {
        var list = new List<WheelItem>();
        foreach (var t in Load())
            list.Add(new WheelItem(Prefix + t.Key, ShortName(t.Name).ToUpperInvariant(), "app:" + t.Key, ShortName(t.Name)));
        list.Add(new WheelItem(Prefix + "copy", "COPY", "Copy", "Copy"));
        list.Add(new WheelItem(Prefix + "custom", "CUSTOM", "Plus", "Choose apps"));
        return list;
    }

    /// <summary>The caption under the dragged file while it's over a slice of the Share ring.</summary>
    public static string DropCaption(WheelItem item) => item.Id switch
    {
        Prefix + "copy" => "Copy to paste anywhere",
        Prefix + "custom" => "Choose the apps for this ring",
        _ => "Send with " + item.Hint,
    };

    /// <summary>An app's icon for the wheel, if it has one.</summary>
    public static BitmapSource? Icon(string key) => targets?.FirstOrDefault(t => t.Key == key)?.Icon;

    public static void Send(IReadOnlyList<string> files, WheelItem item, POINT at)
    {
        var key = item.Id[Prefix.Length..];
        string what = files.Count == 1 ? "File" : $"{files.Count} files";
        switch (key)
        {
            case "copy":
                CopyToClipboard(files);
                ToastWindow.Flash("Copied", $"{what} ready — paste anywhere with Ctrl+V", at);
                return;
            case "custom":
                new ShareAppsPanel(files, at).Present();
                return;
        }
        var target = Load().FirstOrDefault(t => t.Key == key) ?? throw new IOException("That app isn't on the Share ring anymore.");
        if (target.How == How.Attach && files.Count == 1 && OutlookPath() is { } outlook)
        {
            var start = new ProcessStartInfo(outlook) { UseShellExecute = false };
            start.ArgumentList.Add("/a");
            start.ArgumentList.Add(files[0]);
            Process.Start(start);
            ToastWindow.Flash("Opening Outlook", "A new email with the file attached", at);
            return;
        }
        CopyToClipboard(files);
        try { ShellApps.Launch(target.Id); }
        catch (Exception e)
        {
            Program.Log("Share launch failed: " + e.Message);
            ToastWindow.Flash("Couldn't open " + target.Name, $"{what} copied — open it yourself and press Ctrl+V", at);
            return;
        }
        ToastWindow.Flash("Opening " + target.Name, $"{what} copied — open a chat and press Ctrl+V", at);
    }

    /// <summary>
    /// Puts the files on the clipboard the way Explorer's Copy does (file list, shell item list and
    /// "copy" effect), so chat apps, mail and Explorer itself can all paste them — any number at once.
    /// </summary>
    public static void CopyToClipboard(IReadOnlyList<string> files)
    {
        var paths = files.Select(Path.GetFullPath).ToArray();
        var list = new StringCollection();
        list.AddRange(paths);
        var data = new DataObject();
        data.SetFileDropList(list);
        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1))); // DROPEFFECT_COPY
        if (ShellApps.ItemIdList(paths) is { } ids) data.SetData("Shell IDList Array", new MemoryStream(ids));
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (System.Runtime.InteropServices.COMException) when (attempt < 8)
            {
                // Another app has the clipboard open for a moment.
                Thread.Sleep(60);
            }
        }
    }

    static string? OutlookPath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\OUTLOOK.EXE");
            return key?.GetValue(null) is string path && File.Exists(path.Trim('"')) ? path.Trim('"') : null;
        }
        catch { return null; }
    }
}

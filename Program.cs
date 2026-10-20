using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using FileWheel.Core;
using FileWheel.Native;
using FileWheel.UI;

namespace FileWheel;

public static class Program
{
    const string PipeName = "FileWheel.Desktop.v2";

    public static string DataFolder
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FileWheel");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static void Log(string message)
    {
        try { File.AppendAllText(Path.Combine(DataFolder, "filewheel.log"), $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}:{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}"); } catch { }
    }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--convert") return Cli.Convert(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "--snapshot") return Snapshots.Run(args.Skip(1).ToArray());
        if (args.Length > 0 && args[0] == "--dragtest") return DragTest.Run(args.Skip(1).ToArray());

        using var mutex = new Mutex(true, @"Local\FileWheel.Desktop.v2", out bool first);
        if (!first)
        {
            // Already running: hand our files (or a "show" request) to the running copy.
            SendToRunningInstance(args.Length == 0 ? ["--show"] : args);
            return 0;
        }

        var app = new App();
        app.Run();
        return 0;
    }

    static void SendToRunningInstance(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", args));
            client.Write(bytes);
        }
        catch (Exception e) { Log("Pipe send failed: " + e.Message); }
    }

    internal static void ListenForInstances(Action<string[]> received)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = reader.ReadToEnd();
                    received(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (Exception e) { Log("Pipe error: " + e.Message); Thread.Sleep(500); }
            }
        })
        { IsBackground = true, Name = "FileWheel instance pipe" };
        thread.Start();
    }

    public static void Reveal(string path)
    {
        try
        {
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            info.ArgumentList.Add("/select," + path);
            Process.Start(info);
        }
        catch (Exception e) { Log("Reveal failed: " + e.Message); }
    }
}

/// <summary>The tray app: owns the global input watcher, the wheel and the tray icon.</summary>
public sealed class App : Application
{
    GlobalInput? input;
    WheelWindow? wheel;
    TrayIcon? tray;
    HomeWindow? home;

    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/FileWheel;component/UI/Theme.xaml") });
        DispatcherUnhandledException += (_, e) =>
        {
            Program.Log(e.Exception.ToString());
            e.Handled = true;
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Task.Run(Formats.Warm);
        Share.Warm();

        wheel = new WheelWindow();
        wheel.Picked += (files, mode, item, at) => Controller.Handle(files, mode, item, at);

        try
        {
            input = new GlobalInput(Dispatcher) { Paused = Settings.Current.Paused };
            input.ModeKey += m =>
            {
                if (wheel.IsOpen) wheel.SwitchMode(m);
                else wheel.OpenAt(m, Win32.Cursor());
            };
            input.EscapePressed += () => wheel.Dismiss();
            input.LeftReleased += () => wheel.OnPointerReleased();
        }
        catch (Exception ex)
        {
            Program.Log("Global input unavailable: " + ex.Message);
        }

        CreateTray();
        // Build one wheel off-screen while idle so the first real one opens instantly.
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            var warm = WheelWindow.BuildPreview(["warm-up.jpg"], WheelMode.Tools, 1);
            warm.Measure(new Size(WheelView.ViewSize, WheelView.ViewSize));
            warm.Arrange(new Rect(0, 0, WheelView.ViewSize, WheelView.ViewSize));
        });
        Program.ListenForInstances(args => Dispatcher.BeginInvoke(() => HandleArgs(args)));

        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (args.Length > 0) HandleArgs(args);
        else ShowHome();
    }

    void HandleArgs(string[] args)
    {
        if (args.Contains("--show")) { ShowHome(); return; }
        if (args.Contains("--background")) return;
        var files = args.Where(a => File.Exists(a) || Directory.Exists(a)).ToArray();
        if (files.Length > 0) OpenWheel(files, WheelMode.Convert);
    }

    public void OpenWheel(IReadOnlyList<string> files, WheelMode mode) => wheel?.OpenAt(mode, Win32.Cursor(), files);

    /// <summary>Opens a clickable wheel at <paramref name="at"/>; with <paramref name="back"/>, its middle is a Back button.</summary>
    public void OpenWheelAt(IReadOnlyList<string> files, WheelMode mode, POINT at, Action? back = null) => wheel?.OpenAt(mode, at, files, back);

    public static new App? Current => Application.Current as App;

    public void ShowHome() => ShowHome(fresh: false);

    public void ShowHome(bool fresh)
    {
        if (!fresh && home is { IsVisible: true })
        {
            home.Activate();
            return;
        }
        home = new HomeWindow(this);
        home.Present();
    }

    public bool Paused
    {
        get => input?.Paused ?? true;
        set
        {
            if (input != null) input.Paused = value;
            Settings.Current.Paused = value;
            Settings.Save();
            UpdateTrayText();
        }
    }

    void CreateTray()
    {
        tray = new TrayIcon("FileWheel");
        tray.Menu.Add(new TrayIcon.MenuItem("Open FileWheel", () => ShowHome()));
        tray.Menu.Add(new TrayIcon.MenuItem("Choose files…", PickFiles));
        tray.Menu.Add(new TrayIcon.MenuItem("Pause drag + Shift", () => Paused = !Paused, () => Paused));
        tray.Menu.Add(null);
        tray.Menu.Add(new TrayIcon.MenuItem("Quit FileWheel", Quit));
        tray.Clicked += () => ShowHome();
        UpdateTrayText();
    }

    void UpdateTrayText()
    {
        if (tray != null) tray.Text = Paused ? "FileWheel (paused)" : "FileWheel — hold Shift and drag a file";
    }

    public void PickFiles()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Choose files for FileWheel" };
        if (picker.ShowDialog() == true) OpenWheel(picker.FileNames, WheelMode.Convert);
    }

    public void Quit()
    {
        input?.Dispose();
        tray?.Dispose();
        Shutdown();
    }
}

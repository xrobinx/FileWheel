using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FileWheel.Core;

namespace FileWheel.UI.Editors;

/// <summary>"Compress" for audio and video: one quality slider, progress in a toast.</summary>
public sealed class MediaCompressPanel : GlassPanel
{
    readonly IReadOnlyList<string> files;
    Slider? quality;

    public MediaCompressPanel(IReadOnlyList<string> files) : base("Compress", 400, 330)
    {
        this.files = files;
        var stack = new StackPanel { Margin = new Thickness(20, 4, 20, 12) };
        var name = Theme.Text(files.Count == 1 ? System.IO.Path.GetFileName(files[0]) : $"{files.Count} files", 13, Theme.Ink, FontWeights.SemiBold);
        name.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(name);
        var size = Theme.Text(Formats.SizeText(files.Sum(f => new FileInfo(f).Length)), 28, Theme.Ink, FontWeights.SemiBold);
        size.FontFamily = Theme.DisplayFont;
        size.HorizontalAlignment = HorizontalAlignment.Center;
        size.Margin = new Thickness(0, 10, 0, 14);
        stack.Children.Add(size);
        stack.Children.Add(Theme.SliderRow("Quality", 0, 100, 55, "%", _ => { }, out quality, 70));
        var note = Theme.Text("Lower quality makes a smaller file. Saved as a copy.", 11.5, Theme.InkMuted);
        note.HorizontalAlignment = HorizontalAlignment.Center;
        note.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(note);
        Body.Children.Add(stack);
        var go = Theme.Primary("Compress", Go);
        SetFooter(null, go);
        DefaultAction = Go;
    }

    void Go()
    {
        int q = (int)quality!.Value;
        var at = Anchor.X == 0 && Anchor.Y == 0 ? CenterOnScreen() : Anchor;
        long before = files.Sum(f => new FileInfo(f).Length);
        CloseAnimated();
        _ = ToastWindow.Run("Compressing", at,
            (ct, report) => files.Select((f, i) => Media.Compress(f, q, p => report((i + p) / files.Count, $"{(i + p) / files.Count:P0}"), ct)).ToList(),
            outputs => ("Compressed", $"{Formats.SizeText(before)} → {Formats.SizeText(outputs.Sum(o => new FileInfo(o).Length))}", outputs.FirstOrDefault()),
            "Couldn't compress");
    }
}

/// <summary>What a <see cref="TrimPanel"/> does with the moment or stretch you pick.</summary>
public enum TimeTool { Trim, Bleep, Split, Snapshot }

/// <summary>
/// "Trim" (keep a stretch), "Bleep" (beep over a stretch), "Split" (cut in two at a point) and
/// "Snapshot" (save the frame at a point): the same timeline with one or two handles.
/// </summary>
public sealed class TrimPanel : GlassPanel
{
    const double TrackW = 340;

    readonly string file;
    readonly TimeTool tool;
    readonly bool single;
    readonly MediaElement player = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Close, ScrubbingEnabled = true, Stretch = Stretch.Uniform };
    readonly Canvas track = new() { Width = TrackW, Height = 34, Background = Brushes.Transparent, Cursor = Cursors.Hand };
    readonly Rectangle range = new() { Height = 6, RadiusX = 3, RadiusY = 3 };
    readonly Ellipse startKnob = Knob(), endKnob = Knob();
    readonly Rectangle head = new() { Width = 2, Height = 18 };
    readonly TextBlock times;
    readonly Button playButton;
    readonly DispatcherTimer ticker = new() { Interval = TimeSpan.FromMilliseconds(50) };
    double duration, start, end;
    Ellipse? dragging;
    bool playing;

    public TrimPanel(string file, TimeTool tool = TimeTool.Trim) : base(tool.ToString(), 420, 520)
    {
        this.file = file;
        this.tool = tool;
        single = tool is TimeTool.Split or TimeTool.Snapshot;
        range.Fill = Theme.Brush(Theme.Accent);
        head.Fill = Theme.Brush(Theme.Ink, 0.7);
        times = Theme.Text("", 12, Theme.InkSoft, FontWeights.Medium);
        Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        bool isVideo = Formats.Kind(file) == FileKind.Video;
        var screen = new Border
        {
            Margin = new Thickness(18, 2, 18, 10), CornerRadius = new CornerRadius(10), ClipToBounds = true,
            Background = isVideo ? Brushes.Black : Theme.WellBrush,
            Child = isVideo ? player : Theme.Icon(tool == TimeTool.Bleep ? "Bleep" : "Trim", 48, Theme.InkSoft),
        };
        if (!isVideo) Body.Children.Add(player);
        Body.Children.Add(screen);

        var controls = new StackPanel { Margin = new Thickness(18, 0, 18, 12) };
        track.Children.Add(new Rectangle { Width = TrackW, Height = 6, RadiusX = 3, RadiusY = 3, Fill = Theme.TrackBrush });
        Canvas.SetTop(track.Children[0], 14);
        track.Children.Add(range);
        Canvas.SetTop(range, 14);
        track.Children.Add(head);
        Canvas.SetTop(head, 8);
        track.Children.Add(startKnob);
        track.Children.Add(endKnob);
        Canvas.SetTop(startKnob, 9);
        Canvas.SetTop(endKnob, 9);
        if (single)
        {
            // One handle: the point to cut at, or the frame to save.
            range.Visibility = Visibility.Collapsed;
            endKnob.Visibility = Visibility.Collapsed;
        }
        controls.Children.Add(track);

        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        playButton = Theme.IconButton("Arrow", tool == TimeTool.Trim ? "Play selection" : "Play", TogglePlay);
        playButton.Content = Theme.Text("▶", 11, Theme.InkSoft);
        row.Children.Add(playButton);
        times.Margin = new Thickness(10, 0, 0, 0);
        row.Children.Add(times);
        controls.Children.Add(row);
        Grid.SetRow(controls, 1);
        Body.Children.Add(controls);

        var go = Theme.Primary(tool switch { TimeTool.Split => "Split Here", TimeTool.Snapshot => "Save Frame", _ => tool.ToString() }, Run);
        SetFooter(Theme.Soft("Reset", () => { Defaults(); Layout(); }), go);
        DefaultAction = Run;

        track.MouseLeftButtonDown += (_, e) =>
        {
            var x = e.GetPosition(track).X;
            dragging = single || Math.Abs(x - Pos(start)) <= Math.Abs(x - Pos(end)) ? startKnob : endKnob;
            track.CaptureMouse();
            DragTo(x);
        };
        track.MouseMove += (_, e) => { if (dragging != null) DragTo(e.GetPosition(track).X); };
        track.MouseLeftButtonUp += (_, _) => { dragging = null; track.ReleaseMouseCapture(); };
        ticker.Tick += (_, _) =>
        {
            double t = player.Position.TotalSeconds;
            Canvas.SetLeft(head, Pos(t) - 1);
            if (playing && !single && t >= end) TogglePlay();
        };
        player.MediaOpened += (_, _) =>
        {
            if (player.NaturalDuration.HasTimeSpan) { duration = player.NaturalDuration.TimeSpan.TotalSeconds; Defaults(); Layout(); }
            player.Pause();
        };
        Closed += (_, _) => { ticker.Stop(); player.Close(); };
        Loaded += async (_, _) =>
        {
            try
            {
                player.Source = new Uri(file);
                player.Play();
                player.Pause();
                ticker.Start();
                var info = await Task.Run(() => Media.Probe(file));
                if (duration <= 0 && info.Duration > 0) { duration = info.Duration; Defaults(); Layout(); }
            }
            catch (Exception e) { ShowError(Converter.Friendly(e)); }
        };
    }

    /// <summary>Where the handles start: the whole file (Trim), the middle (Split), a second in the middle (Bleep), near the start (Snapshot).</summary>
    void Defaults()
    {
        switch (tool)
        {
            case TimeTool.Split: start = duration / 2; end = duration; break;
            case TimeTool.Snapshot: start = Math.Min(1, duration / 3); end = duration; break;
            case TimeTool.Bleep: start = Math.Max(0, duration / 2 - 0.5); end = Math.Min(duration, start + 1); break;
            default: start = 0; end = duration; break;
        }
        player.Position = TimeSpan.FromSeconds(start);
    }

    static Ellipse Knob() => new()
    {
        Width = 16, Height = 16, Fill = Brushes.White, Stroke = Theme.Brush(Colors.Black, 0.15),
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 5, ShadowDepth = 1, Opacity = 0.25 },
    };

    double Pos(double seconds) => duration <= 0 ? 0 : seconds / duration * TrackW;

    void DragTo(double x)
    {
        if (duration <= 0) return;
        double t = Math.Clamp(x / TrackW, 0, 1) * duration;
        if (single) start = t;
        else if (dragging == startKnob) start = Math.Min(t, end - 0.1);
        else end = Math.Max(t, start + 0.1);
        player.Position = TimeSpan.FromSeconds(dragging == endKnob ? end : start);
        Layout();
    }

    void Layout()
    {
        Canvas.SetLeft(startKnob, Pos(start) - 8);
        Canvas.SetLeft(endKnob, Pos(end) - 8);
        Canvas.SetLeft(range, Pos(start));
        range.Width = Math.Max(0, Pos(end) - Pos(start));
        times.Text = tool switch
        {
            TimeTool.Split => $"Cut at {Clock(start)}   ·   {Clock(start)} + {Clock(duration - start)}",
            TimeTool.Snapshot => $"Frame at {Clock(start)}",
            TimeTool.Bleep => $"Beep {Clock(start)} – {Clock(end)}   ·   {end - start:0.0} s",
            _ => $"{Clock(start)} – {Clock(end)}   ·   {end - start:0.0} s",
        };
    }

    static string Clock(double s) => TimeSpan.FromSeconds(Math.Max(0, s)).ToString(s >= 3600 ? @"h\:mm\:ss\.f" : @"m\:ss\.f");

    void TogglePlay()
    {
        playing = !playing;
        playButton.Content = Theme.Text(playing ? "❚❚" : "▶", 11, Theme.InkSoft);
        if (playing)
        {
            if (single || player.Position.TotalSeconds < start || player.Position.TotalSeconds >= end - 0.05) player.Position = TimeSpan.FromSeconds(start);
            player.Play();
        }
        else player.Pause();
    }

    void Run()
    {
        if (duration <= 0) { ShowError("Still reading the file…"); return; }
        double s = start, e = end;
        var at = Anchor.X == 0 && Anchor.Y == 0 ? CenterOnScreen() : Anchor;
        if (tool == TimeTool.Snapshot)
        {
            // One frame is quick: save it, then confirm.
            try
            {
                var frame = Media.Snapshot(file, s);
                CloseAnimated();
                ToastWindow.Flash("Frame saved", System.IO.Path.GetFileName(frame), at, frame);
            }
            catch (Exception ex) { ShowError(Converter.Friendly(ex)); }
            return;
        }
        CloseAnimated();
        switch (tool)
        {
            case TimeTool.Split:
                _ = ToastWindow.Run("Splitting", at,
                    (ct, report) => Media.Split(file, s, p => report(p, $"{p:P0}"), ct),
                    outputs => ("Split in two", string.Join(" · ", outputs.Select(System.IO.Path.GetFileName)), outputs.FirstOrDefault()),
                    "Couldn't split");
                break;
            case TimeTool.Bleep:
                _ = ToastWindow.Run("Bleeping", at,
                    (ct, report) => Media.Bleep(file, s, e, p => report(p, $"{p:P0}"), ct),
                    output => ("Bleeped", System.IO.Path.GetFileName(output), output),
                    "Couldn't bleep");
                break;
            default:
                _ = ToastWindow.Run("Trimming", at,
                    (ct, report) => Media.Trim(file, s, e, p => report(p, $"{p:P0}"), ct),
                    output => ("Trimmed", System.IO.Path.GetFileName(output), output),
                    "Couldn't trim");
                break;
        }
    }
}

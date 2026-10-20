using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FileWheel.Native;
using FileWheel.UI.Editors;

namespace FileWheel.UI;

/// <summary>
/// "Custom" on the Share ring: tick any installed apps to put them on the ring. Dropping files on
/// one of them later copies the files and opens the app, ready to paste.
/// </summary>
public sealed class ShareAppsPanel : GlassPanel
{
    readonly IReadOnlyList<string> files;
    readonly POINT at;
    readonly StackPanel list = new();
    readonly TextBox search;
    readonly TextBlock note;
    readonly List<Settings.SavedApp> chosen;
    readonly List<(Border Row, Settings.SavedApp App, Action Paint)> rows = [];

    public ShareAppsPanel(IReadOnlyList<string> files, POINT at) : base("Share apps", 360, 540, near: at)
    {
        this.files = files;
        this.at = at;
        Anchor = at;
        chosen = Share.CurrentChoice();
        if (files.Count > 0) EnableBack(ReopenRing);

        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var intro = Theme.Text("Tick the apps for the Share ring. Drop files on one to copy them all and open it — then just paste (Ctrl+V).", 11.5, Theme.InkSoft);
        intro.TextWrapping = TextWrapping.Wrap;
        intro.TextTrimming = TextTrimming.None;
        intro.Margin = new Thickness(18, 0, 18, 10);
        Body.Children.Add(intro);

        search = new TextBox { Style = Theme.Style("FieldBox"), Padding = new Thickness(28, 5, 8, 5) };
        var placeholder = Theme.Text("Search apps", 12.5, Theme.InkMuted);
        placeholder.IsHitTestVisible = false;
        placeholder.Margin = new Thickness(30, 0, 0, 0);
        var glass = Theme.Icon("Search", 13, Theme.InkMuted);
        glass.HorizontalAlignment = HorizontalAlignment.Left;
        glass.VerticalAlignment = VerticalAlignment.Center;
        glass.Margin = new Thickness(10, 0, 0, 0);
        glass.IsHitTestVisible = false;
        var field = new Grid { Margin = new Thickness(18, 0, 18, 10) };
        field.Children.Add(search);
        field.Children.Add(placeholder);
        field.Children.Add(glass);
        Grid.SetRow(field, 1);
        Body.Children.Add(field);
        search.TextChanged += (_, _) =>
        {
            placeholder.Visibility = search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Filter();
        };

        list.Children.Add(Preview.LoadingLabel());
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12, 0, 8, 6), Padding = new Thickness(0, 0, 4, 0) };
        Grid.SetRow(scroll, 2);
        Body.Children.Add(scroll);

        note = Theme.Text("", 11.5, Theme.InkMuted);
        note.Margin = new Thickness(18, 0, 18, 6);
        Grid.SetRow(note, 3);
        Body.Children.Add(note);
        UpdateNote();

        SetFooter(Theme.Soft("Reset", Reset), Theme.Primary("Done", Done));
        DefaultAction = Done;
        Loaded += (_, _) => LoadApps();
    }

    /// <summary>Reads the Start menu's app list and the icons on a background thread, filling rows as they come.</summary>
    void LoadApps()
    {
        var dispatcher = Dispatcher;
        var thread = new Thread(() =>
        {
            List<ShellApps.InstalledApp> apps;
            try { apps = ShellApps.List(); }
            catch (Exception e) { dispatcher.BeginInvoke(() => UpdateNote("Couldn't read the app list: " + e.Message)); return; }
            // Your current apps first (in ring order), then everything else A–Z.
            var ordered = chosen.Select(c => apps.FirstOrDefault(a => a.ParsingName == c.Id) ?? new ShellApps.InstalledApp(c.Name, c.Id))
                .Concat(apps.Where(a => chosen.All(c => c.Id != a.ParsingName))
                    .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                    .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            dispatcher.Invoke(() =>
            {
                list.Children.Clear();
                foreach (var app in ordered) AddRow(new Settings.SavedApp { Name = app.Name, Id = app.ParsingName });
            });
            for (int i = 0; i < ordered.Count; i++)
            {
                var icon = ShellApps.Icon(ordered[i].ParsingName, 32);
                int index = i;
                if (icon != null) dispatcher.BeginInvoke(() => SetIcon(index, icon));
            }
        })
        { IsBackground = true, Name = "FileWheel app list" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    readonly List<Image> icons = [];

    void SetIcon(int index, BitmapSource icon)
    {
        if (index < icons.Count) icons[index].Source = icon;
    }

    void AddRow(Settings.SavedApp app)
    {
        var image = new Image { Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        icons.Add(image);

        var mark = new Grid { Width = 20, Height = 20 };
        var ring = new Ellipse { StrokeThickness = 1.4 };
        var tick = Theme.Icon("Check", 12, Colors.White, 1.8);
        tick.HorizontalAlignment = HorizontalAlignment.Center;
        tick.VerticalAlignment = VerticalAlignment.Center;
        mark.Children.Add(ring);
        mark.Children.Add(tick);

        var name = Theme.Text(app.Name, 12.5, Theme.Ink);
        var dock = new DockPanel();
        DockPanel.SetDock(image, Dock.Left);
        DockPanel.SetDock(mark, Dock.Right);
        dock.Children.Add(image);
        dock.Children.Add(mark);
        dock.Children.Add(name);

        var row = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 6, 8, 6), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = dock };
        void Paint()
        {
            bool on = chosen.Any(c => c.Id == app.Id);
            ring.Fill = on ? Theme.Brush(Theme.Accent) : Brushes.Transparent;
            ring.Stroke = on ? Theme.Brush(Theme.Accent) : Theme.Brush(Theme.InkSoft, 0.5);
            tick.Visibility = on ? Visibility.Visible : Visibility.Hidden;
            name.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
        Paint();
        row.MouseEnter += (_, _) => row.Background = Theme.Brush(Theme.Current.SoftFillHover);
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, _) =>
        {
            var existing = chosen.FindIndex(c => c.Id == app.Id);
            if (existing >= 0) chosen.RemoveAt(existing);
            else if (chosen.Count >= Share.MaxApps) { UpdateNote($"Up to {Share.MaxApps} apps fit on the ring — untick one first."); return; }
            else chosen.Add(app);
            UpdateNote();
            Paint();
        };
        rows.Add((row, app, Paint));
        list.Children.Add(row);
    }

    void UpdateNote(string? warning = null)
    {
        note.Text = warning ?? (chosen.Count == 0 ? "No apps chosen — the ring will only have Copy." : $"{chosen.Count} of {Share.MaxApps} on the ring");
        note.Foreground = Theme.Brush(warning != null ? Palette.Danger : Theme.InkMuted);
    }

    void Filter()
    {
        var q = search.Text.Trim();
        foreach (var (row, app, _) in rows)
            row.Visibility = q.Length == 0 || app.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Back to the messaging apps found automatically.</summary>
    void Reset()
    {
        Share.SaveChoice(null);
        chosen.Clear();
        chosen.AddRange(Share.CurrentChoice());
        foreach (var (_, _, paint) in rows) paint();
        UpdateNote();
    }

    void Done()
    {
        Share.SaveChoice(chosen.ToList());
        if (files.Count > 0) CloseAnimated(ReopenRing);
        else CloseAnimated();
    }

    /// <summary>The Share ring again for the same files, so they can be sent right away.</summary>
    void ReopenRing() =>
        App.Current?.OpenWheelAt(files, Core.WheelMode.Share, at, back: () => App.Current?.OpenWheelAt(files, Core.WheelMode.Actions, at));
}

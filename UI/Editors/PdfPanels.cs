using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileWheel.Core;

namespace FileWheel.UI.Editors;

/// <summary>Page thumbnails rendered in the background.</summary>
public sealed class PageGrid : WrapPanel
{
    public event Action<int>? PageClicked;
    readonly List<(Border Frame, Image Image, RotateTransform Turn)> pages = [];
    public IReadOnlySet<int> Selected => selected;
    readonly HashSet<int> selected = [];

    public PageGrid() { Margin = new Thickness(14, 0, 8, 0); }

    public async Task Load(string file, int maxPages = 120)
    {
        int count = await Task.Run(() => PdfRenderer.PageCount(file));
        for (int i = 0; i < Math.Min(count, maxPages); i++)
        {
            var turn = new RotateTransform();
            var img = new Image { Stretch = Stretch.Uniform, Width = 70, Height = 90, LayoutTransform = turn };
            var label = Theme.Text((i + 1).ToString(), 10.5, Theme.InkMuted);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var stack = new StackPanel();
            stack.Children.Add(img);
            stack.Children.Add(label);
            var frame = new Border
            {
                Child = stack, Padding = new Thickness(5, 5, 5, 2), Margin = new Thickness(4), CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Background = Theme.SurfaceBrush, Cursor = Cursors.Hand,
            };
            int page = i + 1;
            frame.MouseLeftButtonUp += (_, _) => PageClicked?.Invoke(page);
            pages.Add((frame, img, turn));
            Children.Add(frame);
        }
        for (int i = 0; i < pages.Count; i++)
        {
            int index = i;
            var bmp = await Task.Run(() =>
            {
                using var page = PdfRenderer.Render(file, index, 30);
                return Converter.ToBitmap(page);
            });
            pages[i].Image.Source = bmp;
        }
    }

    public void SetRotation(double degrees)
    {
        foreach (var p in pages) p.Turn.Angle = degrees;
    }

    public void SetSelected(IEnumerable<int> pagesToSelect)
    {
        selected.Clear();
        foreach (var p in pagesToSelect) selected.Add(p);
        for (int i = 0; i < pages.Count; i++)
            pages[i].Frame.BorderBrush = selected.Contains(i + 1) ? Theme.Brush(Theme.Accent) : Brushes.Transparent;
    }
}

public abstract class PdfPanel : GlassPanel
{
    protected readonly IReadOnlyList<string> Files;
    protected readonly PageGrid Pages = new();
    protected readonly StackPanel Controls = new() { Margin = new Thickness(18, 8, 18, 12) };
    protected int PageCount;

    protected PdfPanel(string title, IReadOnlyList<string> files, double height = 560) : base(title, 420, height)
    {
        Files = files;
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var info = Theme.Text(files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} PDFs", 12, Theme.InkSoft, FontWeights.Medium);
        info.Margin = new Thickness(18, 0, 18, 8);
        info.HorizontalAlignment = HorizontalAlignment.Center;
        Body.Children.Add(info);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = Pages };
        Grid.SetRow(scroll, 1);
        Body.Children.Add(scroll);
        Grid.SetRow(Controls, 2);
        Body.Children.Add(Controls);
        Loaded += async (_, _) =>
        {
            try
            {
                PageCount = await Task.Run(() => PdfRenderer.PageCount(files[0]));
                info.Text += $" · {PageCount} page{(PageCount == 1 ? "" : "s")} · {Formats.SizeText(new FileInfo(files[0]).Length)}";
                OnPagesCounted();
                await Pages.Load(files[0]);
            }
            catch (Exception e) { ShowError(Converter.Friendly(e)); }
        };
    }

    protected virtual void OnPagesCounted() { }

    protected static DockPanel Row(string label, UIElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        var l = Theme.Label(label);
        l.Width = 80;
        row.Children.Add(l);
        row.Children.Add(control);
        return row;
    }

    /// <summary>Runs a long PDF job with a progress toast, closing the panel first.</summary>
    protected void RunWithToast(string heading, Func<CancellationToken, Action<double>, IReadOnlyList<string>> work, string doneHeading)
    {
        var at = Anchor.X == 0 && Anchor.Y == 0 ? CenterOnScreen() : Anchor;
        CloseAnimated();
        long before = Files.Sum(f => new FileInfo(f).Length);
        _ = ToastWindow.Run(heading, at,
            (ct, report) => work(ct, p => report(p, $"{p:P0}")),
            outputs =>
            {
                long after = outputs.Sum(o => new FileInfo(o).Length);
                var text = doneHeading == "Compressed" ? $"{Formats.SizeText(before)} → {Formats.SizeText(after)}" : $"{outputs.Count} file{(outputs.Count == 1 ? "" : "s")} saved";
                return (doneHeading, text, outputs.FirstOrDefault());
            },
            "Couldn't finish");
    }
}

public sealed class PdfCompressPanel : PdfPanel
{
    static readonly (string Label, int Dpi, int Quality)[] Levels = [("Smallest", 90, 50), ("Balanced", 130, 66), ("Sharp", 180, 80)];
    (string Label, int Dpi, int Quality) level = Levels[1];

    public PdfCompressPanel(IReadOnlyList<string> files) : base("Compress PDF", files)
    {
        Controls.Children.Add(Row("Quality", Theme.Segmented(Levels.Select(l => l.Label), level.Label, s => level = Levels.First(l => l.Label == s), out _)));
        var note = Theme.Text("Pages are rebuilt as compressed images — best for scans and photo-heavy PDFs. Text won't be selectable in the copy.", 11.5, Theme.InkMuted);
        note.TextWrapping = TextWrapping.Wrap;
        note.TextTrimming = TextTrimming.None;
        note.Margin = new Thickness(0, 6, 0, 0);
        Controls.Children.Add(note);
        var go = Theme.Primary("Compress", () => RunWithToast("Compressing PDF", (ct, p) => Files.Select(f => PdfTools.Compress(f, level.Dpi, level.Quality, p, ct)).ToList(), "Compressed"));
        SetFooter(null, go);
        DefaultAction = () => go.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    }
}

public sealed class PdfRotatePanel : PdfPanel
{
    int degrees = 90;

    public PdfRotatePanel(IReadOnlyList<string> files) : base("Rotate Pages", files)
    {
        Controls.Children.Add(Row("Turn", Theme.Segmented(["90° right", "180°", "90° left"], "90° right", s =>
        {
            degrees = s switch { "180°" => 180, "90° left" => 270, _ => 90 };
            Pages.SetRotation(degrees);
        }, out _)));
        Pages.Loaded += (_, _) => Pages.SetRotation(degrees);
        var go = Theme.Primary("Rotate", () => RunWithToast("Rotating", (_, _) => Files.Select(f => PdfTools.Rotate(f, degrees)).ToList(), "Rotated"));
        SetFooter(null, go);
    }
}

public sealed class PdfSplitPanel : PdfPanel
{
    readonly Button go;

    public PdfSplitPanel(IReadOnlyList<string> files) : base("Split PDF", files)
    {
        go = Theme.Primary("Split", () => RunWithToast("Splitting", (ct, p) => Files.SelectMany(f => PdfTools.Split(f, p, ct)).ToList(), "Split"));
        SetFooter(null, go);
    }

    protected override void OnPagesCounted()
    {
        go.Content = $"Split into {PageCount} PDFs";
        go.IsEnabled = PageCount > 1;
    }
}

public sealed class PdfExtractPanel : PdfPanel
{
    readonly TextBox range;
    readonly Button go;
    bool syncing;

    public PdfExtractPanel(string file) : base("Extract Pages", [file], 600)
    {
        range = new TextBox { Style = Theme.Style("FieldBox"), Text = "1" };
        Controls.Children.Add(Row("Pages", range));
        var hint = Theme.Text("Click pages or type ranges like 1-3, 6", 11.5, Theme.InkMuted);
        hint.Margin = new Thickness(80, 2, 0, 0);
        Controls.Children.Add(hint);
        range.TextChanged += (_, _) => SyncFromText();
        Pages.PageClicked += p =>
        {
            var set = Pages.Selected.ToHashSet();
            if (!set.Remove(p)) set.Add(p);
            Pages.SetSelected(set);
            syncing = true;
            range.Text = Compact(set.OrderBy(x => x).ToList());
            syncing = false;
        };
        go = Theme.Primary("Extract", Extract);
        SetFooter(null, go);
        DefaultAction = Extract;
    }

    protected override void OnPagesCounted() => SyncFromText();

    void SyncFromText()
    {
        if (syncing || PageCount == 0) return;
        try
        {
            var pages = PdfTools.ParseRange(range.Text, PageCount);
            Pages.SetSelected(pages);
            go.IsEnabled = pages.Count > 0;
        }
        catch { go.IsEnabled = false; }
    }

    static string Compact(List<int> pages)
    {
        var parts = new List<string>();
        for (int i = 0; i < pages.Count;)
        {
            int j = i;
            while (j + 1 < pages.Count && pages[j + 1] == pages[j] + 1) j++;
            parts.Add(i == j ? pages[i].ToString() : $"{pages[i]}-{pages[j]}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    void Extract()
    {
        List<int> pages;
        try { pages = PdfTools.ParseRange(range.Text, PageCount); }
        catch (Exception e) { ShowError(e.Message); return; }
        if (pages.Count == 0) { ShowError("Choose at least one page."); return; }
        var label = Compact(pages.OrderBy(p => p).ToList()).Replace(", ", ",");
        RunWithToast("Extracting pages", (_, _) => [PdfTools.Extract(Files[0], pages, label)], "Extracted");
    }
}

public sealed class PdfMergePanel : GlassPanel
{
    readonly List<string> order;
    readonly StackPanel list = new() { Margin = new Thickness(18, 4, 18, 8) };

    public PdfMergePanel(IReadOnlyList<string> files) : base("Merge PDFs", 420, 520)
    {
        order = files.ToList();
        var hint = Theme.Text("Arrange the order, then merge into one PDF.", 12, Theme.InkSoft);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        hint.Margin = new Thickness(0, 0, 0, 6);
        var stack = new DockPanel();
        DockPanel.SetDock(hint, Dock.Top);
        stack.Children.Add(hint);
        stack.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Body.Children.Add(stack);
        Fill();
        var go = Theme.Primary("Merge", Merge);
        SetFooter(null, go);
        DefaultAction = Merge;
    }

    void Fill()
    {
        list.Children.Clear();
        for (int i = 0; i < order.Count; i++)
        {
            int index = i;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var card = new Border
            {
                CornerRadius = new CornerRadius(9), Background = Theme.SurfaceBrush, BorderBrush = Theme.SurfaceBorderBrush,
                BorderThickness = new Thickness(1), Padding = new Thickness(10, 7, 8, 7), Child = row,
            };
            var number = Theme.Text($"{i + 1}", 12, Theme.Accent, FontWeights.Bold);
            number.Width = 22;
            row.Children.Add(number);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var up = Theme.IconButton("Arrow", "Move up", () => Swap(index, index - 1));
            up.Content = Theme.Text("▲", 10, Theme.InkSoft);
            up.IsEnabled = i > 0;
            var down = Theme.IconButton("Arrow", "Move down", () => Swap(index, index + 1));
            down.Content = Theme.Text("▼", 10, Theme.InkSoft);
            down.IsEnabled = i < order.Count - 1;
            down.Margin = new Thickness(4, 0, 0, 0);
            buttons.Children.Add(up);
            buttons.Children.Add(down);
            DockPanel.SetDock(buttons, Dock.Right);
            row.Children.Add(buttons);
            row.Children.Add(Theme.Text(Path.GetFileName(order[i]), 12.5, Theme.Ink, FontWeights.Medium));
            list.Children.Add(card);
        }
    }

    void Swap(int a, int b)
    {
        if (b < 0 || b >= order.Count) return;
        (order[a], order[b]) = (order[b], order[a]);
        Fill();
    }

    void Merge()
    {
        var at = Anchor.X == 0 && Anchor.Y == 0 ? CenterOnScreen() : Anchor;
        var files = order.ToList();
        CloseAnimated();
        _ = ToastWindow.Run("Merging PDFs", at,
            (ct, report) => PdfTools.Merge(files, p => report(p, $"{p:P0}"), ct),
            output => ("Merged", Path.GetFileName(output), output),
            "Couldn't merge");
    }
}

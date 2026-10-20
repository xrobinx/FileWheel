using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileWheel.Core;

namespace FileWheel.UI.Editors;

/// <summary>
/// "Organize Pages": click pages to select them, then move them earlier or later, turn them or delete
/// them. Save writes "Name Organized.pdf"; the original stays as it was.
/// </summary>
public sealed class PdfOrganizePanel : GlassPanel
{
    sealed class Page
    {
        public required int Index;          // 0-based page in the original
        public int Turn;                    // extra rotation in degrees
        public BitmapSource? Thumb;
    }

    readonly string file;
    readonly List<Page> pages = [];
    readonly HashSet<Page> selected = [];
    readonly WrapPanel grid = new() { Margin = new Thickness(14, 0, 8, 0) };
    readonly TextBlock info;
    readonly Button save;

    public PdfOrganizePanel(string file) : base("Organize Pages", 440, 600)
    {
        this.file = file;
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        info = Theme.Text(Path.GetFileName(file), 12, Theme.InkSoft, FontWeights.Medium);
        info.Margin = new Thickness(18, 0, 18, 8);
        info.HorizontalAlignment = HorizontalAlignment.Center;
        Body.Children.Add(info);

        grid.Children.Add(Preview.LoadingLabel());
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = grid };
        Grid.SetRow(scroll, 1);
        Body.Children.Add(scroll);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(18, 8, 18, 10) };
        tools.Children.Add(Tool("◀", "Move earlier", () => Move(-1)));
        tools.Children.Add(Tool("▶", "Move later", () => Move(1)));
        tools.Children.Add(IconTool("RotateLeft", "Turn left", () => Turn(270)));
        tools.Children.Add(IconTool("Rotate", "Turn right", () => Turn(90)));
        tools.Children.Add(IconTool("Delete", "Delete pages (Del)", Delete));
        var hint = Theme.Text("Click pages to select", 11.5, Theme.InkMuted);
        hint.Margin = new Thickness(10, 0, 0, 0);
        tools.Children.Add(hint);
        Grid.SetRow(tools, 2);
        Body.Children.Add(tools);

        save = Theme.Primary("Save", Save);
        SetFooter(Theme.Soft("Reset", Reset), save);
        DefaultAction = Save;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Delete) { Delete(); e.Handled = true; } };

        Loaded += async (_, _) =>
        {
            try
            {
                int count = await Task.Run(() => PdfRenderer.PageCount(file));
                for (int i = 0; i < count; i++) pages.Add(new Page { Index = i });
                Fill();
                foreach (var page in pages.ToList())
                {
                    int index = page.Index;
                    page.Thumb = await Task.Run(() =>
                    {
                        using var image = PdfRenderer.Render(file, index, 30);
                        return Converter.ToBitmap(image);
                    });
                    Fill();
                }
            }
            catch (Exception e) { ShowError(Converter.Friendly(e)); }
        };
    }

    static Button Tool(string glyph, string tip, Action click)
    {
        var b = Theme.IconButton("Arrow", tip, click);
        b.Content = Theme.Text(glyph, 10, Theme.InkSoft);
        b.Margin = new Thickness(0, 0, 6, 0);
        return b;
    }

    static Button IconTool(string icon, string tip, Action click)
    {
        var b = Theme.IconButton(icon, tip, click);
        b.Margin = new Thickness(0, 0, 6, 0);
        return b;
    }

    /// <summary>Redraws the thumbnails in their current order, turns and selection.</summary>
    void Fill()
    {
        grid.Children.Clear();
        foreach (var page in pages)
        {
            var image = new Image
            {
                Source = page.Thumb, Stretch = Stretch.Uniform, Width = 70, Height = 90,
                LayoutTransform = new RotateTransform(page.Turn),
            };
            var label = Theme.Text((page.Index + 1).ToString(), 10.5, Theme.InkMuted);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            var stack = new StackPanel();
            stack.Children.Add(image);
            stack.Children.Add(label);
            var frame = new Border
            {
                Child = stack, Padding = new Thickness(5, 5, 5, 2), Margin = new Thickness(4), CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(2), Background = Theme.SurfaceBrush, Cursor = Cursors.Hand,
                BorderBrush = selected.Contains(page) ? Theme.Brush(Theme.Accent) : Brushes.Transparent,
            };
            frame.MouseLeftButtonUp += (_, _) =>
            {
                if (!selected.Remove(page)) selected.Add(page);
                Fill();
            };
            grid.Children.Add(frame);
        }
        info.Text = $"{Path.GetFileName(file)} · {pages.Count} page{(pages.Count == 1 ? "" : "s")}" + (selected.Count > 0 ? $" · {selected.Count} selected" : "");
        save.IsEnabled = pages.Count > 0;
    }

    void Move(int step)
    {
        if (selected.Count == 0) return;
        // Move the selected pages one place together, keeping their order.
        var order = step < 0 ? Enumerable.Range(0, pages.Count) : Enumerable.Range(0, pages.Count).Reverse();
        foreach (int i in order)
        {
            int j = i + step;
            if (!selected.Contains(pages[i]) || j < 0 || j >= pages.Count || selected.Contains(pages[j])) continue;
            (pages[i], pages[j]) = (pages[j], pages[i]);
        }
        Fill();
    }

    void Turn(int degrees)
    {
        foreach (var page in selected) page.Turn = (page.Turn + degrees) % 360;
        Fill();
    }

    void Delete()
    {
        if (selected.Count == 0) return;
        if (selected.Count == pages.Count) { ShowError("A PDF needs at least one page."); return; }
        pages.RemoveAll(selected.Contains);
        selected.Clear();
        Fill();
    }

    void Reset()
    {
        var thumbs = pages.Where(p => p.Thumb != null).ToDictionary(p => p.Index, p => p.Thumb);
        int count = PdfRenderer.PageCount(file);
        pages.Clear();
        selected.Clear();
        for (int i = 0; i < count; i++) pages.Add(new Page { Index = i, Thumb = thumbs.GetValueOrDefault(i) });
        Fill();
    }

    void Save()
    {
        var plan = pages.Select(p => (p.Index, p.Turn)).ToList();
        SaveInBackground("Saving…", save, (_, _) => [PdfTools.Organize(file, plan)], "Organized");
    }
}

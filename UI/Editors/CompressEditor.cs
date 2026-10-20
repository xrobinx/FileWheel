using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FileWheel.Core;
using ImageMagick;

namespace FileWheel.UI.Editors;

/// <summary>"Compress": quality, format and maximum size, with a live before → after size estimate.</summary>
public sealed class CompressEditor : GlassPanel
{
    static readonly (string Label, int Pixels)[] Sizes = [("Full", 0), ("4K", 3840), ("2K", 2560), ("1080p", 1920), ("720p", 1280)];

    readonly IReadOnlyList<string> files;
    readonly Image preview = new() { Stretch = Stretch.Uniform };
    readonly TextBlock before, after, saving;
    readonly Button compress;
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(220) };
    Slider? quality;
    Func<string, bool> selectFormat = _ => false, selectSize = _ => false;
    string format = "Original";
    int maxPixels;
    MagickImage? full;
    int version;

    public CompressEditor(IReadOnlyList<string> files) : base("Compress", 400, 560)
    {
        this.files = files;
        Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var previewBox = new Border
        {
            Margin = new Thickness(18, 2, 18, 10),
            CornerRadius = new CornerRadius(10),
            Background = Theme.WellBrush,
            BorderBrush = Theme.WellBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = preview,
        };
        Body.Children.Add(previewBox);

        var controls = new StackPanel { Margin = new Thickness(18, 0, 18, 12) };

        var stats = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        stats.ColumnDefinitions.Add(new ColumnDefinition());
        stats.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stats.ColumnDefinitions.Add(new ColumnDefinition());
        before = Stat("—", "Now");
        after = Stat("…", "After");
        var arrow = Theme.Text("→", 22, Theme.InkMuted);
        arrow.Margin = new Thickness(12, 0, 12, 14);
        stats.Children.Add(before.Parent as UIElement);
        Grid.SetColumn(arrow, 1);
        stats.Children.Add(arrow);
        var afterPanel = (StackPanel)after.Parent;
        Grid.SetColumn(afterPanel, 2);
        stats.Children.Add(afterPanel);
        controls.Children.Add(stats);
        saving = Theme.Text("", 11.5, Theme.InkMuted);
        saving.HorizontalAlignment = HorizontalAlignment.Center;
        saving.Margin = new Thickness(0, -8, 0, 10);
        controls.Children.Add(saving);

        controls.Children.Add(Theme.SliderRow("Quality", 5, 100, 72, "%", _ => Queue(), out quality, 76));
        controls.Children.Add(Row("Format", Theme.Segmented(["Original", "JPG", "WebP"], "Original", f => { format = f; Queue(); }, out selectFormat)));
        controls.Children.Add(Row("Max size", Theme.Segmented(Sizes.Select(s => s.Label), "Full", s => { maxPixels = Sizes.First(x => x.Label == s).Pixels; Queue(); }, out selectSize)));
        Grid.SetRow(controls, 1);
        Body.Children.Add(controls);

        compress = Theme.Primary(files.Count > 1 ? $"Compress {files.Count} Images" : "Compress", Save);
        SetFooter(Theme.Soft("Reset", Reset), compress);
        DefaultAction = Save;

        debounce.Tick += (_, _) => { debounce.Stop(); Estimate(); };
        Closed += (_, _) => full?.Dispose();

        Loaded += async (_, _) =>
        {
            try
            {
                before.Text = Formats.SizeText(new FileInfo(files[0]).Length);
                var p = await Task.Run(() => Preview.Load(files[0], 900));
                preview.Source = p.Bitmap;
                full = await Task.Run(() => Converter.LoadImage(files[0]));
                Estimate();
            }
            catch (Exception e) { ShowError(Converter.Friendly(e)); }
        };
    }

    static TextBlock Stat(string value, string caption)
    {
        var big = Theme.Text(value, 22, Theme.Ink, FontWeights.SemiBold);
        big.FontFamily = Theme.DisplayFont;
        big.HorizontalAlignment = HorizontalAlignment.Center;
        var small = Theme.Label(caption);
        small.HorizontalAlignment = HorizontalAlignment.Center;
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(big);
        stack.Children.Add(small);
        return big;
    }

    static DockPanel Row(string label, UIElement control)
    {
        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var l = Theme.Label(label);
        l.Width = 76;
        row.Children.Add(l);
        row.Children.Add(control);
        return row;
    }

    string Ext => format switch { "JPG" => "jpg", "WebP" => "webp", _ => "keep" };

    void Queue()
    {
        after.Text = "…";
        debounce.Stop();
        debounce.Start();
    }

    async void Estimate()
    {
        if (full == null || quality == null) return;
        int mine = ++version;
        int q = (int)quality.Value, max = maxPixels;
        var ext = ImageTools.CompressExt(files[0], Ext);
        try
        {
            var source = full;
            long size = await Task.Run(() => (long)ImageTools.CompressBytes(source, ext, q, max).Length);
            if (mine != version) return;
            long original = new FileInfo(files[0]).Length;
            after.Text = Formats.SizeText(size);
            double change = 1 - size / (double)original;
            saving.Text = change > 0.005 ? $"{change:P0} smaller · {ext.ToUpperInvariant()}" : $"Not smaller than the original · {ext.ToUpperInvariant()}";
        }
        catch (Exception e) { if (mine == version) ShowError(Converter.Friendly(e)); }
    }

    void Reset()
    {
        quality!.Value = 72;
        selectFormat("Original");
        selectSize("Full");
        format = "Original";
        maxPixels = 0;
        Queue();
    }

    void Save()
    {
        int q = (int)quality!.Value, max = maxPixels;
        var fmt = Ext;
        SaveInBackground("Compressing…", compress, (_, _) => files.Select(f => ImageTools.Compress(f, fmt, q, max)).ToList(), "Compressed");
    }
}

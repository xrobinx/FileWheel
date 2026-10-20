using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FileWheel.Core;
using ImageMagick;

namespace FileWheel.UI.Editors;

/// <summary>"Edit Image": rotate, flip, resize and tune brightness, contrast, saturation and warmth.</summary>
public sealed class AdjustEditor : GlassPanel
{
    readonly IReadOnlyList<string> files;
    readonly Image preview = new() { Stretch = Stretch.Uniform };
    readonly TextBlock sizeText;
    readonly Button save;
    readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(45) };
    readonly Adjustments a = new();
    readonly List<Slider> sliders = [];
    Slider? scale;
    MagickImage? small;
    int srcW, srcH, version;
    bool busy, again;

    public AdjustEditor(IReadOnlyList<string> files) : base("Edit Image", 400, 620)
    {
        this.files = files;
        sizeText = Theme.Text("", 11.5, Theme.InkMuted);
        Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Body.Children.Add(new Border
        {
            Margin = new Thickness(18, 2, 18, 10),
            CornerRadius = new CornerRadius(10),
            Background = Theme.WellBrush,
            BorderBrush = Theme.WellBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Child = preview,
        });

        var controls = new StackPanel { Margin = new Thickness(18, 0, 18, 12) };
        var tools = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var buttons = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        buttons.Children.Add(Gap(Theme.IconButton("RotateLeft", "Rotate left", () => { a.Rotation = (a.Rotation + 270) % 360; Queue(); })));
        buttons.Children.Add(Gap(Theme.IconButton("Rotate", "Rotate right", () => { a.Rotation = (a.Rotation + 90) % 360; Queue(); })));
        buttons.Children.Add(Gap(Theme.IconButton("FlipH", "Flip horizontal", () => { a.FlipHorizontal = !a.FlipHorizontal; Queue(); })));
        buttons.Children.Add(Gap(Theme.IconButton("FlipV", "Flip vertical", () => { a.FlipVertical = !a.FlipVertical; Queue(); })));
        tools.Children.Add(buttons);
        sizeText.HorizontalAlignment = HorizontalAlignment.Right;
        tools.Children.Add(sizeText);
        controls.Children.Add(tools);

        controls.Children.Add(Track("Brightness", v => a.Brightness = v));
        controls.Children.Add(Track("Contrast", v => a.Contrast = v));
        controls.Children.Add(Track("Saturation", v => a.Saturation = v));
        controls.Children.Add(Track("Warmth", v => a.Warmth = v));
        controls.Children.Add(Theme.SliderRow("Size", 10, 200, 100, "%", v => { a.Scale = Math.Round(v); UpdateSize(); }, out scale, 84));
        Grid.SetRow(controls, 1);
        Body.Children.Add(controls);

        save = Theme.Primary("Save", Save);
        SetFooter(Theme.Soft("Reset", Reset), save);
        DefaultAction = Save;

        debounce.Tick += (_, _) => { debounce.Stop(); Render(); };
        Closed += (_, _) => small?.Dispose();
        Loaded += async (_, _) =>
        {
            try
            {
                (srcW, srcH) = await Task.Run(() => Preview.SourceSize(files[0]));
                small = await Task.Run(() => Preview.LoadMagick(files[0], 900));
                UpdateSize();
                Render();
            }
            catch (Exception e) { ShowError(Converter.Friendly(e)); }
        };
    }

    static Button Gap(Button b)
    {
        b.Margin = new Thickness(0, 0, 6, 0);
        return b;
    }

    Grid Track(string label, Action<double> set)
    {
        var row = Theme.SliderRow(label, -100, 100, 0, "", v => { set(Math.Round(v)); Queue(); }, out var s, 84);
        sliders.Add(s);
        return row;
    }

    void UpdateSize()
    {
        bool turned = a.Rotation % 180 != 0;
        double w = (turned ? srcH : srcW) * a.Scale / 100, h = (turned ? srcW : srcH) * a.Scale / 100;
        sizeText.Text = $"{Math.Round(w):0} × {Math.Round(h):0} px";
    }

    void Queue()
    {
        UpdateSize();
        debounce.Stop();
        debounce.Start();
    }

    async void Render()
    {
        if (small == null) return;
        if (busy) { again = true; return; }
        busy = true;
        int mine = ++version;
        var settings = Copy(a);
        settings.Scale = 100; // preview stays the same size; only the export is resized
        var source = small;
        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var img = source.Clone();
                ImageTools.Adjust(img, settings);
                return Converter.ToBitmap(img);
            });
            if (mine == version) preview.Source = bitmap;
        }
        catch (Exception e) { ShowError(Converter.Friendly(e)); }
        busy = false;
        if (again) { again = false; Render(); }
    }

    static Adjustments Copy(Adjustments x) => new()
    {
        Brightness = x.Brightness, Contrast = x.Contrast, Saturation = x.Saturation, Warmth = x.Warmth,
        Rotation = x.Rotation, FlipHorizontal = x.FlipHorizontal, FlipVertical = x.FlipVertical, Scale = x.Scale,
    };

    void Reset()
    {
        foreach (var s in sliders) s.Value = 0;
        scale!.Value = 100;
        a.Rotation = 0;
        a.FlipHorizontal = a.FlipVertical = false;
        Queue();
    }

    void Save()
    {
        var settings = Copy(a);
        SaveInBackground("Saving…", save, (_, _) => files.Select(f => ImageTools.Edit(f, settings)).ToList(), "Edited");
    }
}

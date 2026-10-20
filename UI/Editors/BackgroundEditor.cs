using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using FileWheel.Core;

namespace FileWheel.UI.Editors;

/// <summary>"Add BG": place the image on a gradient or color with padding, rounded corners and a shadow.</summary>
public sealed class BackgroundEditor : GlassPanel
{
    sealed class Look
    {
        public Backgrounds.Swatch Swatch = Backgrounds.Gradients[0];
        public Color? CustomColor;
        public string? CustomImage;
        public double Padding, Corners, Shadow;
        public string Ratio = "Auto";

        public Brush MakeBrush() => CustomColor is { } c ? new SolidColorBrush(c) : Swatch.Make();
    }

    readonly IReadOnlyList<string> files;
    readonly Look look = new();
    readonly Border previewBox;
    readonly Viewbox previewHost = new() { Stretch = Stretch.Uniform, Margin = new Thickness(10) };
    readonly WrapPanel swatches = new() { Width = 292 };
    readonly Button save;
    readonly List<(Border Ring, Func<bool> IsSelected)> rings = [];
    Slider? padding, corners, shadow;
    Func<string, bool> selectRatio = _ => false;
    BitmapSource? previewBitmap;
    BitmapSource? customImage;
    int srcW = 1, srcH = 1;
    (double P, double C, double S) defaults;

    public BackgroundEditor(IReadOnlyList<string> files) : base("Add BG", 420, 640)
    {
        this.files = files;
        Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        previewBox = new Border
        {
            Margin = new Thickness(18, 2, 18, 12),
            CornerRadius = new CornerRadius(10),
            Background = Theme.WellBrush,
            BorderBrush = Theme.WellBorderBrush,
            BorderThickness = new Thickness(1),
            Child = Preview.LoadingLabel(),
        };
        Body.Children.Add(previewBox);

        var controls = new StackPanel { Margin = new Thickness(18, 0, 18, 12) };
        var bgRow = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var bgLabel = Theme.Label("Background");
        bgLabel.Width = 86;
        bgLabel.VerticalAlignment = VerticalAlignment.Top;
        bgLabel.Margin = new Thickness(0, 4, 0, 0);
        bgRow.Children.Add(bgLabel);
        bgRow.Children.Add(swatches);
        controls.Children.Add(bgRow);
        BuildSwatches();

        controls.Children.Add(Theme.SliderRow("Padding", 0, 100, 0, "px", v => { look.Padding = v; Render(); }, out padding, 86));
        controls.Children.Add(Theme.SliderRow("Corners", 0, 100, 0, "px", v => { look.Corners = v; Render(); }, out corners, 86));
        controls.Children.Add(Theme.SliderRow("Shadow", 0, 100, 0, "px", v => { look.Shadow = v; Render(); }, out shadow, 86));

        var ratioRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        var ratioLabel = Theme.Label("Ratio");
        ratioLabel.Width = 86;
        ratioRow.Children.Add(ratioLabel);
        var seg = Theme.Segmented(["Auto", "1:1", "16:9", "4:5"], "Auto", r => { look.Ratio = r; Render(); }, out selectRatio);
        seg.HorizontalAlignment = HorizontalAlignment.Right;
        ratioRow.Children.Add(seg);
        controls.Children.Add(ratioRow);
        Grid.SetRow(controls, 1);
        Body.Children.Add(controls);

        save = Theme.Primary("Save with Background", Save);
        SetFooter(Theme.Soft("Reset", Reset), save);
        DefaultAction = Save;

        Loaded += async (_, _) =>
        {
            try
            {
                var preview = await Task.Run(() => Preview.Load(files[0], 900));
                previewBitmap = preview.Bitmap;
                srcW = preview.SourceWidth;
                srcH = preview.SourceHeight;
                double max = Math.Max(srcW, srcH), min = Math.Min(srcW, srcH);
                defaults = (Math.Round(max * 0.05), Math.Round(min * 0.04), Math.Round(Math.Max(8, max * 0.01)));
                padding!.Maximum = Math.Round(max * 0.4);
                corners!.Maximum = Math.Round(min * 0.25);
                shadow!.Maximum = Math.Round(Math.Max(40, max * 0.05));
                previewBox.Child = previewHost;
                Reset();
            }
            catch (Exception e) { ShowError(Converter.Friendly(e)); }
        };
    }

    void BuildSwatches()
    {
        foreach (var s in Backgrounds.Gradients) AddSwatch(s.Make(), s.Name, () => look.CustomColor == null && look.CustomImage == null && look.Swatch == s, () => { look.Swatch = s; look.CustomColor = null; look.CustomImage = null; });

        // Custom picture button at the end of the gradient row.
        var picture = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(5),
            Background = Theme.SurfaceBrush,
            Child = Theme.Icon("Image", 14, Theme.InkSoft),
            ToolTip = "Use a picture…",
        };
        AddRing(picture, () => look.CustomImage != null, PickImage, select: false);

        foreach (var s in Backgrounds.Solids.Take(9)) AddSwatch(s.Make(), s.Name, () => look.CustomColor == null && look.CustomImage == null && look.Swatch == s, () => { look.Swatch = s; look.CustomColor = null; look.CustomImage = null; });

        // Custom color pill.
        var spectrum = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        foreach (var (hex, at) in new[] { ("#3E7BF0", 0.0), ("#3E7BF0", 0.55), ("#7B5CF0", 1.0) }) spectrum.GradientStops.Add(new GradientStop(Theme.Hex(hex), at));
        var pill = new Border { Width = 30, Height = 12, CornerRadius = new CornerRadius(6), Background = spectrum, ToolTip = "Pick a color…", Margin = new Thickness(0, 4, 0, 4) };
        AddRing(pill, () => look.CustomColor != null, PickColor, select: false, radius: 9);
    }

    void AddSwatch(Brush brush, string name, Func<bool> isSelected, Action choose)
    {
        var chip = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(5),
            Background = brush,
            BorderBrush = Theme.Brush(Colors.White, 0.7),
            BorderThickness = new Thickness(1),
            ToolTip = name,
        };
        AddRing(chip, isSelected, choose, select: true);
    }

    void AddRing(Border chip, Func<bool> isSelected, Action choose, bool select, double radius = 7)
    {
        var ring = new Border
        {
            Padding = new Thickness(1),
            Margin = new Thickness(0, 0, 1, 2),
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(2),
            BorderBrush = Brushes.Transparent,
            Child = chip,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
        };
        ring.MouseLeftButtonUp += (_, _) =>
        {
            choose();
            if (select) Render();
        };
        rings.Add((ring, isSelected));
        swatches.Children.Add(ring);
    }

    void RefreshRings()
    {
        foreach (var (ring, isSelected) in rings) ring.BorderBrush = isSelected() ? Theme.Brush(Theme.Accent) : Brushes.Transparent;
    }

    void PickImage()
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "Pictures|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.avif", Title = "Choose a background picture" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            customImage = Preview.Load(picker.FileName, 1600).Bitmap;
            look.CustomImage = picker.FileName;
            look.CustomColor = null;
            Render();
        }
        catch (Exception e) { ShowError(Converter.Friendly(e)); }
    }

    void PickColor()
    {
        var picked = FileWheel.Native.ColorPicker.Pick(new System.Windows.Interop.WindowInteropHelper(this).Handle, look.CustomColor ?? Colors.White);
        if (picked is not { } color) return;
        look.CustomColor = color;
        look.CustomImage = null;
        Render();
    }

    void Reset()
    {
        look.Swatch = Backgrounds.Gradients[0];
        look.CustomColor = null;
        look.CustomImage = null;
        look.Ratio = "Auto";
        selectRatio("Auto");
        padding!.Value = defaults.P;
        corners!.Value = defaults.C;
        shadow!.Value = defaults.S;
        look.Padding = defaults.P;
        look.Corners = defaults.C;
        look.Shadow = defaults.S;
        Render();
    }

    void Render()
    {
        RefreshRings();
        if (previewBitmap == null) return;
        previewHost.Child = Compose(previewBitmap, srcW, srcH, look, look.MakeBrush(), customImage);
    }

    /// <summary>Builds the final picture at source resolution. Used for the live preview (inside a Viewbox) and for export.</summary>
    static FrameworkElement Compose(BitmapSource image, double width, double height, Look look, Brush background, BitmapSource? backgroundImage)
    {
        var (w, h) = CanvasSize(width, height, look);
        var root = new Grid { Width = w, Height = h, Background = background, ClipToBounds = true };
        if (backgroundImage != null) root.Children.Add(new Image { Source = backgroundImage, Stretch = Stretch.UniformToFill });
        double radius = Math.Min(look.Corners, Math.Min(width, height) / 2);
        if (look.Shadow > 0)
        {
            root.Children.Add(new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(radius),
                Background = Brushes.Black,
                Opacity = 0.38,
                Effect = new BlurEffect { Radius = look.Shadow * 2, KernelType = KernelType.Gaussian },
                RenderTransform = new TranslateTransform(0, look.Shadow * 0.45),
            });
        }
        var brush = new ImageBrush(image) { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        root.Children.Add(new Border { Width = width, Height = height, CornerRadius = new CornerRadius(radius), Background = brush });
        return root;
    }

    static (double W, double H) CanvasSize(double width, double height, Look look)
    {
        double w = width + look.Padding * 2, h = height + look.Padding * 2;
        double r = look.Ratio switch { "1:1" => 1, "16:9" => 16 / 9.0, "4:5" => 0.8, _ => 0 };
        if (r > 0)
        {
            if (w / h < r) w = h * r;
            else h = w / r;
        }
        return (Math.Round(w), Math.Round(h));
    }

    void Save()
    {
        var snapshot = new Look
        {
            Swatch = look.Swatch, CustomColor = look.CustomColor, CustomImage = look.CustomImage,
            Padding = look.Padding, Corners = look.Corners, Shadow = look.Shadow, Ratio = look.Ratio,
        };
        SaveInBackground("Saving…", save, (_, _) => files.Select(f => Export(f, snapshot)).ToList(), "Background added");
    }

    /// <summary>Command-line test hook: exports with the default look.</summary>
    internal static string TestExport(string file)
    {
        var (w, h) = Preview.SourceSize(file);
        return Export(file, new Look { Padding = Math.Round(Math.Max(w, h) * 0.05), Corners = Math.Round(Math.Min(w, h) * 0.04), Shadow = 24, Ratio = "1:1" });
    }

    /// <summary>Renders at full resolution on a private UI thread so the panel stays responsive.</summary>
    static string Export(string file, Look look)
    {
        string? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var full = Converter.LoadImage(file);
                double width = full.Width, height = full.Height;
                var bitmap = Converter.ToBitmap(full);
                BitmapSource? bg = look.CustomImage != null ? Preview.Load(look.CustomImage, 4000).Bitmap : null;
                var (w, h) = CanvasSize(width, height, look);
                double fit = Math.Min(1, Math.Sqrt(60_000_000 / (w * h)));
                var element = Compose(bitmap, width, height, look, look.MakeBrush(), bg);
                var host = new Viewbox { Child = element, Width = Math.Round(w * fit), Height = Math.Round(h * fit), Stretch = Stretch.Fill };
                host.Measure(new Size(host.Width, host.Height));
                host.Arrange(new Rect(0, 0, host.Width, host.Height));
                host.UpdateLayout();
                var rtb = new RenderTargetBitmap((int)host.Width, (int)host.Height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(host);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                result = Output.Save(file, " With Background", "png", temp =>
                {
                    using var fs = File.Create(temp);
                    encoder.Save(fs);
                });
            }
            catch (Exception e) { error = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw error;
        return result!;
    }
}

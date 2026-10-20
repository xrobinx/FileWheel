using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace FileWheel.UI;

/// <summary>Colors, icons and small control builders shared by every surface.</summary>
public static class Theme
{
    /// <summary>
    /// The palette of the surface being built. Each wheel/panel/toast picks its palette from what's
    /// behind it right before building its content, so everything created during that build matches.
    /// </summary>
    public static Palette Current { get; set; } = Palette.Tangerine;

    public static Color Accent => Current.Accent;
    public static Color Ink => Current.Ink;
    public static Color InkSoft => Current.InkSoft;
    public static Color InkMuted => Current.InkMuted;

    public static Brush SurfaceBrush => Brush(Current.Surface);
    public static Brush SurfaceBorderBrush => Brush(Current.SurfaceBorder);
    public static Brush WellBrush => Brush(Current.Well);
    public static Brush WellBorderBrush => Brush(Current.WellBorder);
    public static Brush TrackBrush => Brush(Current.Track);

    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");

    public static SolidColorBrush Brush(Color c, double opacity = 1)
    {
        var b = new SolidColorBrush(c) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    public static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static Brush HoverFill()
    {
        var b = new LinearGradientBrush(Current.HoverTop, Current.HoverBottom, 90);
        b.Freeze();
        return b;
    }

    public static Style Style(string key) => (Style)Application.Current.FindResource(key);

    public static TextBlock Text(string text, double size = 12.5, Color? color = null, FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        FontFamily = UiFont,
        Foreground = Brush(color ?? Ink),
        FontWeight = weight ?? FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Label(string text) => Text(text, 12, InkMuted, FontWeights.Medium);

    public static Button Primary(string text, Action click)
    {
        var b = new Button { Content = text, Style = Style("PrimaryButton") };
        b.Click += (_, _) => click();
        return b;
    }

    public static Button Soft(string text, Action click)
    {
        var b = new Button { Content = text, Style = Style("SoftButton") };
        b.Click += (_, _) => click();
        return b;
    }

    public static Button IconButton(string icon, string tip, Action click)
    {
        var b = new Button { Content = Icon(icon, 15, InkSoft), Style = Style("IconButton"), ToolTip = tip };
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>Segmented control: a translucent capsule of options with an orange selected pill.</summary>
    public static Border Segmented(IEnumerable<string> options, string selected, Action<string> changed, out Func<string, bool> select)
    {
        var group = "seg" + Guid.NewGuid().ToString("N");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new Dictionary<string, RadioButton>();
        bool first = true;
        foreach (var option in options)
        {
            if (!first)
                row.Children.Add(new Rectangle { Width = 1, Height = 12, Fill = Brush(InkSoft, 0.28), Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center });
            first = false;
            var value = option;
            var rb = new RadioButton { Content = option, GroupName = group, Style = Style("SegmentButton"), IsChecked = option == selected };
            rb.Checked += (_, _) => changed(value);
            buttons[option] = rb;
            row.Children.Add(rb);
        }
        select = v =>
        {
            if (!buttons.TryGetValue(v, out var b)) return false;
            b.IsChecked = true;
            return true;
        };
        return new Border
        {
            Background = SurfaceBrush,
            BorderBrush = SurfaceBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(2),
            Child = row,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    /// <summary>"Padding ───●──── 160 px" row.</summary>
    public static Grid SliderRow(string label, double min, double max, double value, string unit, Action<double> changed, out Slider slider, double labelWidth = 70)
    {
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(62) });
        grid.Children.Add(Label(label));
        var s = new Slider { Minimum = min, Maximum = Math.Max(min + 1, max), Value = value, Style = Style("GlassSlider") };
        Grid.SetColumn(s, 1);
        grid.Children.Add(s);
        var display = Text(Format(value, unit), 11.5, InkMuted);
        display.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(display, 2);
        grid.Children.Add(display);
        s.ValueChanged += (_, e) =>
        {
            display.Text = Format(e.NewValue, unit);
            changed(e.NewValue);
        };
        slider = s;
        return grid;
    }

    static string Format(double v, string unit) => unit switch
    {
        "%" => $"{Math.Round(v):0}%",
        "" => $"{Math.Round(v):0}",
        _ => $"{Math.Round(v):0} {unit}",
    };

    public static Rectangle Divider() => new() { Height = 1, Fill = Brush(Current.Hairline), SnapsToDevicePixels = true };

    // ---------- Icons (16×16 stroked line art) ----------

    static readonly Dictionary<string, string> IconData = new()
    {
        ["Compress"] = "M2,2 L6.6,6.6 M6.8,3 L6.8,6.8 L3,6.8 M14,14 L9.4,9.4 M9.2,13 L9.2,9.2 L13,9.2",
        ["Metadata"] = "M2,3.2 C2,2.5 2.5,2 3.2,2 L8,2 L14.1,8.1 C14.6,8.6 14.6,9.3 14.1,9.8 L9.8,14.1 C9.3,14.6 8.6,14.6 8.1,14.1 L2,8 Z M6.5,5 A1.5,1.5 0 1 1 3.5,5 A1.5,1.5 0 1 1 6.5,5 Z",
        ["Edit"] = "M2,3.5 L9,3.5 M12,3.5 L14,3.5 M10.5,2 L10.5,5 M2,8 L4,8 M7,8 L14,8 M5.5,6.5 L5.5,9.5 M2,12.5 L8,12.5 M11,12.5 L14,12.5 M9.5,11 L9.5,14",
        ["Annotate"] = "M14.3,8 A6.3,6.3 0 1 1 1.7,8 A6.3,6.3 0 1 1 14.3,8 Z M5.2,10.8 L5.6,9 L9.9,4.7 L11.3,6.1 L7,10.4 Z",
        ["AddBackground"] = "M2.8,3 L13.2,3 C13.9,3 14.5,3.6 14.5,4.3 L14.5,11.7 C14.5,12.4 13.9,13 13.2,13 L2.8,13 C2.1,13 1.5,12.4 1.5,11.7 L1.5,4.3 C1.5,3.6 2.1,3 2.8,3 Z M2,11.6 L5.6,8 L8.2,10.6 L10.2,8.6 L14,12.2 M11.8,6.1 A1,1 0 1 1 9.8,6.1 A1,1 0 1 1 11.8,6.1 Z",
        ["Crop"] = "M4,1 L4,12 L15,12 M1,4 L12,4 L12,15",
        ["Redact"] = "M1.5,8 C3.3,4.8 5.5,3.5 8,3.5 C10.5,3.5 12.7,4.8 14.5,8 C12.7,11.2 10.5,12.5 8,12.5 C5.5,12.5 3.3,11.2 1.5,8 Z M10,8 A2,2 0 1 1 6,8 A2,2 0 1 1 10,8 Z M2.5,14 L13.5,2",
        ["Rotate"] = "M13.2,8.5 A5.2,5.2 0 1 1 10.8,3.6 M10.6,1 L10.9,3.8 L8.2,4.4",
        ["RotateLeft"] = "M2.8,8.5 A5.2,5.2 0 1 0 5.2,3.6 M5.4,1 L5.1,3.8 L7.8,4.4",
        ["FlipH"] = "M8,1.5 L8,14.5 M6,3.5 L2,12.5 L6,12.5 Z M10,3.5 L14,12.5 L10,12.5 Z",
        ["FlipV"] = "M1.5,8 L14.5,8 M3.5,6 L12.5,2 L12.5,6 Z M3.5,10 L12.5,14 L12.5,10 Z",
        ["Split"] = "M2,2.5 L6.8,2.5 L6.8,13.5 L2,13.5 Z M9.2,2.5 L14,2.5 L14,13.5 L9.2,13.5 Z",
        ["Extract"] = "M9.5,14.5 L3,14.5 L3,1.5 L9,1.5 L12,4.5 L12,7 M8,10.5 L15,10.5 M12.5,8 L15,10.5 L12.5,13",
        ["Merge"] = "M5.5,1.5 L13,1.5 L13,11 M3,4 L10.5,4 L10.5,14.5 L3,14.5 Z",
        ["Trim"] = "M6,4 A2,2 0 1 1 2,4 A2,2 0 1 1 6,4 Z M6,12 A2,2 0 1 1 2,12 A2,2 0 1 1 6,12 Z M5.6,5.2 L14,12.6 M5.6,10.8 L14,3.4",
        ["Mute"] = "M2,6 L4.6,6 L8,3 L8,13 L4.6,10 L2,10 Z M10.5,6 L14.5,10 M14.5,6 L10.5,10",
        ["Share"] = "M8,10 L8,1.5 M5,4.5 L8,1.5 L11,4.5 M5.5,6.8 L3.5,6.8 L3.5,14.5 L12.5,14.5 L12.5,6.8 L10.5,6.8",
        ["Float"] = "M2.3,3 L13.7,3 C14.1,3 14.5,3.4 14.5,3.8 L14.5,12.2 C14.5,12.6 14.1,13 13.7,13 L2.3,13 C1.9,13 1.5,12.6 1.5,12.2 L1.5,3.8 C1.5,3.4 1.9,3 2.3,3 Z M8,8 L12.8,8 L12.8,11.3 L8,11.3 Z",
        ["Copy"] = "M5.5,5 L13.5,5 L13.5,14.5 L5.5,14.5 Z M3.5,11.5 L2.5,11.5 L2.5,1.5 L10.5,1.5 L10.5,3",
        ["Move"] = "M1.5,3.5 L6,3.5 L7.5,5 L14.5,5 L14.5,13.5 L1.5,13.5 Z M5.5,9.3 L10.8,9.3 M8.8,7.3 L10.8,9.3 L8.8,11.3",
        ["CopyPath"] = "M6.8,9.2 L9.2,6.8 M6,7.4 L4.4,9 C3.5,9.9 3.5,11.3 4.4,12.2 C5.3,13.1 6.7,13.1 7.6,12.2 L9.2,10.6 M10,8.6 L11.6,7 C12.5,6.1 12.5,4.7 11.6,3.8 C10.7,2.9 9.3,2.9 8.4,3.8 L6.8,5.4",
        ["Terminal"] = "M2.3,2.5 L13.7,2.5 C14.1,2.5 14.5,2.9 14.5,3.3 L14.5,12.7 C14.5,13.1 14.1,13.5 13.7,13.5 L2.3,13.5 C1.9,13.5 1.5,13.1 1.5,12.7 L1.5,3.3 C1.5,2.9 1.9,2.5 2.3,2.5 Z M4.2,6 L6.6,8 L4.2,10 M8,10.4 L11.6,10.4",
        ["Undo"] = "M5,3 L2,6 L5,9 M2.5,6 L10,6 C12.2,6 14,7.8 14,10 C14,12.2 12.2,14 10,14 L6,14",
        ["Pen"] = "M2.5,13.5 L3.2,10.4 L10.6,3 C11.2,2.4 12.1,2.4 12.7,3 L13,3.3 C13.6,3.9 13.6,4.8 13,5.4 L5.6,12.8 Z M9.4,4.2 L11.8,6.6",
        ["Arrow"] = "M2.5,13.5 L13,3 M7,3 L13,3 L13,9",
        ["Box"] = "M2.5,3.5 L13.5,3.5 L13.5,12.5 L2.5,12.5 Z",
        ["TextTool"] = "M3,3.5 L13,3.5 M8,3.5 L8,13.5 M3,3.5 L3,5 M13,3.5 L13,5 M6.2,13.5 L9.8,13.5",
        ["Highlight"] = "M2,14 L14,14 M4.5,11.5 L4.5,9 L10.5,3 L13,5.5 L7,11.5 Z",
        ["Image"] = "M2.8,3 L13.2,3 C13.9,3 14.5,3.6 14.5,4.3 L14.5,11.7 C14.5,12.4 13.9,13 13.2,13 L2.8,13 C2.1,13 1.5,12.4 1.5,11.7 L1.5,4.3 C1.5,3.6 2.1,3 2.8,3 Z M2,11.6 L5.6,8 L8.2,10.6 L10.2,8.6 L14,12.2 M11.8,6.1 A1,1 0 1 1 9.8,6.1 A1,1 0 1 1 11.8,6.1 Z",
        ["Folder"] = "M1.5,3.5 L6,3.5 L7.5,5 L14.5,5 L14.5,13.5 L1.5,13.5 Z",
        ["Check"] = "M3,8.5 L6.5,12 L13,4.5",
        ["Back"] = "M9.5,3 L4.5,8 L9.5,13 M4.8,8 L13,8",
        ["Plus"] = "M8,2.5 L8,13.5 M2.5,8 L13.5,8",
        ["Speed"] = "M2.2,12 A6.2,6.2 0 1 1 13.8,12 M8,11 L11,6.5",
        ["Snapshot"] = "M2,5.2 L4.6,5.2 L5.8,3.4 L10.2,3.4 L11.4,5.2 L14,5.2 L14,13 L2,13 Z M10.6,9 A2.6,2.6 0 1 1 5.4,9 A2.6,2.6 0 1 1 10.6,9 Z",
        ["Normalize"] = "M2.5,6.5 L2.5,9.5 M5,4 L5,12 M7.5,5.5 L7.5,10.5 M10,3 L10,13 M12.5,5.5 L12.5,10.5",
        ["Visualize"] = "M2,3 L14,3 L14,13 L2,13 Z M4,8 L5.5,8 L6.5,5.5 L8,10.5 L9.5,6.5 L10.5,8 L12,8",
        ["Channels"] = "M2.5,3 L6.5,3 L6.5,13 L2.5,13 Z M9.5,3 L13.5,3 L13.5,13 L9.5,13 Z M4.5,9.5 L4.5,10.5 M11.5,9.5 L11.5,10.5",
        ["Bleep"] = "M1.5,8 L4,8 L5,5 L6.5,11 L8,3 L9.5,13 L11,6 L12,8 L14.5,8",
        ["Organize"] = "M2.5,2.5 L6.8,2.5 L6.8,6.8 L2.5,6.8 Z M9.2,2.5 L13.5,2.5 L13.5,6.8 L9.2,6.8 Z M2.5,9.2 L6.8,9.2 L6.8,13.5 L2.5,13.5 Z M9.2,9.2 L13.5,9.2 L13.5,13.5 L9.2,13.5 Z",
        ["Unpack"] = "M2,3.5 L14,3.5 L14,6.3 L2,6.3 Z M3,6.3 L3,13.5 L13,13.5 L13,6.3 M8,7.8 L8,12 M6.2,10.2 L8,12 L9.8,10.2",
        ["Delete"] = "M3,4.5 L13,4.5 M6.2,4.5 L6.2,3 L9.8,3 L9.8,4.5 M4.3,4.5 L5,13.5 L11,13.5 L11.7,4.5",
        ["Search"] = "M11,6.8 A4.2,4.2 0 1 1 2.6,6.8 A4.2,4.2 0 1 1 11,6.8 Z M9.9,9.9 L14,14",
        ["Warning"] = "M8,2 L15,14 L1,14 Z M8,6.5 L8,10 M8,12 L8,12.2",
    };

    public static Path Icon(string name, double size, Color color, double thickness = 1.35)
    {
        var path = new Path
        {
            Data = Geometry.Parse(IconData.TryGetValue(name, out var d) ? d : IconData["Image"]),
            Stroke = Brush(color),
            StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            // A fixed 16×16 box keeps icons centered even when the path doesn't touch every edge.
            Width = 16,
            Height = 16,
            Stretch = Stretch.None,
            LayoutTransform = new ScaleTransform(size / 16.0, size / 16.0),
        };
        return path;
    }

    public static bool HasIcon(string name) => IconData.ContainsKey(name);

    // ---------- Motion ----------

    /// <summary>Runs <paramref name="action"/> on the UI thread after <paramref name="ms"/> milliseconds.</summary>
    public static void After(double ms, Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    public static IEasingFunction EaseOut { get; } = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static IEasingFunction Spring { get; } = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.32 });

    static T Freeze<T>(T e) where T : Freezable { e.Freeze(); return e; }

    public static void Animate(Animatable target, DependencyProperty property, double to, double ms, IEasingFunction? ease = null, double? from = null, double delayMs = 0, Action? done = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? EaseOut, BeginTime = TimeSpan.FromMilliseconds(delayMs) };
        if (from.HasValue) a.From = from;
        if (done != null) a.Completed += (_, _) => done();
        target.BeginAnimation(property, a);
    }

    public static void Animate(UIElement target, DependencyProperty property, double to, double ms, IEasingFunction? ease = null, double? from = null, double delayMs = 0, Action? done = null)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease ?? EaseOut, BeginTime = TimeSpan.FromMilliseconds(delayMs) };
        if (from.HasValue) a.From = from;
        if (done != null) a.Completed += (_, _) => done();
        target.BeginAnimation(property, a);
    }
}

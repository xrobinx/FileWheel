using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FileWheel.Core;

namespace FileWheel.UI.Editors;

/// <summary>A drawing on the image, stored in 0–1 image coordinates so it renders identically at any size.</summary>
public abstract record Mark(Color Color, double Width)
{
    public abstract void Draw(DrawingContext dc, Size size);

    protected Pen Pen(Size size, double alpha = 1) => new(new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), Color.R, Color.G, Color.B)), Math.Max(1, Width * size.Width))
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
        LineJoin = PenLineJoin.Round,
    };

    protected static Point At(Point p, Size s) => new(p.X * s.Width, p.Y * s.Height);
}

public sealed record InkMark(List<Point> Points, Color Color, double Width, bool Highlighter) : Mark(Color, Width)
{
    public override void Draw(DrawingContext dc, Size size)
    {
        if (Points.Count == 0) return;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(At(Points[0], size), false, false);
            if (Points.Count == 1) c.LineTo(At(Points[0], size) + new Vector(0.1, 0), true, false);
            c.PolyLineTo(Points.Skip(1).Select(p => At(p, size)).ToList(), true, true);
        }
        var pen = Pen(size, Highlighter ? 0.42 : 1);
        if (Highlighter) pen.Thickness *= 3.2;
        dc.DrawGeometry(null, pen, g);
    }
}

public sealed record ArrowMark(Point From, Point To, Color Color, double Width) : Mark(Color, Width)
{
    public override void Draw(DrawingContext dc, Size size)
    {
        var a = At(From, size);
        var b = At(To, size);
        var pen = Pen(size);
        var dir = b - a;
        if (dir.Length < 1) return;
        dir.Normalize();
        double head = Math.Max(10, pen.Thickness * 4.2);
        var back = b - dir * head;
        var normal = new Vector(-dir.Y, dir.X) * head * 0.55;
        dc.DrawLine(pen, a, back + dir * (head * 0.3));
        var tri = new StreamGeometry();
        using (var c = tri.Open())
        {
            c.BeginFigure(b, true, true);
            c.LineTo(back + normal, true, true);
            c.LineTo(back - normal, true, true);
        }
        dc.DrawGeometry(pen.Brush, new Pen(pen.Brush, pen.Thickness * 0.5) { LineJoin = PenLineJoin.Round }, tri);
    }
}

public sealed record BoxMark(Rect Box, Color Color, double Width) : Mark(Color, Width)
{
    public override void Draw(DrawingContext dc, Size size)
    {
        var r = new Rect(At(Box.TopLeft, size), At(Box.BottomRight, size));
        double radius = Math.Min(r.Width, r.Height) * 0.06;
        dc.DrawRoundedRectangle(null, Pen(size), r, radius, radius);
    }
}

public sealed record TextMark(Point Position, string Text, Color Color, double Width) : Mark(Color, Width)
{
    public override void Draw(DrawingContext dc, Size size)
    {
        double em = Math.Max(10, Width * size.Width * 6);
        var ft = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(Theme.UiFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), em, new SolidColorBrush(Color), 1);
        var geometry = ft.BuildGeometry(At(Position, size));
        bool dark = Color.R * 0.3 + Color.G * 0.59 + Color.B * 0.11 < 140;
        dc.DrawGeometry(null, new Pen(dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(30, 30, 30)), em * 0.12) { LineJoin = PenLineJoin.Round }, geometry);
        dc.DrawGeometry(new SolidColorBrush(Color), null, geometry);
    }
}

/// <summary>Draws a list of marks over its own area.</summary>
public sealed class MarkLayer : FrameworkElement
{
    public List<Mark> Marks { get; } = [];
    public Mark? Live { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var size = new Size(ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(size));
        foreach (var m in Marks) m.Draw(dc, size);
        Live?.Draw(dc, size);
    }
}

/// <summary>"Annotate": pen, highlighter, arrows, boxes and text in a few colors.</summary>
public sealed class AnnotateEditor : GlassPanel
{
    const double StageW = 364, StageH = 380;
    static readonly string[] Swatches = ["#F2412A", "#F7931E", "#F7D23A", "#34C759", "#2F7BF6", "#1E1E1E", "#FFFFFF"];

    readonly string file;
    readonly Grid stage = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly Image image = new() { Stretch = Stretch.Fill };
    readonly MarkLayer layer = new() { Cursor = Cursors.Pen };
    readonly Canvas textHost = new();
    readonly Button save;
    readonly List<(Border Ring, string Hex)> colorRings = [];
    string tool = "Pen";
    Color color = Theme.Hex(Swatches[0]);
    double width = 0.006;
    Point start;
    bool drawing;
    int srcW, srcH;

    public AnnotateEditor(string file) : base("Annotate", 420, 640)
    {
        this.file = file;
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Tool bar.
        var bar = new DockPanel { Margin = new Thickness(18, 0, 18, 10) };
        var toolsRow = new StackPanel { Orientation = Orientation.Horizontal };
        var group = "tools" + GetHashCode();
        foreach (var (name, icon) in new[] { ("Pen", "Pen"), ("Highlight", "Highlight"), ("Arrow", "Arrow"), ("Box", "Box"), ("Text", "TextTool") })
        {
            var rb = new RadioButton
            {
                GroupName = group,
                Style = Theme.Style("SegmentButton"),
                Content = Theme.Icon(icon, 15, Theme.InkSoft),
                ToolTip = name,
                IsChecked = name == tool,
                Padding = new Thickness(7, 4, 7, 4),
            };
            var value = name;
            rb.Checked += (_, _) => { tool = value; layer.Cursor = value == "Text" ? Cursors.IBeam : Cursors.Pen; };
            rb.Checked += (_, _) => Recolor(rb, true);
            rb.Unchecked += (_, _) => Recolor(rb, false);
            if (name == tool) Recolor(rb, true);
            toolsRow.Children.Add(rb);
        }
        var toolsBox = new Border
        {
            Background = Theme.SurfaceBrush, BorderBrush = Theme.SurfaceBorderBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(2), Child = toolsRow,
        };
        bar.Children.Add(toolsBox);
        var undo = Theme.IconButton("Undo", "Undo (Ctrl+Z)", Undo);
        undo.HorizontalAlignment = HorizontalAlignment.Right;
        bar.Children.Add(undo);
        Body.Children.Add(bar);

        // Stage.
        stage.Children.Add(image);
        stage.Children.Add(layer);
        stage.Children.Add(textHost);
        var host = new Grid { Margin = new Thickness(18, 0, 18, 10) };
        host.Children.Add(Preview.LoadingLabel());
        host.Children.Add(stage);
        Grid.SetRow(host, 1);
        Body.Children.Add(host);

        // Colors + thickness.
        var bottom = new StackPanel { Margin = new Thickness(18, 0, 18, 12) };
        var colors = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var colorLabel = Theme.Label("Color");
        colorLabel.Width = 76;
        colors.Children.Add(colorLabel);
        foreach (var hex in Swatches)
        {
            var dot = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Theme.Brush(Theme.Hex(hex)),
                BorderBrush = Theme.Brush(Colors.Black, 0.12), BorderThickness = new Thickness(1),
            };
            var ring = new Border
            {
                Child = dot, Padding = new Thickness(1.5), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(2),
                BorderBrush = hex == Swatches[0] ? Theme.Brush(Theme.Accent) : Brushes.Transparent, Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand, Background = Brushes.Transparent,
            };
            var value = hex;
            ring.MouseLeftButtonUp += (_, _) =>
            {
                color = Theme.Hex(value);
                foreach (var (r, h) in colorRings) r.BorderBrush = h == value ? Theme.Brush(Theme.Accent) : Brushes.Transparent;
            };
            colorRings.Add((ring, hex));
            colors.Children.Add(ring);
        }
        bottom.Children.Add(colors);
        bottom.Children.Add(Theme.SliderRow("Size", 1, 20, 6, "", v => width = v / 1000.0, out _, 76));
        Grid.SetRow(bottom, 2);
        Body.Children.Add(bottom);

        save = Theme.Primary("Save", Save);
        SetFooter(Theme.Soft("Clear", () => { layer.Marks.Clear(); layer.InvalidateVisual(); }), save);

        layer.MouseLeftButtonDown += Down;
        layer.MouseMove += Move;
        layer.MouseLeftButtonUp += Up;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo(); e.Handled = true; } };

        Loaded += async (_, _) =>
        {
            try
            {
                var p = await Task.Run(() => Preview.Load(file, 1400));
                srcW = p.SourceWidth;
                srcH = p.SourceHeight;
                image.Source = p.Bitmap;
                var fit = Preview.Fit(srcW, srcH, StageW, StageH);
                stage.Width = fit.Width;
                stage.Height = fit.Height;
                host.Children.RemoveAt(0);
            }
            catch (Exception e) { ShowError(Converter.Friendly(e)); }
        };
    }

    static void Recolor(RadioButton rb, bool on)
    {
        if (rb.Content is System.Windows.Shapes.Path p) p.Stroke = on ? Brushes.White : Theme.Brush(Theme.InkSoft);
    }

    Point Norm(MouseEventArgs e)
    {
        var p = e.GetPosition(layer);
        return new Point(Math.Clamp(p.X / layer.ActualWidth, 0, 1), Math.Clamp(p.Y / layer.ActualHeight, 0, 1));
    }

    void Down(object sender, MouseButtonEventArgs e)
    {
        var p = Norm(e);
        if (tool == "Text") { BeginText(e.GetPosition(layer), p); return; }
        start = p;
        drawing = true;
        layer.Live = tool switch
        {
            "Pen" or "Highlight" => new InkMark([p], color, width, tool == "Highlight"),
            "Arrow" => new ArrowMark(p, p, color, width),
            _ => new BoxMark(new Rect(p, p), color, width),
        };
        layer.CaptureMouse();
        layer.InvalidateVisual();
    }

    void Move(object sender, MouseEventArgs e)
    {
        if (!drawing || layer.Live == null) return;
        var p = Norm(e);
        layer.Live = layer.Live switch
        {
            InkMark ink => AddPoint(ink, p),
            ArrowMark arrow => arrow with { To = p },
            BoxMark box => box with { Box = new Rect(start, p) },
            _ => layer.Live,
        };
        layer.InvalidateVisual();
    }

    static InkMark AddPoint(InkMark ink, Point p)
    {
        ink.Points.Add(p);
        return ink;
    }

    void Up(object sender, MouseButtonEventArgs e)
    {
        if (!drawing) return;
        drawing = false;
        layer.ReleaseMouseCapture();
        if (layer.Live != null) layer.Marks.Add(layer.Live);
        layer.Live = null;
        layer.InvalidateVisual();
    }

    void BeginText(Point at, Point norm)
    {
        double em = Math.Max(10, width * layer.ActualWidth * 6);
        var box = new TextBox
        {
            MinWidth = 60,
            FontSize = em,
            FontWeight = FontWeights.Bold,
            FontFamily = Theme.UiFont,
            Foreground = new SolidColorBrush(color),
            Background = Theme.Brush(Colors.White, 0.35),
            BorderThickness = new Thickness(0),
            CaretBrush = new SolidColorBrush(color),
            Padding = new Thickness(0),
        };
        Canvas.SetLeft(box, at.X);
        Canvas.SetTop(box, at.Y);
        textHost.Children.Add(box);
        box.Focus();
        var chosen = color;
        double w = width;
        void Commit()
        {
            if (!textHost.Children.Contains(box)) return;
            textHost.Children.Remove(box);
            if (!string.IsNullOrWhiteSpace(box.Text)) layer.Marks.Add(new TextMark(norm, box.Text, chosen, w));
            layer.InvalidateVisual();
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            if (e.Key == Key.Escape) { box.Text = ""; Commit(); e.Handled = true; }
        };
    }

    void Undo()
    {
        if (layer.Marks.Count == 0) return;
        layer.Marks.RemoveAt(layer.Marks.Count - 1);
        layer.InvalidateVisual();
    }

    void Save()
    {
        if (layer.Marks.Count == 0) { ShowError("Draw something first."); return; }
        var marks = layer.Marks.ToList();
        SaveInBackground("Saving…", save, (_, _) => [Export(file, marks)], "Annotated");
    }

    /// <summary>Command-line test hook.</summary>
    internal static string TestExport(string file) => Export(file,
    [
        new ArrowMark(new Point(0.1, 0.1), new Point(0.45, 0.4), Theme.Hex("#F2412A"), 0.006),
        new BoxMark(new Rect(0.55, 0.15, 0.3, 0.25), Theme.Hex("#2F7BF6"), 0.006),
        new TextMark(new Point(0.1, 0.7), "Tangerine!", Theme.Hex("#F7D23A"), 0.006),
        new InkMark([new Point(0.5, 0.8), new Point(0.6, 0.75), new Point(0.7, 0.85)], Theme.Hex("#34C759"), 0.006, true),
    ]);

    static string Export(string file, List<Mark> marks)
    {
        string? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var full = Converter.LoadImage(file);
                var bitmap = Converter.ToBitmap(full);
                var size = new Size(full.Width, full.Height);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawImage(bitmap, new Rect(size));
                    foreach (var m in marks) m.Draw(dc, size);
                }
                var rtb = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rtb));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                var ext = ImageTools.OutputExt(file);
                using var drawn = new ImageMagick.MagickImage(ms.ToArray());
                result = Output.Save(file, " Annotated", ext, temp => Converter.WriteImage(drawn, ext, temp, ext == "jpg" ? 94 : 0));
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

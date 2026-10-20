using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FileWheel.Core;
using Path = System.Windows.Shapes.Path;

namespace FileWheel.UI;

/// <summary>
/// The radial menu: a frosted warm disc with rounded wedges, an orange highlight that follows the
/// pointer and a center pill showing the file size or the hovered choice.
/// </summary>
public sealed class WheelView : Canvas
{
    public const double ViewSize = 380, Cx = 190, Cy = 190;
    public const double DiscR = 158, OuterR = 149, InnerR = 64, CenterR = 53;
    const double Gap = 7, Corner = 12;

    sealed class Slice
    {
        public required Canvas Group;
        public required Path Hot;
        public required SolidColorBrush Ink;
        public required Color IdleInk;
        public required ScaleTransform Pop;
    }

    readonly Canvas root = new() { Width = ViewSize, Height = ViewSize };
    readonly ScaleTransform rootScale = new(1, 1, Cx, Cy);
    readonly Image backdrop = new() { Stretch = Stretch.Fill, Width = DiscR * 2, Height = DiscR * 2 };
    readonly Canvas wedgeLayer = new() { Width = ViewSize, Height = ViewSize };
    readonly TextBlock pillText, centerGlyph;
    readonly Border pill;
    readonly Ellipse shadow, tint, sheen, rim, innerRim, ring, ringFill;
    readonly List<Slice> slices = [];
    Palette palette = Theme.Current;
    int hover = -1;
    string idleText = "";
    string? centerLabel;

    public IReadOnlyList<WheelItem> Items { get; private set; } = [];
    public int Hover => hover;

    public WheelView()
    {
        Width = Height = ViewSize;
        Children.Add(root);
        root.RenderTransform = rootScale;

        // Soft shadow under the disc.
        shadow = new Ellipse
        {
            Width = DiscR * 2 - 10,
            Height = DiscR * 2 - 10,
            Fill = Brushes.Black,
            Opacity = 0.30,
            Effect = new BlurEffect { Radius = 28, RenderingBias = RenderingBias.Performance },
        };
        Place(shadow, Cx - DiscR + 5, Cy - DiscR + 14);
        root.Children.Add(shadow);

        // Frosted backdrop: what's behind the wheel, blurred, clipped to the disc.
        var glass = new Grid { Width = DiscR * 2, Height = DiscR * 2, Clip = new EllipseGeometry(new Point(DiscR, DiscR), DiscR, DiscR) };
        backdrop.Effect = new BlurEffect { Radius = 16, RenderingBias = RenderingBias.Performance };
        glass.Children.Add(backdrop);
        tint = new Ellipse();
        sheen = new Ellipse();
        glass.Children.Add(tint);
        glass.Children.Add(sheen);
        Place(glass, Cx - DiscR, Cy - DiscR);
        root.Children.Add(glass);

        // Glass rim.
        rim = Circle(DiscR - 0.75, null, null, 1.5);
        innerRim = Circle(DiscR - 3.5, null, null, 2);
        root.Children.Add(rim);
        root.Children.Add(innerRim);

        root.Children.Add(wedgeLayer);

        // Center: a glass ring holding the pill.
        ring = Circle(CenterR, null, null, 1.3);
        ringFill = Circle(CenterR - 5, null, null, 0);
        root.Children.Add(ring);
        root.Children.Add(ringFill);
        pillText = new TextBlock
        {
            FontFamily = Theme.UiFont,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(8, 0, 8, 1),
        };
        pill = new Border
        {
            Width = 90,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            BorderThickness = new Thickness(1),
            Child = pillText,
        };
        Place(pill, Cx - 45, Cy - 17);
        root.Children.Add(pill);

        // A small × (or ←) above the pill: letting go in the middle cancels (or goes back).
        centerGlyph = new TextBlock
        {
            FontFamily = Theme.UiFont,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Width = 40,
            TextAlignment = TextAlignment.Center,
            Text = "×",
            IsHitTestVisible = false,
        };
        Place(centerGlyph, Cx - 20, Cy - 43);
        root.Children.Add(centerGlyph);
        ApplyPalette(Theme.Current);
    }

    /// <summary>Recolors the glass for what's behind it. Wedges built afterwards use the same palette.</summary>
    public void ApplyPalette(Palette p)
    {
        palette = p;
        shadow.Opacity = p.Dark ? 0.45 : 0.28;
        tint.Fill = p.DiscTint;
        tint.Opacity = p.DiscTintOpacity;
        var s = p.Sheen;
        sheen.Fill = new RadialGradientBrush(Color.FromArgb((byte)(s.A * 0.6), 255, 255, 255), Color.FromArgb((byte)(s.A * 0.15), 255, 255, 255));
        rim.Stroke = new LinearGradientBrush(p.RimTop, p.RimBottom, 90);
        innerRim.Stroke = Theme.Brush(Colors.White, p.Dark ? 0.06 : 0.16);
        ring.Fill = Theme.Brush(Colors.White, p.Dark ? 0.06 : 0.15);
        ring.Stroke = Theme.Brush(p.Ring);
        ringFill.Fill = Theme.Brush(Colors.White, p.Dark ? 0.04 : 0.08);
        PaintPill();
        pillText.Effect = p.Dark ? TextShadow() : null;
        centerGlyph.Foreground = Theme.Brush(p.WedgeInk, 0.55);
    }

    void PaintPill()
    {
        bool hot = centerLabel != null;
        pill.Background = hot ? new LinearGradientBrush(palette.HoverTop, palette.HoverBottom, 90) : new LinearGradientBrush(palette.PillTop, palette.PillBottom, 90);
        pill.BorderBrush = Theme.Brush(hot ? Palette.Shift(palette.Accent, 0.18) : palette.Ring);
        pillText.Foreground = Theme.Brush(hot ? Colors.White : palette.WedgeInk);
        pillText.Text = centerLabel ?? (hover >= 0 && hover < Items.Count ? Items[hover].Hint ?? Items[hover].Label : idleText);
        // Longer words get a little smaller so they still fit the pill ("NORMALIZE", "Swap sides").
        pillText.FontSize = pillText.Text.Length > 10 ? 11 : pillText.Text.Length > 8 ? 12 : 13.5;
    }

    /// <summary>"×" when letting go in the middle cancels, "←" when it goes back a step.</summary>
    public void SetCenterGlyph(string glyph) => centerGlyph.Text = glyph;

    /// <summary>Lights the middle pill with <paramref name="label"/> ("Cancel", "Back") while the pointer is over it.</summary>
    public void SetCenterHover(string? label)
    {
        if (label == centerLabel) return;
        centerLabel = label;
        PaintPill();
    }

    /// <summary>A soft dark halo that keeps white labels readable over bright things behind the glass.</summary>
    static DropShadowEffect TextShadow() => new() { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 0.6, RenderingBias = RenderingBias.Performance };

    static void Place(UIElement e, double x, double y)
    {
        SetLeft(e, x);
        SetTop(e, y);
    }

    static Ellipse Circle(double r, Brush? fill, Brush? stroke, double thickness)
    {
        var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = fill, Stroke = stroke, StrokeThickness = thickness, IsHitTestVisible = false };
        Place(e, Cx - r, Cy - r);
        return e;
    }

    public void SetBackdrop(BitmapSource? image) => backdrop.Source = image;

    /// <summary>What the pill shows when nothing is hovered (file size, "3 files").</summary>
    public void SetIdleText(string text)
    {
        idleText = text;
        if (hover < 0 && centerLabel == null) pillText.Text = text;
    }

    // ---------- Building ----------

    public void SetItems(IReadOnlyList<WheelItem> items, bool iconStyle, bool animate)
    {
        var old = slices.ToList();
        slices.Clear();
        hover = -1;
        centerLabel = null;
        PaintPill();
        foreach (var s in old)
        {
            var group = s.Group;
            if (!animate) { wedgeLayer.Children.Remove(group); continue; }
            group.IsHitTestVisible = false;
            Theme.Animate(group, OpacityProperty, 0, 90, done: () => wedgeLayer.Children.Remove(group));
            Theme.Animate(s.Pop, ScaleTransform.ScaleXProperty, 0.9, 90);
            Theme.Animate(s.Pop, ScaleTransform.ScaleYProperty, 0.9, 90);
        }

        Items = items;
        if (items.Count == 0)
        {
            var ring = new Path
            {
                Data = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Point(Cx, Cy), OuterR, OuterR), new EllipseGeometry(new Point(Cx, Cy), InnerR, InnerR)),
                Fill = new LinearGradientBrush(palette.WedgeTop, palette.WedgeBottom, 90),
                Opacity = 0.6,
            };
            var note = Theme.Text("Nothing to do here", 11.5, palette.WedgeInk, FontWeights.SemiBold);
            note.Width = 160;
            note.TextAlignment = TextAlignment.Center;
            Place(note, Cx - 80, Cy + 86);
            var group = new Canvas { Width = ViewSize, Height = ViewSize };
            group.Children.Add(ring);
            group.Children.Add(note);
            var pop = new ScaleTransform(1, 1, Cx, Cy);
            group.RenderTransform = pop;
            wedgeLayer.Children.Add(group);
            slices.Add(new Slice { Group = group, Hot = new Path(), Ink = new SolidColorBrush(), IdleInk = palette.WedgeInk, Pop = pop });
            if (animate) Theme.Animate(group, OpacityProperty, 1, 160, from: 0);
            return;
        }

        double span = 360.0 / items.Count;
        for (int i = 0; i < items.Count; i++)
        {
            double mid = StartAngle(items.Count) + i * span;
            var geometry = items.Count == 1
                ? new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Point(Cx, Cy), OuterR, OuterR), new EllipseGeometry(new Point(Cx, Cy), InnerR, InnerR))
                : WedgeGeometry(InnerR, OuterR, mid - span / 2, mid + span / 2);
            geometry.Freeze();

            var group = new Canvas { Width = ViewSize, Height = ViewSize };
            var pop = new ScaleTransform(1, 1, Cx, Cy);
            group.RenderTransform = pop;

            var basePath = new Path
            {
                Data = geometry,
                Fill = new LinearGradientBrush(palette.WedgeTop, palette.WedgeBottom, 90),
                Stroke = Theme.Brush(palette.WedgeStroke),
                StrokeThickness = 1,
            };
            var hotFill = new LinearGradientBrush(palette.HoverTop, palette.HoverBottom, 90);
            var hot = new Path { Data = geometry, Fill = hotFill, Opacity = 0, Stroke = Theme.Brush(Palette.Shift(palette.Accent, 0.18), 0.7), StrokeThickness = 1 };
            group.Children.Add(basePath);
            group.Children.Add(hot);

            var ink = new SolidColorBrush(palette.WedgeInk);
            var item = items[i];
            FrameworkElement label;
            if (iconStyle && item.Icon != null)
            {
                FrameworkElement icon;
                if (item.Icon.StartsWith("app:") && Share.Icon(item.Icon[4..]) is { } picture)
                {
                    // An installed app (Share ring): its real icon.
                    icon = new Image { Source = picture, Width = 26, Height = 26, Margin = new Thickness(0, -4, 0, -2) };
                    RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
                }
                else
                {
                    var glyph = Theme.Icon(item.Icon.StartsWith("app:") ? "Share" : item.Icon, 17, Colors.Black, 1.3);
                    glyph.Stroke = ink;
                    icon = glyph;
                }
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                var text = new TextBlock
                {
                    Text = item.Label,
                    FontFamily = Theme.UiFont,
                    FontSize = items.Count > 8 ? 8.5 : 9.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = ink,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 0),
                    MaxWidth = 78,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var stack = new StackPanel();
                stack.Children.Add(icon);
                stack.Children.Add(text);
                label = stack;
            }
            else
            {
                label = new TextBlock
                {
                    Text = item.Label,
                    FontFamily = Theme.UiFont,
                    FontSize = items.Count > 8 ? 12 : 13.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = ink,
                };
            }
            label.IsHitTestVisible = false;
            if (palette.Dark) label.Effect = TextShadow();
            var holder = new Grid { Width = 92, Height = 52 };
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            holder.Children.Add(label);
            double a = mid * Math.PI / 180, lr = (InnerR + OuterR) / 2 + 1;
            Place(holder, Cx + lr * Math.Cos(a) - 46, Cy + lr * Math.Sin(a) - 26);
            group.Children.Add(holder);

            wedgeLayer.Children.Add(group);
            slices.Add(new Slice { Group = group, Hot = hot, Ink = ink, IdleInk = palette.WedgeInk, Pop = pop });

            if (animate)
            {
                group.Opacity = 0;
                double delay = 40 + i * 18;
                Theme.Animate(group, OpacityProperty, 1, 170, delayMs: delay, from: 0);
                Theme.Animate(pop, ScaleTransform.ScaleXProperty, 1, 260, Theme.Spring, 0.86, delay);
                Theme.Animate(pop, ScaleTransform.ScaleYProperty, 1, 260, Theme.Spring, 0.86, delay);
            }
        }
    }

    /// <summary>
    /// An annular sector with constant-width gaps and rounded corners. The sharp shape is built
    /// inset by the corner radius, then grown back with a round-joined outline, which rounds every corner evenly.
    /// </summary>
    static Geometry WedgeGeometry(double rIn, double rOut, double a0, double a1)
    {
        double ri = rIn + Corner, ro = rOut - Corner, half = Gap / 2 + Corner;
        Point P(double r, double deg) => new(Cx + r * Math.Cos(deg * Math.PI / 180), Cy + r * Math.Sin(deg * Math.PI / 180));
        double oo = Math.Asin(half / ro) * 180 / Math.PI, io = Math.Asin(half / ri) * 180 / Math.PI;
        var core = new StreamGeometry();
        using (var c = core.Open())
        {
            c.BeginFigure(P(ro, a0 + oo), true, true);
            c.ArcTo(P(ro, a1 - oo), new Size(ro, ro), 0, (a1 - oo) - (a0 + oo) > 180, SweepDirection.Clockwise, true, true);
            c.LineTo(P(ri, a1 - io), true, true);
            c.ArcTo(P(ri, a0 + io), new Size(ri, ri), 0, (a1 - io) - (a0 + io) > 180, SweepDirection.Counterclockwise, true, true);
        }
        core.Freeze();
        var widened = core.GetWidenedPathGeometry(new Pen(Brushes.Black, Corner * 2) { LineJoin = PenLineJoin.Round }, 0.05, ToleranceType.Absolute);
        return Geometry.Combine(core, widened, GeometryCombineMode.Union, null, 0.05, ToleranceType.Absolute);
    }

    // ---------- Interaction ----------

    /// <summary>The first slice sits at the top; with just two (Copy / Edit) they sit left and right.</summary>
    static double StartAngle(int count) => count == 2 ? 180 : -90;

    /// <summary>-2 outside the disc, -1 the center (cancel), otherwise the wedge index.</summary>
    public int HitTest(Point p)
    {
        double dx = p.X - Cx, dy = p.Y - Cy, r = Math.Sqrt(dx * dx + dy * dy);
        if (r > DiscR + 2) return -2;
        if (r < InnerR - 3 || Items.Count == 0) return -1;
        double span = 360.0 / Items.Count;
        double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        double rel = (angle - StartAngle(Items.Count) + span / 2 + 720) % 360;
        return Math.Min(Items.Count - 1, (int)(rel / span));
    }

    public void SetHover(int index, bool instant = false)
    {
        if (index >= Items.Count || index < -1) index = -1;
        if (index == hover) return;
        if (hover >= 0 && hover < slices.Count) Light(slices[hover], false, instant);
        hover = index;
        if (hover >= 0 && hover < slices.Count) Light(slices[hover], true, instant);
        PaintPill();
    }

    static void Light(Slice s, bool on, bool instant)
    {
        if (instant)
        {
            s.Hot.Opacity = on ? 1 : 0;
            s.Pop.ScaleX = s.Pop.ScaleY = on ? 1.035 : 1;
            s.Ink.Color = on ? Colors.White : s.IdleInk;
            return;
        }
        Theme.Animate(s.Hot, OpacityProperty, on ? 1 : 0, on ? 90 : 140);
        Theme.Animate(s.Pop, ScaleTransform.ScaleXProperty, on ? 1.035 : 1, 160, Theme.Spring);
        Theme.Animate(s.Pop, ScaleTransform.ScaleYProperty, on ? 1.035 : 1, 160, Theme.Spring);
        s.Ink.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(on ? Colors.White : s.IdleInk, TimeSpan.FromMilliseconds(110)));
    }

    // ---------- Motion ----------

    public void AnimateOpen()
    {
        Theme.Animate(root, OpacityProperty, 1, 120, from: 0);
        Theme.Animate(rootScale, ScaleTransform.ScaleXProperty, 1, 300, Theme.Spring, 0.8);
        Theme.Animate(rootScale, ScaleTransform.ScaleYProperty, 1, 300, Theme.Spring, 0.8);
    }

    public void AnimateClose(Action done, bool picked = false)
    {
        if (picked && hover >= 0 && hover < slices.Count)
        {
            var s = slices[hover];
            Theme.Animate(s.Pop, ScaleTransform.ScaleXProperty, 1.08, 120);
            Theme.Animate(s.Pop, ScaleTransform.ScaleYProperty, 1.08, 120);
        }
        double delay = picked ? 70 : 0;
        Theme.Animate(root, OpacityProperty, 0, 150, delayMs: delay, done: done);
        Theme.Animate(rootScale, ScaleTransform.ScaleXProperty, 0.9, 170, delayMs: delay);
        Theme.Animate(rootScale, ScaleTransform.ScaleYProperty, 0.9, 170, delayMs: delay);
    }

    public void ResetMotion()
    {
        root.BeginAnimation(OpacityProperty, null);
        rootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        rootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        root.Opacity = 1;
        rootScale.ScaleX = rootScale.ScaleY = 1;
    }
}

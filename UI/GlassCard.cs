using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using FileWheel.Native;

namespace FileWheel.UI;

/// <summary>
/// A rounded frosted card: blurred screen backdrop, palette tint, a light sheen, hairline rim and soft shadow.
/// Used by the toast, the editor panels and the floating preview.
/// </summary>
public sealed class GlassCard : Grid
{
    readonly Border clipHost;
    readonly Canvas backdropLayer = new() { ClipToBounds = true, IsHitTestVisible = false };
    readonly Image backdrop = new() { Stretch = Stretch.Fill };
    readonly Border tint = new() { IsHitTestVisible = false };
    readonly Border sheen = new() { IsHitTestVisible = false };
    readonly Border rim;
    readonly Border shadow;
    readonly Grid content = new();
    BitmapSource? shot;
    RECT shotRect;

    public Grid Content => content;

    public GlassCard(Palette palette, Brush tintBrush, double tintOpacity, double radius = 16, double shadowOpacity = 0.30)
    {
        shadow = new Border
        {
            CornerRadius = new CornerRadius(radius),
            Background = Brushes.Black,
            Opacity = shadowOpacity,
            Margin = new Thickness(6, 14, 6, -4),
            Effect = new BlurEffect { Radius = 30, RenderingBias = RenderingBias.Performance },
            IsHitTestVisible = false,
        };
        Children.Add(shadow);

        var layers = new Grid();
        backdrop.Effect = new BlurEffect { Radius = 22, RenderingBias = RenderingBias.Performance };
        backdropLayer.Children.Add(backdrop);
        layers.Children.Add(backdropLayer);
        layers.Children.Add(tint);
        layers.Children.Add(sheen);
        layers.Children.Add(content);
        clipHost = new Border { Child = layers, CornerRadius = new CornerRadius(radius) };
        clipHost.SizeChanged += (_, e) => clipHost.Clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
        Children.Add(clipHost);

        rim = new Border { CornerRadius = new CornerRadius(radius), BorderThickness = new Thickness(1), IsHitTestVisible = false };
        Children.Add(rim);
        Paint(palette, tintBrush, tintOpacity);
    }

    public void Paint(Palette palette, Brush tintBrush, double tintOpacity)
    {
        tint.Background = tintBrush;
        tint.Opacity = tintOpacity;
        var top = palette.Sheen;
        sheen.Background = new LinearGradientBrush(top, Color.FromArgb(0, top.R, top.G, top.B), 90);
        rim.BorderBrush = new LinearGradientBrush(palette.RimTop, palette.RimBottom, 90);
        shadow.Opacity = palette.Dark ? 0.45 : 0.26;
    }

    /// <summary>Uses a capture of <paramref name="screenRect"/> (physical px) as the frosted backdrop.</summary>
    public void SetBackdrop(BitmapSource? image, RECT screenRect)
    {
        shot = image;
        shotRect = screenRect;
        backdrop.Source = image;
    }

    /// <summary>Lines the backdrop up with where the card currently sits on screen.</summary>
    public void AlignBackdrop(Visual window)
    {
        if (shot == null || PresentationSource.FromVisual(this) == null) return;
        AlignBackdrop(clipHost.PointToScreen(new Point(0, 0)), VisualTreeHelper.GetDpi(window).DpiScaleX);
    }

    /// <param name="cardTopLeft">The card's top-left corner in physical screen pixels.</param>
    public void AlignBackdrop(Point cardTopLeft, double scale)
    {
        if (shot == null) return;
        backdrop.Width = shotRect.Width / scale;
        backdrop.Height = shotRect.Height / scale;
        Canvas.SetLeft(backdrop, (shotRect.Left - cardTopLeft.X) / scale);
        Canvas.SetTop(backdrop, (shotRect.Top - cardTopLeft.Y) / scale);
    }
}

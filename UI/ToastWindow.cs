using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FileWheel.Native;

namespace FileWheel.UI;

/// <summary>
/// The small frosted progress card: "× Converting to JPG / 1 of 1 saved / ▬▬▬───".
/// Click it when finished to reveal the result in Explorer.
/// </summary>
public sealed class ToastWindow : Window
{
    const double CardW = 330, CardH = 100, Pad = 30;

    readonly GlassCard card;
    readonly TextBlock title, subtitle, link;
    readonly Border fill;
    readonly Grid track;
    readonly TranslateTransform slide = new();
    readonly DispatcherTimer autoClose = new();
    CancellationTokenSource? cancel;
    string? reveal;
    readonly Palette palette;
    readonly int placeX, placeY;
    readonly double placeScale;
    double fraction;
    bool closing;

    /// <summary>Builds a toast that will appear centered on <paramref name="center"/> (physical px).</summary>
    public ToastWindow(string heading, CancellationTokenSource? cancellation, POINT center)
    {
        cancel = cancellation;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = CardW + Pad * 2;
        Height = CardH + Pad * 2;
        SourceInitialized += (_, _) => Win32.MakeToolWindow(this, noActivate: true);

        // Look at what's behind the spot first, so the glass and text suit it.
        var (work, _, scale) = Backdrop.Screen(center);
        int w = (int)(Width * scale), h = (int)(Height * scale), m = (int)(Pad * scale);
        placeX = Math.Clamp(center.X - w / 2, work.Left, Math.Max(work.Left, work.Right - w));
        placeY = Math.Clamp(center.Y - h / 2, work.Top, Math.Max(work.Top, work.Bottom - h));
        placeScale = scale;
        var rect = new RECT { Left = placeX + m, Top = placeY + m, Right = placeX + w - m, Bottom = placeY + h - m };
        var shot = Backdrop.Capture(rect);
        palette = Palette.For(shot);
        Theme.Current = palette;
        palette.ApplyTo(Resources);
        bool tangerine = palette == Palette.Tangerine;

        card = new GlassCard(palette, palette.ToastTint, palette.ToastTintOpacity, 20, 0.22) { Margin = new Thickness(Pad), RenderTransform = slide };
        card.SetBackdrop(shot, rect);

        var grid = new Grid { Margin = new Thickness(18, 14, 18, 16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new DockPanel();
        var close = new Button { Style = Theme.Style("CloseButton"), Width = 20, Height = 20, Margin = new Thickness(0, 0, 12, 0), ToolTip = "Cancel" };
        close.Click += (_, _) => { cancel?.Cancel(); Dismiss(); };
        DockPanel.SetDock(close, Dock.Left);
        header.Children.Add(close);
        title = Theme.Text(heading, 15.5, tangerine ? Theme.Hex("#7A4632") : palette.Ink, FontWeights.SemiBold);
        header.Children.Add(title);
        grid.Children.Add(header);

        var info = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        link = Theme.Text("Show in folder", 12, palette.AccentText, FontWeights.SemiBold);
        link.Visibility = Visibility.Collapsed;
        link.Cursor = Cursors.Hand;
        DockPanel.SetDock(link, Dock.Right);
        info.Children.Add(link);
        subtitle = Theme.Text("Starting…", 12.5, tangerine ? Theme.Hex("#8E5A45") : palette.InkSoft);
        info.Children.Add(subtitle);
        Grid.SetRow(info, 1);
        grid.Children.Add(info);

        track = new Grid { Height = 4, Margin = new Thickness(0, 10, 0, 0) };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = tangerine ? Theme.Brush(Theme.Hex("#9E4A2A"), 0.18) : Theme.TrackBrush });
        fill = new Border { CornerRadius = new CornerRadius(2), Background = Theme.Brush(Theme.Accent), HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
        track.Children.Add(fill);
        track.SizeChanged += (_, _) => fill.Width = track.ActualWidth * fraction;
        Grid.SetRow(track, 2);
        grid.Children.Add(track);

        card.Content.Children.Add(grid);
        Content = card;

        card.MouseLeftButtonUp += (_, _) => { if (reveal != null) { Program.Reveal(reveal); Dismiss(); } };
        autoClose.Tick += (_, _) => { autoClose.Stop(); Dismiss(); };
        MouseEnter += (_, _) => autoClose.Stop();
        MouseLeave += (_, _) => { if (closingSoon) autoClose.Start(); };
    }

    bool closingSoon;

    public void Present()
    {
        Left = placeX / placeScale;
        Top = placeY / placeScale;
        Opacity = 0;
        Show();
        Win32.MoveTo(this, placeX, placeY);
        card.AlignBackdrop(this);
        Theme.Animate(this, OpacityProperty, 1, 160, from: 0);
        Theme.Animate(slide, TranslateTransform.YProperty, 0, 260, Theme.Spring, 10);
    }

    /// <summary>Snapshot tests: frost the card with the fixed picture as if it sat at <paramref name="at"/>.</summary>
    public void PrepareSnapshot(Point at, RECT screen)
    {
        card.SetBackdrop(Backdrop.Capture(screen), screen);
        card.AlignBackdrop(new Point(at.X + Pad, at.Y + Pad), 1);
    }

    public void SetProgress(double value, string text)
    {
        fraction = Math.Clamp(value, 0, 1);
        subtitle.Text = text;
        var to = track.ActualWidth * fraction;
        fill.BeginAnimation(WidthProperty, new System.Windows.Media.Animation.DoubleAnimation(to, TimeSpan.FromMilliseconds(180)) { EasingFunction = Theme.EaseOut });
    }

    public void Complete(string heading, string text, string? revealPath, double seconds = 2.6)
    {
        cancel = null;
        title.Text = heading;
        SetProgress(1, text);
        reveal = revealPath;
        if (revealPath != null)
        {
            link.Visibility = Visibility.Visible;
            card.Cursor = Cursors.Hand;
            card.ToolTip = revealPath;
        }
        closingSoon = true;
        autoClose.Interval = TimeSpan.FromSeconds(seconds);
        autoClose.Start();
    }

    public void Fail(string heading, string message)
    {
        cancel = null;
        title.Text = heading;
        subtitle.Text = message;
        subtitle.TextTrimming = TextTrimming.None;
        subtitle.TextWrapping = TextWrapping.Wrap;
        subtitle.ToolTip = message;
        subtitle.MaxHeight = 34;
        fill.Background = Theme.Brush(palette.Danger);
        SetProgress(1, message);
        closingSoon = true;
        autoClose.Interval = TimeSpan.FromSeconds(7);
        autoClose.Start();
    }

    public void Dismiss()
    {
        if (closing) return;
        closing = true;
        autoClose.Stop();
        Theme.Animate(this, OpacityProperty, 0, 180, done: Close);
        Theme.Animate(slide, TranslateTransform.YProperty, 6, 180);
    }

    // ---------- Helpers used by the controller ----------

    /// <summary>Runs <paramref name="work"/> in the background with this toast showing progress.</summary>
    public static async Task<T?> Run<T>(string heading, POINT at, Func<CancellationToken, Action<double, string>, T> work,
        Func<T, (string Heading, string Text, string? Reveal)> success, string failHeading)
    {
        var cts = new CancellationTokenSource();
        var toast = new ToastWindow(heading, cts, at);
        toast.Present();
        var dispatcher = toast.Dispatcher;
        void Report(double f, string text) => dispatcher.BeginInvoke(() => toast.SetProgress(f, text));
        try
        {
            var result = await Task.Run(() => work(cts.Token, Report), cts.Token);
            var (h, t, r) = success(result);
            toast.Complete(h, t, r);
            return result;
        }
        catch (OperationCanceledException)
        {
            toast.Dismiss();
            return default;
        }
        catch (Exception e)
        {
            Debug.WriteLine(e);
            toast.Fail(failHeading, Core.Converter.Friendly(e));
            return default;
        }
    }

    /// <summary>A short confirmation (e.g. "Path copied").</summary>
    public static void Flash(string heading, string text, POINT at, string? reveal = null)
    {
        var toast = new ToastWindow(heading, null, at);
        toast.Present();
        toast.Complete(heading, text, reveal, 1.8);
    }
}

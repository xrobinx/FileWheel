using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FileWheel.Native;

namespace FileWheel.UI;

/// <summary>
/// Base for every editor/panel: a frosted amber card with a round × at top-left, a centered title,
/// a body and a footer separated by a hairline (Reset on the left, the orange action on the right).
/// </summary>
public class GlassPanel : Window
{
    protected const double ShadowMargin = 28;

    protected readonly Grid Body = new();
    readonly Grid footer = new() { Margin = new Thickness(14, 10, 14, 12) };
    readonly GlassCard card;
    readonly ScaleTransform pop = new(1, 1);
    readonly TextBlock titleText;
    readonly Grid header;
    readonly Button close;
    readonly DockPanel footerHost;
    readonly int placeX, placeY;
    readonly double placeScale;
    bool closing;

    /// <summary>The colors of this panel, chosen from what's on screen where it will appear.</summary>
    protected Palette Palette { get; }

    /// <summary>Where the work started (the wheel), for placing result toasts.</summary>
    public POINT Anchor { get; set; }

    /// <summary>Invoked by Enter.</summary>
    protected Action? DefaultAction { get; set; }

    /// <param name="near">Centers the panel on this screen point (e.g. where the wheel was) instead of on the monitor.</param>
    public GlassPanel(string title, double width, double height, POINT? near = null)
    {
        Title = title + " — FileWheel";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        Width = width + ShadowMargin * 2;
        Height = height + ShadowMargin * 2;
        FontFamily = Theme.UiFont;
        UseLayoutRounding = true;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/FileWheel.ico")); } catch { }

        // Decide where the panel goes and look at what's there before anything is built, so the
        // glass (and every control created by the subclass) takes on those colors.
        var (work, bounds, scale) = Backdrop.Screen(near ?? Win32.Cursor());
        int w = (int)(Width * scale), h = (int)(Height * scale), m = (int)(ShadowMargin * scale);
        if (near is { } p)
        {
            placeX = Math.Clamp(p.X - w / 2, work.Left - m, Math.Max(work.Left - m, work.Right - w + m));
            placeY = Math.Clamp(p.Y - h / 2, work.Top - m, Math.Max(work.Top - m, work.Bottom - h + m));
        }
        else
        {
            placeX = work.Left + (work.Width - w) / 2;
            placeY = work.Top + Math.Max(0, (work.Height - h) / 2);
        }
        placeScale = scale;
        var shot = Backdrop.Capture(bounds);
        Palette = Palette.For(shot, Backdrop.Region(bounds, new RECT { Left = placeX + m, Top = placeY + m, Right = placeX + w - m, Bottom = placeY + h - m }));
        Theme.Current = Palette;
        Palette.ApplyTo(Resources);

        card = new GlassCard(Palette, Palette.PanelTint, Palette.PanelTintOpacity) { Margin = new Thickness(ShadowMargin), RenderTransform = pop, RenderTransformOrigin = new Point(0.5, 0.5) };
        card.SetBackdrop(shot, bounds);
        var layout = new DockPanel();

        // Header: × on the left, title centered. Dragging it moves the panel.
        header = new Grid { Height = 40, Background = Brushes.Transparent };
        close = new Button { Style = Theme.Style("CloseButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(14, 0, 0, 0) };
        close.Click += (_, _) => CloseAnimated();
        titleText = Theme.Text(title, 13.5, Theme.Ink, FontWeights.SemiBold);
        titleText.HorizontalAlignment = HorizontalAlignment.Center;
        header.Children.Add(titleText);
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 1 && e.OriginalSource is not Button)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
                card.AlignBackdrop(this);
            }
        };
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        footerHost = new DockPanel();
        var line = Theme.Divider();
        DockPanel.SetDock(line, Dock.Top);
        footerHost.Children.Add(line);
        footerHost.Children.Add(footer);
        DockPanel.SetDock(footerHost, Dock.Bottom);
        layout.Children.Add(footerHost);

        layout.Children.Add(Body);
        card.Content.Children.Add(layout);
        Content = card;

        LocationChanged += (_, _) => card.AlignBackdrop(this);
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { CloseAnimated(); e.Handled = true; }
            else if (e.Key == Key.Back && back != null && Keyboard.FocusedElement is not TextBox) { GoBack(); e.Handled = true; }
            else if (e.Key == Key.Enter && DefaultAction != null && Keyboard.FocusedElement is not TextBox { AcceptsReturn: true })
            {
                DefaultAction();
                e.Handled = true;
            }
        };
    }

    protected void SetTitle(string title) => titleText.Text = title;

    Action? back;

    /// <summary>Adds a ← button at the top-left (the × moves to the right) that closes this panel and runs <paramref name="action"/>.</summary>
    protected void EnableBack(Action action)
    {
        if (back == null)
        {
            close.HorizontalAlignment = HorizontalAlignment.Right;
            close.Margin = new Thickness(0, 0, 14, 0);
            var button = Theme.IconButton("Back", "Back", GoBack);
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Margin = new Thickness(10, 0, 0, 0);
            header.Children.Add(button);
        }
        back = action;
    }

    void GoBack()
    {
        if (back is { } then) CloseAnimated(then);
    }

    /// <summary>For small choosers that act straight from their body.</summary>
    protected void HideFooter() => footerHost.Visibility = Visibility.Collapsed;

    /// <summary>Left-side and right-side footer content.</summary>
    protected void SetFooter(UIElement? left, params UIElement[] right)
    {
        footer.Children.Clear();
        if (left != null)
        {
            if (left is FrameworkElement fe) fe.HorizontalAlignment = HorizontalAlignment.Left;
            footer.Children.Add(left);
        }
        var stack = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var r in right)
        {
            if (r is FrameworkElement fe) fe.Margin = new Thickness(8, 0, 0, 0);
            stack.Children.Add(r);
        }
        footer.Children.Add(stack);
    }

    /// <summary>Opens the panel centered on the monitor under the pointer.</summary>
    public void Present()
    {
        Left = placeX / placeScale;
        Top = placeY / placeScale;
        Opacity = 0;
        Show();
        Win32.MoveTo(this, placeX, placeY);
        card.AlignBackdrop(this);
        Win32.ForceForeground(this);
        Activate();
        Theme.Animate(this, OpacityProperty, 1, 170, from: 0);
        Theme.Animate(pop, ScaleTransform.ScaleXProperty, 1, 260, Theme.Spring, 0.96);
        Theme.Animate(pop, ScaleTransform.ScaleYProperty, 1, 260, Theme.Spring, 0.96);
    }

    /// <summary>Snapshot tests: put a fixed backdrop behind the card as if it sat at <paramref name="screenTopLeft"/>.</summary>
    public void PrepareSnapshot(Point screenTopLeft, double scale, RECT screen)
    {
        card.SetBackdrop(Backdrop.Capture(screen), screen);
        card.AlignBackdrop(new Point(screenTopLeft.X + ShadowMargin * scale, screenTopLeft.Y + ShadowMargin * scale), scale);
    }

    /// <param name="then">Runs once the panel is gone from the screen, so a wheel or panel opened next frosts the real desktop.</param>
    public void CloseAnimated(Action? then = null)
    {
        if (closing) return;
        closing = true;
        Theme.Animate(this, OpacityProperty, 0, 130, done: () =>
        {
            Close();
            if (then != null) Theme.After(40, then);
        });
        Theme.Animate(pop, ScaleTransform.ScaleXProperty, 0.97, 130);
        Theme.Animate(pop, ScaleTransform.ScaleYProperty, 0.97, 130);
    }

    /// <summary>Runs a save in the background, then closes and confirms with a toast.</summary>
    protected async void SaveInBackground(string busyLabel, Button button, Func<CancellationToken, Action<double, string>, IReadOnlyList<string>> work, string doneHeading)
    {
        if (!button.IsEnabled) return;
        var label = button.Content;
        button.IsEnabled = false;
        button.Content = busyLabel;
        Cursor = Cursors.AppStarting;
        var anchor = Anchor.X == 0 && Anchor.Y == 0 ? CenterOnScreen() : Anchor;
        try
        {
            var outputs = await Task.Run(() => work(CancellationToken.None, (_, _) => { }));
            CloseAnimated();
            if (outputs.Count > 0)
                ToastWindow.Flash(doneHeading, outputs.Count == 1 ? "Saved " + Path.GetFileName(outputs[0]) : $"{outputs.Count} files saved", anchor, outputs[0]);
        }
        catch (Exception e)
        {
            Cursor = null;
            button.IsEnabled = true;
            button.Content = label;
            ShowError(Core.Converter.Friendly(e));
        }
    }

    protected POINT CenterOnScreen()
    {
        if (!IsVisible) return Win32.Cursor();
        var r = Win32.WindowRect(this);
        return new POINT((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
    }

    TextBlock? error;

    protected void ShowError(string message)
    {
        if (error == null)
        {
            error = Theme.Text("", 12, Palette.Danger, FontWeights.SemiBold);
            error.TextWrapping = TextWrapping.Wrap;
            error.TextTrimming = TextTrimming.None;
            error.Margin = new Thickness(18, 0, 18, 6);
            error.VerticalAlignment = VerticalAlignment.Bottom;
            Body.Children.Add(error);
        }
        error.Text = message;
    }
}

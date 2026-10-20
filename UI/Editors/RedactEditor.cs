using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FileWheel.Core;
using ImageMagick;

namespace FileWheel.UI.Editors;

/// <summary>"Redact": drag over anything private to black it out, blur it or pixelate it. Metadata is removed too.</summary>
public sealed class RedactEditor : GlassPanel
{
    const double StageW = 364, StageH = 400;

    readonly string file;
    readonly Grid stage = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Cross, Background = Brushes.Transparent };
    readonly Image image = new() { Stretch = Stretch.Fill };
    readonly Canvas overlay = new();
    readonly Rectangle marquee = new() { StrokeThickness = 1.5, StrokeDashArray = [3, 2], Visibility = Visibility.Collapsed };
    readonly List<(NormRect Rect, RedactStyle Style)> regions = [];
    readonly Button save;
    RedactStyle style = RedactStyle.Box;
    MagickImage? small;
    Point start;
    bool dragging;
    int version;

    public RedactEditor(string file) : base("Redact", 410, 620)
    {
        this.file = file;
        marquee.Stroke = Theme.Brush(Theme.Accent);
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var bar = new DockPanel { Margin = new Thickness(18, 0, 18, 10) };
        bar.Children.Add(Theme.Segmented(["Box", "Blur", "Pixelate"], "Box", s => style = Enum.Parse<RedactStyle>(s), out _));
        var undo = Theme.IconButton("Undo", "Undo (Ctrl+Z)", Undo);
        undo.HorizontalAlignment = HorizontalAlignment.Right;
        bar.Children.Add(undo);
        Body.Children.Add(bar);

        overlay.Children.Add(marquee);
        stage.Children.Add(image);
        stage.Children.Add(overlay);
        var host = new Grid { Margin = new Thickness(18, 0, 18, 8) };
        host.Children.Add(Preview.LoadingLabel());
        host.Children.Add(stage);
        Grid.SetRow(host, 1);
        Body.Children.Add(host);

        var hint = Theme.Text("Drag over anything you want hidden. The saved copy has no metadata.", 11.5, Theme.InkMuted);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.TextTrimming = TextTrimming.None;
        hint.Margin = new Thickness(18, 0, 18, 12);
        Grid.SetRow(hint, 2);
        Body.Children.Add(hint);

        save = Theme.Primary("Save Redacted", Save);
        SetFooter(Theme.Soft("Reset", () => { regions.Clear(); Render(); }), save);

        stage.MouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(stage);
            dragging = true;
            marquee.Visibility = Visibility.Visible;
            Place(new Rect(start, start));
            stage.CaptureMouse();
        };
        stage.MouseMove += (_, e) => { if (dragging) Place(new Rect(start, Clamp(e.GetPosition(stage)))); };
        stage.MouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            stage.ReleaseMouseCapture();
            marquee.Visibility = Visibility.Collapsed;
            var r = new Rect(start, Clamp(e.GetPosition(stage)));
            if (r.Width < 3 || r.Height < 3) return;
            regions.Add((new NormRect(r.X / stage.ActualWidth, r.Y / stage.ActualHeight, r.Width / stage.ActualWidth, r.Height / stage.ActualHeight), style));
            Render();
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) { Undo(); e.Handled = true; } };
        Closed += (_, _) => small?.Dispose();

        Loaded += async (_, _) =>
        {
            try
            {
                var (w, h) = await Task.Run(() => Preview.SourceSize(file));
                small = await Task.Run(() => Preview.LoadMagick(file, 1000));
                var fit = Preview.Fit(w, h, StageW, StageH);
                stage.Width = fit.Width;
                stage.Height = fit.Height;
                host.Children.RemoveAt(0);
                Render();
            }
            catch (Exception e) { ShowError(Converter.Friendly(e)); }
        };
    }

    Point Clamp(Point p) => new(Math.Clamp(p.X, 0, stage.ActualWidth), Math.Clamp(p.Y, 0, stage.ActualHeight));

    void Place(Rect r)
    {
        Canvas.SetLeft(marquee, r.X);
        Canvas.SetTop(marquee, r.Y);
        marquee.Width = r.Width;
        marquee.Height = r.Height;
    }

    void Undo()
    {
        if (regions.Count == 0) return;
        regions.RemoveAt(regions.Count - 1);
        Render();
    }

    async void Render()
    {
        if (small == null) return;
        int mine = ++version;
        var list = regions.ToList();
        var source = small;
        var bitmap = await Task.Run(() =>
        {
            lock (source)
            {
                using var copy = source.Clone();
                ImageTools.Redact(copy, list);
                return Converter.ToBitmap(copy);
            }
        });
        if (mine == version) image.Source = bitmap;
    }

    void Save()
    {
        if (regions.Count == 0) { ShowError("Drag over an area to redact first."); return; }
        var list = regions.ToList();
        if (Formats.Kind(file) == FileKind.Video)
        {
            // The boxes apply to every frame of the video, behind a progress toast.
            var at = Anchor.X == 0 && Anchor.Y == 0 ? CenterOnScreen() : Anchor;
            CloseAnimated();
            _ = ToastWindow.Run("Redacting video", at,
                (ct, report) => Media.Redact(file, list, p => report(p, $"{p:P0}"), ct),
                output => ("Redacted", System.IO.Path.GetFileName(output), output),
                "Couldn't redact");
            return;
        }
        SaveInBackground("Saving…", save, (_, _) => [ImageTools.SaveRedacted(file, list)], "Redacted");
    }
}

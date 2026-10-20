using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FileWheel.Core;
using Path = System.Windows.Shapes.Path;

namespace FileWheel.UI.Editors;

/// <summary>"Crop Image": drag the orange frame or its handles, pick an aspect ratio, type exact pixels, Apply.</summary>
public sealed class CropEditor : GlassPanel
{
    const double StageW = 344, StageH = 372;
    static readonly string[] Ratios = ["Free", "1:1", "16:9", "9:16", "4:3", "3:4"];

    readonly IReadOnlyList<string> files;
    readonly Canvas stage = new() { Background = Brushes.Transparent, ClipToBounds = false };
    readonly Image image = new() { Stretch = Stretch.Fill };
    readonly Path dim = new() { Fill = Theme.Brush(Theme.Hex("#333333"), 0.62), IsHitTestVisible = false };
    readonly Rectangle frame = new() { StrokeThickness = 1.5, IsHitTestVisible = false };
    readonly Rectangle[] handles = new Rectangle[8];
    readonly TextBox widthBox, heightBox;
    readonly Button apply;
    Func<string, bool> selectRatio = _ => false;

    int srcW = 1, srcH = 1;
    double scale = 1;              // display px per source px
    Rect crop;                     // in source pixels
    double ratio;                  // width / height, 0 = free
    bool updatingFields;

    enum Drag { None, Move, New, Resize }
    Drag drag;
    int edgeX, edgeY;              // -1 left/top, 1 right/bottom, 0 untouched
    Point dragStart;
    Rect dragOrigin;

    bool IsVideo => Formats.Kind(files[0]) == FileKind.Video;

    public CropEditor(IReadOnlyList<string> files) : base(Formats.Kind(files[0]) == FileKind.Video ? "Crop Video" : "Crop Image", 400, 600)
    {
        this.files = files;
        frame.Stroke = Theme.Brush(Theme.Accent);
        Body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var stageHost = new Grid { Margin = new Thickness(28, 6, 28, 10) };
        stageHost.Children.Add(Preview.LoadingLabel());
        stage.HorizontalAlignment = HorizontalAlignment.Center;
        stage.VerticalAlignment = VerticalAlignment.Center;
        stage.Children.Add(image);
        stage.Children.Add(dim);
        stage.Children.Add(frame);
        for (int i = 0; i < 8; i++)
        {
            handles[i] = new Rectangle { Fill = Theme.Brush(Theme.Accent), IsHitTestVisible = false, RadiusX = 1, RadiusY = 1 };
            stage.Children.Add(handles[i]);
        }
        stage.Visibility = Visibility.Hidden;
        stageHost.Children.Add(stage);
        Body.Children.Add(stageHost);

        // Controls: aspect ratio + exact size.
        var controls = new StackPanel { Margin = new Thickness(20, 4, 20, 12) };
        var ratioRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var ratioLabel = Theme.Label("Aspect ratio");
        ratioLabel.Width = 88;
        ratioRow.Children.Add(ratioLabel);
        ratioRow.Children.Add(Theme.Segmented(Ratios, "Free", SetRatio, out selectRatio));
        controls.Children.Add(ratioRow);

        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal };
        widthBox = new TextBox { Style = Theme.Style("FieldBox"), Width = 62 };
        heightBox = new TextBox { Style = Theme.Style("FieldBox"), Width = 62 };
        sizeRow.Children.Add(FieldLabel("W"));
        sizeRow.Children.Add(widthBox);
        sizeRow.Children.Add(FieldLabel("H", 12));
        sizeRow.Children.Add(heightBox);
        sizeRow.Children.Add(FieldLabel("px", 8));
        controls.Children.Add(sizeRow);
        Grid.SetRow(controls, 1);
        Body.Children.Add(controls);

        widthBox.TextChanged += (_, _) => FieldsChanged(true);
        heightBox.TextChanged += (_, _) => FieldsChanged(false);

        apply = Theme.Primary("Apply", Apply);
        SetFooter(Theme.Soft("Reset", Reset), apply);
        DefaultAction = Apply;

        stage.MouseLeftButtonDown += StageDown;
        stage.MouseMove += StageMove;
        stage.MouseLeftButtonUp += (_, _) => { drag = Drag.None; stage.ReleaseMouseCapture(); };

        Loaded += async (_, _) =>
        {
            try
            {
                var preview = await Task.Run(() => Preview.Load(files[0], 1200));
                srcW = preview.SourceWidth;
                srcH = preview.SourceHeight;
                image.Source = preview.Bitmap;
                var fit = Preview.Fit(srcW, srcH, StageW, StageH);
                scale = fit.Width / srcW;
                stage.Width = image.Width = fit.Width;
                stage.Height = image.Height = fit.Height;
                stageHost.Children.RemoveAt(0);
                stage.Visibility = Visibility.Visible;
                crop = new Rect(0, 0, srcW, srcH);
                Update();
            }
            catch (Exception e) { ShowError(Converter.Friendly(e)); }
        };
    }

    static TextBlock FieldLabel(string text, double left = 0)
    {
        var t = Theme.Label(text);
        t.Margin = new Thickness(left, 0, 6, 0);
        return t;
    }

    void Reset()
    {
        ratio = 0;
        selectRatio("Free");
        crop = new Rect(0, 0, srcW, srcH);
        Update();
    }

    void SetRatio(string value)
    {
        if (value == "Free") { ratio = 0; return; }
        var parts = value.Split(':');
        ratio = double.Parse(parts[0]) / double.Parse(parts[1]);
        // Largest centered frame of that shape, like the reference.
        double w = srcW, h = srcW / ratio;
        if (h > srcH) { h = srcH; w = h * ratio; }
        crop = new Rect((srcW - w) / 2, (srcH - h) / 2, w, h);
        Update();
    }

    void FieldsChanged(bool widthEdited)
    {
        if (updatingFields) return;
        var box = widthEdited ? widthBox : heightBox;
        if (!int.TryParse(box.Text, out var value) || value < 1) return;
        double w = crop.Width, h = crop.Height;
        if (widthEdited) { w = Math.Min(value, srcW); if (ratio > 0) h = w / ratio; }
        else { h = Math.Min(value, srcH); if (ratio > 0) w = h * ratio; }
        if (w > srcW) { w = srcW; h = ratio > 0 ? w / ratio : h; }
        if (h > srcH) { h = srcH; w = ratio > 0 ? h * ratio : w; }
        double x = Math.Clamp(crop.X, 0, srcW - w), y = Math.Clamp(crop.Y, 0, srcH - h);
        crop = new Rect(x, y, w, h);
        Update(fromFields: true);
    }

    // ---------- Pointer ----------

    const double Grab = 10;

    (int X, int Y) EdgesAt(Point p)
    {
        var r = Display(crop);
        bool nearL = Math.Abs(p.X - r.Left) <= Grab, nearR = Math.Abs(p.X - r.Right) <= Grab;
        bool nearT = Math.Abs(p.Y - r.Top) <= Grab, nearB = Math.Abs(p.Y - r.Bottom) <= Grab;
        bool withinX = p.X >= r.Left - Grab && p.X <= r.Right + Grab, withinY = p.Y >= r.Top - Grab && p.Y <= r.Bottom + Grab;
        int x = withinY ? (nearL ? -1 : nearR ? 1 : 0) : 0;
        int y = withinX ? (nearT ? -1 : nearB ? 1 : 0) : 0;
        return (x, y);
    }

    Rect Display(Rect r) => new(r.X * scale, r.Y * scale, r.Width * scale, r.Height * scale);

    void StageDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(stage);
        (edgeX, edgeY) = EdgesAt(p);
        dragStart = p;
        dragOrigin = crop;
        if (edgeX != 0 || edgeY != 0) drag = Drag.Resize;
        else if (Display(crop).Contains(p)) drag = Drag.Move;
        else drag = Drag.New;
        stage.CaptureMouse();
        e.Handled = true;
    }

    void StageMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(stage);
        if (drag == Drag.None)
        {
            var (x, y) = EdgesAt(p);
            stage.Cursor = (x, y) switch
            {
                (-1, -1) or (1, 1) => Cursors.SizeNWSE,
                (1, -1) or (-1, 1) => Cursors.SizeNESW,
                (_, 0) when x != 0 => Cursors.SizeWE,
                (0, _) when y != 0 => Cursors.SizeNS,
                _ => Display(crop).Contains(p) ? Cursors.SizeAll : Cursors.Cross,
            };
            return;
        }
        double dx = (p.X - dragStart.X) / scale, dy = (p.Y - dragStart.Y) / scale;
        const double min = 8;
        switch (drag)
        {
            case Drag.Move:
                crop = new Rect(Math.Clamp(dragOrigin.X + dx, 0, srcW - dragOrigin.Width), Math.Clamp(dragOrigin.Y + dy, 0, srcH - dragOrigin.Height), dragOrigin.Width, dragOrigin.Height);
                break;
            case Drag.New:
            {
                var a = new Point(dragStart.X / scale, dragStart.Y / scale);
                var b = new Point(Math.Clamp(p.X / scale, 0, srcW), Math.Clamp(p.Y / scale, 0, srcH));
                double w = Math.Abs(b.X - a.X), h = Math.Abs(b.Y - a.Y);
                if (ratio > 0) { if (w / Math.Max(h, 1) > ratio) w = h * ratio; else h = w / ratio; }
                double x = b.X < a.X ? a.X - w : a.X, y = b.Y < a.Y ? a.Y - h : a.Y;
                if (w >= min && h >= min) crop = Clamp(new Rect(x, y, w, h));
                break;
            }
            case Drag.Resize:
            {
                double left = dragOrigin.Left, top = dragOrigin.Top, right = dragOrigin.Right, bottom = dragOrigin.Bottom;
                if (edgeX < 0) left = Math.Clamp(left + dx, 0, right - min);
                if (edgeX > 0) right = Math.Clamp(right + dx, left + min, srcW);
                if (edgeY < 0) top = Math.Clamp(top + dy, 0, bottom - min);
                if (edgeY > 0) bottom = Math.Clamp(bottom + dy, top + min, srcH);
                var r = new Rect(new Point(left, top), new Point(right, bottom));
                if (ratio > 0) r = KeepRatio(r);
                crop = r;
                break;
            }
        }
        Update();
    }

    /// <summary>Re-shapes a resized frame to the locked ratio, anchored on the side that didn't move.</summary>
    Rect KeepRatio(Rect r)
    {
        double w = r.Width, h = r.Height;
        bool widthLeads = edgeY == 0 || (edgeX != 0 && Math.Abs(w - dragOrigin.Width) >= Math.Abs(h - dragOrigin.Height));
        if (widthLeads) h = w / ratio; else w = h * ratio;
        double x = edgeX < 0 ? dragOrigin.Right - w : edgeX > 0 ? dragOrigin.Left : dragOrigin.X + (dragOrigin.Width - w) / 2;
        double y = edgeY < 0 ? dragOrigin.Bottom - h : edgeY > 0 ? dragOrigin.Top : dragOrigin.Y + (dragOrigin.Height - h) / 2;
        var result = new Rect(x, y, w, h);
        if (result.Left < 0 || result.Top < 0 || result.Right > srcW || result.Bottom > srcH) return crop; // keep the last valid frame
        return result;
    }

    Rect Clamp(Rect r)
    {
        double x = Math.Clamp(r.X, 0, srcW - 1), y = Math.Clamp(r.Y, 0, srcH - 1);
        return new Rect(x, y, Math.Min(r.Width, srcW - x), Math.Min(r.Height, srcH - y));
    }

    // ---------- Drawing ----------

    void Update(bool fromFields = false)
    {
        var r = Display(crop);
        var full = new Rect(0, 0, stage.Width, stage.Height);
        dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), new RectangleGeometry(r));
        Canvas.SetLeft(frame, r.X);
        Canvas.SetTop(frame, r.Y);
        frame.Width = r.Width;
        frame.Height = r.Height;

        // Corner squares and edge bars, as in the reference.
        const double sq = 8, bar = 16, thin = 4;
        Point[] spots =
        [
            new(r.Left, r.Top), new(r.Right, r.Top), new(r.Left, r.Bottom), new(r.Right, r.Bottom),
            new(r.Left + r.Width / 2, r.Top), new(r.Left + r.Width / 2, r.Bottom), new(r.Left, r.Top + r.Height / 2), new(r.Right, r.Top + r.Height / 2),
        ];
        for (int i = 0; i < 8; i++)
        {
            double w = i < 4 ? sq : i < 6 ? bar : thin, h = i < 4 ? sq : i < 6 ? thin : bar;
            handles[i].Width = w;
            handles[i].Height = h;
            Canvas.SetLeft(handles[i], spots[i].X - w / 2);
            Canvas.SetTop(handles[i], spots[i].Y - h / 2);
        }

        if (!fromFields)
        {
            updatingFields = true;
            widthBox.Text = ((int)Math.Round(crop.Width)).ToString();
            heightBox.Text = ((int)Math.Round(crop.Height)).ToString();
            updatingFields = false;
        }
    }

    void Apply()
    {
        var rect = new NormRect(crop.X / srcW, crop.Y / srcH, crop.Width / srcW, crop.Height / srcH);
        if (IsVideo)
        {
            // The whole video is re-encoded: that takes a while, so it runs behind a progress toast.
            var at = Anchor.X == 0 && Anchor.Y == 0 ? CenterOnScreen() : Anchor;
            var video = files[0];
            CloseAnimated();
            _ = ToastWindow.Run("Cropping video", at,
                (ct, report) => Media.Crop(video, rect, p => report(p, $"{p:P0}"), ct),
                output => ("Cropped", System.IO.Path.GetFileName(output), output),
                "Couldn't crop");
            return;
        }
        SaveInBackground("Saving…", apply, (_, _) => files.Select(f => ImageTools.Crop(f, rect)).ToList(), "Cropped");
    }
}

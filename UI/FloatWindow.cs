using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FileWheel.Core;
using FileWheel.Native;
using FileWheel.UI.Editors;

namespace FileWheel.UI;

/// <summary>
/// "Float": an always-on-top glass preview of a file you can keep beside your work. PDFs show every
/// page in a scrolling column; text, Word documents and sheets scroll in full; tall images scroll too.
/// </summary>
public sealed class FloatWindow : Window
{
    const double M = 20;
    const int PdfDpi = 100;
    readonly string file;
    readonly GlassCard card;
    readonly Grid body = new();
    readonly Button close;
    readonly POINT anchor;
    readonly RECT work, bounds;
    readonly double scale;

    public FloatWindow(string file, POINT at)
    {
        anchor = at;
        this.file = file;
        Title = Path.GetFileName(file);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Width = 360 + M * 2;
        Height = 300 + M * 2;
        MinWidth = 160;
        MinHeight = 140;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/FileWheel.ico")); } catch { }

        (work, bounds, scale) = Backdrop.Screen(at);
        var shot = Backdrop.Capture(bounds);
        int half = (int)(200 * scale);
        var palette = Palette.For(shot, Backdrop.Region(bounds, new RECT { Left = at.X - half, Top = at.Y - half, Right = at.X + half, Bottom = at.Y + half }));
        Theme.Current = palette;
        palette.ApplyTo(Resources);
        card = new GlassCard(palette, palette.PanelTint, palette.PanelTintOpacity * 0.75, 14, 0.28) { Margin = new Thickness(M) };
        card.SetBackdrop(shot, bounds);
        body.Margin = new Thickness(8);
        card.Content.Children.Add(body);
        close = new Button
        {
            Style = Theme.Style("CloseButton"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 8, 8, 0),
            Opacity = 0,
        };
        close.Click += (_, _) => Close();
        card.Content.Children.Add(close);
        Content = card;

        MouseEnter += (_, _) => Theme.Animate(close, OpacityProperty, 1, 120);
        MouseLeave += (_, _) => Theme.Animate(close, OpacityProperty, 0, 200);
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) { try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); } catch { } return; }
            // Scrollbars and selectable text keep their own dragging; anywhere else moves the window.
            if (e.OriginalSource is Thumb or Button || IsInside<ScrollBar>(e.OriginalSource) || IsInside<TextBox>(e.OriginalSource)) return;
            try { DragMove(); } catch (InvalidOperationException) { }
        };
        // Ctrl + wheel: see-through. A plain wheel scrolls.
        PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.08 : -0.08), 0.25, 1);
            e.Handled = true;
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += async (_, _) => await Fill();
    }

    async Task Fill()
    {
        try
        {
            switch (Formats.Kind(file))
            {
                case FileKind.Image:
                {
                    var p = await Task.Run(() => Preview.Load(file, 1600));
                    if (p.SourceHeight > p.SourceWidth * 1.7)
                    {
                        // Long screenshots and receipts: full width, scroll down.
                        var picture = new Image { Source = p.Bitmap, Stretch = Stretch.Uniform };
                        ShowScrolling(picture, 380, 520, viewport => picture.Width = viewport);
                    }
                    else ShowContent(new Image { Source = p.Bitmap, Stretch = Stretch.Uniform }, p.SourceWidth, p.SourceHeight);
                    break;
                }
                case FileKind.Pdf:
                    await ShowPdf();
                    break;
                case FileKind.Video:
                case FileKind.Audio:
                {
                    var media = new MediaElement { Source = new Uri(file), LoadedBehavior = MediaState.Manual, Stretch = Stretch.Uniform, IsMuted = true };
                    media.MediaEnded += (_, _) => { media.Position = TimeSpan.Zero; media.Play(); };
                    media.MediaOpened += (_, _) => { if (media.NaturalVideoWidth > 0) Resize(media.NaturalVideoWidth, media.NaturalVideoHeight); };
                    media.MouseLeftButtonUp += (_, _) => media.IsMuted = !media.IsMuted;
                    media.ToolTip = "Click to toggle sound";
                    body.Children.Add(media);
                    media.Play();
                    break;
                }
                case FileKind.Text or FileKind.Delimited or FileKind.Subtitle:
                case FileKind.Office when Formats.Ext(file) is "docx" or "xlsx":
                {
                    var text = await Task.Run(ReadText);
                    var box = new TextBox
                    {
                        Text = text,
                        IsReadOnly = true,
                        TextWrapping = TextWrapping.Wrap,
                        BorderThickness = new Thickness(0),
                        Background = Brushes.Transparent,
                        Foreground = Theme.Brush(Theme.Ink),
                        SelectionBrush = Theme.Brush(Theme.Accent),
                        FontFamily = Formats.Kind(file) == FileKind.Office ? Theme.UiFont : new FontFamily("Cascadia Mono, Consolas"),
                        FontSize = 12,
                        Padding = new Thickness(6),
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    };
                    body.Children.Add(box);
                    Width = 420 + 16 + M * 2;
                    Height = 480 + 16 + M * 2;
                    card.AlignBackdrop(this);
                    break;
                }
                default:
                {
                    var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    var icon = Theme.Icon(Directory.Exists(file) ? "Folder" : "Copy", 40, Theme.InkSoft);
                    icon.HorizontalAlignment = HorizontalAlignment.Center;
                    stack.Children.Add(icon);
                    var name = Theme.Text(Path.GetFileName(file.TrimEnd('\\')), 13, Theme.Ink, FontWeights.SemiBold);
                    name.HorizontalAlignment = HorizontalAlignment.Center;
                    name.Margin = new Thickness(0, 10, 0, 0);
                    stack.Children.Add(name);
                    body.Children.Add(stack);
                    break;
                }
            }
        }
        catch (Exception e)
        {
            body.Children.Add(Theme.Text(Converter.Friendly(e), 12, Theme.InkSoft));
        }
    }

    /// <summary>The whole text (up to 2 MB), or the readable text of a Word document or the first sheet of a workbook.</summary>
    string ReadText()
    {
        const int Limit = 2_000_000;
        var ext = Formats.Ext(file);
        if (ext == "docx") return WordText.Read(file, markdown: false);
        if (ext == "xlsx") return string.Join("\n", Tabular.ReadXlsx(file).Select(r => string.Join("\t", r)));
        using var reader = new StreamReader(file);
        var buffer = new char[Limit];
        int n = reader.ReadBlock(buffer, 0, Limit);
        var text = new string(buffer, 0, n);
        return reader.Peek() >= 0 ? text + "\n\n… (the rest is too long to preview — double-click to open the file)" : text;
    }

    // ---------- PDF: every page in a scrolling column, drawn as it comes into view ----------

    async Task ShowPdf()
    {
        var first = await Task.Run(() => { using var page = PdfRenderer.Render(file, 0, PdfDpi); return Converter.ToBitmap(page); });
        int count = await Task.Run(() => PdfRenderer.PageCount(file));
        double aspect = first.PixelHeight / (double)Math.Max(1, first.PixelWidth);

        var pages = new StackPanel();
        var frames = new List<Border>();
        var pictures = new List<Image>();
        var aspects = new List<double>();
        for (int i = 0; i < count; i++)
        {
            var picture = new Image { Stretch = Stretch.Uniform, Source = i == 0 ? first : null };
            var frame = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = Brushes.White,
                Margin = new Thickness(0, 0, 0, i < count - 1 ? 8 : 0),
                Child = picture,
                SnapsToDevicePixels = true,
            };
            pages.Children.Add(frame);
            frames.Add(frame);
            pictures.Add(picture);
            aspects.Add(aspect);
        }

        var counterText = Theme.Text($"1 / {count}", 11, Colors.White, FontWeights.SemiBold);
        var counter = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(10, 3, 10, 4),
            CornerRadius = new CornerRadius(10),
            Background = Theme.Brush(Color.FromArgb(150, 20, 20, 24)),
            IsHitTestVisible = false,
            Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed,
            Child = counterText,
        };

        double width = aspect > 1.2 ? 400 : 440;
        var scroll = ShowScrolling(pages, width, Math.Min(560, width * aspect), viewport =>
        {
            for (int i = 0; i < frames.Count; i++) frames[i].Height = viewport * aspects[i];
        });
        body.Children.Add(counter);

        var loading = new HashSet<int> { 0 };

        async void Load(int index)
        {
            if (!loading.Add(index)) return;
            try
            {
                var bmp = await Task.Run(() => { using var page = PdfRenderer.Render(file, index, PdfDpi); return Converter.ToBitmap(page); });
                if (!loading.Contains(index)) return;
                pictures[index].Source = bmp;
                double real = bmp.PixelHeight / (double)Math.Max(1, bmp.PixelWidth);
                if (Math.Abs(real - aspects[index]) > 0.01)
                {
                    aspects[index] = real;
                    frames[index].Height = scroll.ViewportWidth * real;
                }
            }
            catch (Exception e) { Program.Log("Float page failed: " + e.Message); }
        }

        void Update()
        {
            double top = scroll.VerticalOffset, height = Math.Max(1, scroll.ViewportHeight);
            int current = 0;
            for (int i = 0; i < frames.Count; i++)
            {
                double y = frames[i].TranslatePoint(new Point(0, 0), pages).Y, h = frames[i].ActualHeight;
                if (y + h / 2 <= top + height / 2) current = i;
                if (y + h > top - height && y < top + height * 2) Load(i);
                else if (i != 0 && pictures[i].Source != null && (y + h < top - height * 6 || y > top + height * 7))
                {
                    // Pages far away are let go, so long documents don't fill memory.
                    pictures[i].Source = null;
                    loading.Remove(i);
                }
            }
            counterText.Text = $"{current + 1} / {count}";
        }
        scroll.ScrollChanged += (_, _) => Update();
    }

    /// <summary>
    /// Puts <paramref name="content"/> in a vertical scroller; <paramref name="fitWidth"/> is told the
    /// usable width whenever it changes so the content can size itself to it.
    /// </summary>
    ScrollViewer ShowScrolling(FrameworkElement content, double width, double height, Action<double> fitWidth)
    {
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            PanningMode = PanningMode.VerticalOnly,
        };
        scroll.SizeChanged += (_, _) => fitWidth(Math.Max(40, scroll.ViewportWidth));
        var clip = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = scroll };
        body.Children.Add(clip);
        Width = width + 16 + M * 2;
        Height = height + 16 + M * 2;
        card.AlignBackdrop(this);
        return scroll;
    }

    static bool IsInside<T>(object? source) where T : DependencyObject
    {
        for (var d = source as DependencyObject; d != null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is T) return true;
        return false;
    }

    void ShowContent(UIElement element, double w, double h)
    {
        var clip = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = element };
        body.Children.Add(clip);
        Resize(w, h);
    }

    void Resize(double w, double h)
    {
        var fit = Preview.Fit(w, h, 420, 420);
        Width = fit.Width + 16 + M * 2;
        Height = fit.Height + 16 + M * 2;
        card.AlignBackdrop(this);
    }

    public void Present()
    {
        var at = anchor;
        Left = (at.X / scale) - Width / 2 + new Random().Next(-30, 30);
        Top = (at.Y / scale) - Height / 2 + new Random().Next(-30, 30);
        Left = Math.Clamp(Left, work.Left / scale, work.Right / scale - Width);
        Top = Math.Clamp(Top, work.Top / scale, work.Bottom / scale - Height);
        LocationChanged += (_, _) => card.AlignBackdrop(this);
        SizeChanged += (_, _) => card.AlignBackdrop(this);
        Show();
        card.AlignBackdrop(this);
        Theme.Animate(this, OpacityProperty, 1, 160, from: 0);
    }
}

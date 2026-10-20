using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FileWheel.Core;
using FileWheel.Native;
using FileWheel.UI;
using FileWheel.UI.Editors;

namespace FileWheel;

/// <summary>
/// Renders every surface to PNG over a fixed wallpaper so the look can be checked against the reference:
///   FileWheel --snapshot &lt;outDir&gt; &lt;wallpaper.png&gt; &lt;sample.pdf&gt; &lt;sample.jpg&gt;
/// </summary>
public static class Snapshots
{
    const int ScreenW = 1440, ScreenH = 900;

    public static int Run(string[] args)
    {
        string outDir = args[0], wallpaper = args[1], pdf = args[2], jpg = args[3];
        string only = args.Length > 4 ? args[4] : "";
        Directory.CreateDirectory(outDir);
        Backdrop.UsePicture(wallpaper, ScreenW, ScreenH);
        Backdrop.VirtualScreen = new RECT { Left = 0, Top = 0, Right = ScreenW, Bottom = ScreenH };
        if (Environment.GetEnvironmentVariable("FILEWHEEL_THEME") is { Length: > 0 } theme) Settings.Current.Theme = theme;
        var background = new BitmapImage(new Uri(Path.GetFullPath(wallpaper)));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/FileWheel;component/UI/Theme.xaml") });
        int code = 0;
        app.Startup += (_, _) =>
        {
            try
            {
                Formats.Warm();
                bool Want(string name) => only.Length == 0 || name.Contains(only);
                int Index(IReadOnlyList<WheelItem> items, string id) => items.ToList().FindIndex(i => i.Id == id);

                if (Want("wheel"))
                {
                    var pdfItems = Formats.Items(WheelMode.Convert, [pdf]);
                    Wheel(outDir, "wheel-pdf-convert", background, [pdf], WheelMode.Convert, Index(pdfItems, "jpg"));
                    var jpgItems = Formats.Items(WheelMode.Convert, [jpg]);
                    Wheel(outDir, "wheel-jpg-convert", background, [jpg], WheelMode.Convert, Index(jpgItems, "tiff"));
                    Wheel(outDir, "wheel-jpg-tools", background, [jpg], WheelMode.Tools, 3);
                    Wheel(outDir, "wheel-jpg-tools-idle", background, [jpg], WheelMode.Tools, -1);
                    Wheel(outDir, "wheel-jpg-actions", background, [jpg], WheelMode.Actions, 0);
                    Wheel(outDir, "wheel-pdf-tools", background, [pdf], WheelMode.Tools, -1);
                }
                if (Want("share")) Wheel(outDir, "wheel-share", background, [jpg], WheelMode.Share, 0);
                if (Want("same")) Wheel(outDir, "wheel-same", background, [pdf], WheelMode.Same, 0);
                if (Want("rings"))
                {
                    Wheel(outDir, "ring-video-tools", background, ["Clip.mp4"], WheelMode.Tools, 0);
                    Wheel(outDir, "ring-audio-tools", background, ["Tone.mp3"], WheelMode.Tools, 2);
                    Wheel(outDir, "ring-speed", background, ["Clip.mp4"], WheelMode.Speed, 5);
                    Wheel(outDir, "ring-channels", background, ["Tone.mp3"], WheelMode.Channels, 0);
                    Wheel(outDir, "ring-archive-convert", background, ["Bundle.rar"], WheelMode.Convert, 0);
                    Wheel(outDir, "ring-archive-tools", background, ["Bundle.rar"], WheelMode.Tools, 0);
                    Wheel(outDir, "ring-pdf-convert", background, [pdf], WheelMode.Convert, 1);
                    Wheel(outDir, "ring-pdf-tools", background, [pdf], WheelMode.Tools, 2);
                }
                if (Want("organize")) Panel(outDir, "organize", background, new PdfOrganizePanel(pdf), 2500);
                if (Want("rename")) Panel(outDir, "rename", background, new RenamePanel([jpg], new POINT(ScreenW / 2, ScreenH / 2)));
                if (Want("shareapps")) Panel(outDir, "share-apps", background, new ShareAppsPanel([jpg], new POINT(ScreenW / 2, ScreenH / 2)), 5000);
                if (Want("float"))
                {
                    // Float a PDF, then scroll down a few pages the way the mouse wheel would.
                    var floating = new FloatWindow(pdf, new POINT(ScreenW / 2, ScreenH / 2)) { Left = -30000, Top = -30000 };
                    floating.Show();
                    Pump(1800);
                    SaveWindow(outDir, "float-top", background, floating, new Point(480, 120));
                    if (Find<System.Windows.Controls.ScrollViewer>(floating) is { } scroller)
                    {
                        scroller.ScrollToVerticalOffset(scroller.ViewportHeight * 2.6);
                        Pump(1800);
                    }
                    SaveWindow(outDir, "float-scrolled", background, floating, new Point(480, 120));
                    floating.Close();
                }
                if (Want("toast"))
                {
                    var toast = new ToastWindow("Converting to JPG", null, new POINT(ScreenW / 2, ScreenH / 2 - 120)) { Left = -30000, Top = -30000 };
                    toast.Show();
                    Pump(100);
                    toast.SetProgress(0.12, "1 of 1 saved");
                    Pump(400);
                    SaveWindow(outDir, "toast", background, toast, new Point((ScreenW - toast.Width) / 2, ScreenH / 2 - 120 - toast.Height / 2));
                    toast.Close();
                }
                if (Want("crop")) Panel(outDir, "crop", background, new CropEditor([jpg]));
                if (Want("addbg")) Panel(outDir, "addbg", background, new BackgroundEditor([jpg]));
                if (Want("compress")) Panel(outDir, "compress", background, new CompressEditor([jpg]), 2500);
                if (Want("edit")) Panel(outDir, "edit", background, new AdjustEditor([jpg]));
                if (Want("annotate")) Panel(outDir, "annotate", background, new AnnotateEditor(jpg));
                if (Want("redact")) Panel(outDir, "redact", background, new RedactEditor(jpg));
                if (Want("metadata")) Panel(outDir, "metadata", background, new MetadataPanel([jpg]));
                if (Want("pdfextract")) Panel(outDir, "pdfextract", background, new PdfExtractPanel(pdf));
                if (Want("pdfcompress")) Panel(outDir, "pdfcompress", background, new PdfCompressPanel([pdf]));
                if (Want("home")) Panel(outDir, "home", background, new HomeWindow(null!));
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                File.WriteAllText(Path.Combine(outDir, "error.txt"), e.ToString());
                code = 1;
            }
            app.Shutdown();
        };
        app.Run();
        return code;
    }

    static T? Find<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (Find<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    static void Wheel(string outDir, string name, BitmapSource background, IReadOnlyList<string> files, WheelMode mode, int hover)
    {
        var at = new Point(ScreenW / 2 - WheelView.ViewSize / 2, ScreenH / 2 - WheelView.ViewSize / 2);
        int r = (int)WheelView.DiscR;
        int cx = (int)(at.X + WheelView.Cx), cy = (int)(at.Y + WheelView.Cy);
        var behind = Backdrop.Capture(new RECT { Left = cx - r, Top = cy - r, Right = cx + r, Bottom = cy + r });
        Theme.Current = Palette.For(behind);
        var view = WheelWindow.BuildPreview(files, mode, hover);
        view.SetBackdrop(behind);
        view.Measure(new Size(WheelView.ViewSize, WheelView.ViewSize));
        view.Arrange(new Rect(0, 0, WheelView.ViewSize, WheelView.ViewSize));
        view.UpdateLayout();
        Compose(outDir, name, background, view, at, new Size(WheelView.ViewSize, WheelView.ViewSize));
    }

    static void Panel(string outDir, string name, BitmapSource background, Window panel, int waitMs = 1500)
    {
        panel.Left = -30000;
        panel.Top = -30000;
        panel.Show();
        Pump(waitMs);
        var at = new Point((ScreenW - panel.Width) / 2, Math.Max(0, (ScreenH - panel.Height) / 2));
        SaveWindow(outDir, name, background, panel, at);
        panel.Close();
    }

    static void SaveWindow(string outDir, string name, BitmapSource background, Window window, Point at)
    {
        if (window is GlassPanel gp) gp.PrepareSnapshot(at, 1, new RECT { Left = 0, Top = 0, Right = ScreenW, Bottom = ScreenH });
        if (window is ToastWindow toast) toast.PrepareSnapshot(at, new RECT { Left = 0, Top = 0, Right = ScreenW, Bottom = ScreenH });
        Pump(150);
        Compose(outDir, name, background, (Visual)window.Content, at, new Size(window.Width, window.Height));
    }

    static void Compose(string outDir, string name, BitmapSource background, Visual element, Point at, Size size)
    {
        int w = (int)Math.Ceiling(size.Width), h = (int)Math.Ceiling(size.Height);
        var layer = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        layer.Render(element);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // The wallpaper as it sits behind this element on the virtual 1440×900 screen.
            dc.PushTransform(new TranslateTransform(-at.X, -at.Y));
            dc.DrawImage(background, new Rect(0, 0, ScreenW, ScreenH));
            dc.Pop();
            dc.DrawImage(layer, new Rect(0, 0, w, h));
        }
        var final = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        final.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(final));
        using var fs = File.Create(Path.Combine(outDir, name + ".png"));
        encoder.Save(fs);
    }
}

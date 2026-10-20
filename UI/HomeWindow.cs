using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using FileWheel.Core;

namespace FileWheel.UI;

/// <summary>The welcome/settings card shown from the tray.</summary>
public sealed class HomeWindow : GlassPanel
{
    readonly App? app;
    readonly Button pause;

    public HomeWindow(App? app) : base("FileWheel", 372, 586)
    {
        this.app = app;
        var stack = new StackPanel { Margin = new Thickness(22, 0, 22, 10) };

        // A live, non-interactive wheel as the hero.
        var demo = new WheelView();
        demo.SetIdleText("1.2 MB");
        demo.SetItems(Formats.Items(WheelMode.Convert, ["Photo.jpg"]), false, animate: false);
        demo.SetHover(3, instant: true);
        demo.IsHitTestVisible = false;
        stack.Children.Add(new Viewbox { Child = demo, Height = 136, Margin = new Thickness(0, -6, 0, 2) });

        var headline = Theme.Text("Hold Shift. Drag a file.", 18, Theme.Ink, FontWeights.SemiBold);
        headline.FontFamily = Theme.DisplayFont;
        headline.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(headline);
        var sub = Theme.Text("Works in Explorer, on the desktop, anywhere. Results are saved next to the original.", 11.5, Theme.InkSoft);
        sub.TextWrapping = TextWrapping.Wrap;
        sub.TextTrimming = TextTrimming.None;
        sub.TextAlignment = TextAlignment.Center;
        sub.Margin = new Thickness(6, 3, 6, 10);
        stack.Children.Add(sub);

        var keys = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = Theme.SurfaceBrush,
            BorderBrush = Theme.SurfaceBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 5, 10, 5),
        };
        var keyRows = new UniformGrid2();
        keyRows.Add("Shift", "Hold while dragging — convert");
        keyRows.Add("Ctrl", "Tap mid-drag — edit tools");
        keyRows.Add("Alt", "Tap mid-drag — share, copy path…");
        keyRows.Add("Esc", "Cancel (or let go in the middle)");
        keys.Child = keyRows.Panel;
        stack.Children.Add(keys);

        var look = new DockPanel { Margin = new Thickness(2, 10, 2, 0) };
        var lookSwitch = Theme.Segmented(["Glass", "Tangerine"], Settings.Current.Theme == "Tangerine" ? "Tangerine" : "Glass", v =>
        {
            Settings.Current.Theme = v;
            Settings.Save();
            Dispatcher.BeginInvoke(Restyle);
        }, out _);
        DockPanel.SetDock(lookSwitch, Dock.Right);
        look.Children.Add(lookSwitch);
        look.Children.Add(Theme.Text("Look", 12, Theme.Ink, FontWeights.Medium));
        stack.Children.Add(look);
        stack.Children.Add(Toggle("Start with Windows", Settings.StartWithWindows, v => Settings.StartWithWindows = v));
        stack.Children.Add(Toggle("Show in the “Send to” menu", Settings.InSendTo, v => Settings.InSendTo = v));

        var drop = new Border
        {
            Margin = new Thickness(0, 10, 0, 0),
            Height = 36,
            CornerRadius = new CornerRadius(9),
            BorderBrush = Theme.Brush(Theme.InkSoft, 0.35),
            BorderThickness = new Thickness(1.2),
            Background = Theme.WellBrush,
            AllowDrop = true,
            Child = Theme.Text("…or drop files here", 12, Theme.InkMuted, FontWeights.Medium),
        };
        ((TextBlock)drop.Child).HorizontalAlignment = HorizontalAlignment.Center;
        drop.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        drop.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                Dispatcher.BeginInvoke(() => app?.OpenWheel(files, WheelMode.Convert));
            e.Handled = true;
        };
        stack.Children.Add(drop);

        Body.Children.Add(stack);
        pause = Theme.Soft(app?.Paused == true ? "Resume" : "Pause", TogglePause);
        SetFooter(pause, Theme.Primary("Choose Files…", () => { CloseAnimated(); app?.PickFiles(); }));
    }

    /// <summary>
    /// Reopens with the new look. This window leaves the screen first, so the new one's glass is
    /// made from the desktop behind it rather than from this card.
    /// </summary>
    void Restyle()
    {
        Hide();
        Theme.After(160, () =>
        {
            Close();
            app?.ShowHome(fresh: true);
        });
    }

    void TogglePause()
    {
        if (app == null) return;
        app.Paused = !app.Paused;
        pause.Content = app.Paused ? "Resume" : "Pause";
    }

    static DockPanel Toggle(string label, bool initial, Action<bool> changed)
    {
        bool on = initial;
        var knob = new Ellipse { Width = 14, Height = 14, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2) };
        var pill = new Border { Width = 34, Height = 18, CornerRadius = new CornerRadius(9), Child = knob, Cursor = Cursors.Hand };
        var move = new TranslateTransform();
        knob.RenderTransform = move;
        void Paint(bool animate)
        {
            pill.Background = on ? Theme.Brush(Theme.Accent) : Theme.Brush(Theme.InkSoft, 0.3);
            if (animate) Theme.Animate(move, TranslateTransform.XProperty, on ? 16 : 0, 160, Theme.Spring);
            else move.X = on ? 16 : 0;
        }
        Paint(false);
        var row = new DockPanel { Margin = new Thickness(2, 10, 2, 0), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        DockPanel.SetDock(pill, Dock.Right);
        row.Children.Add(pill);
        row.Children.Add(Theme.Text(label, 12, Theme.Ink, FontWeights.Medium));
        row.MouseLeftButtonUp += (_, _) =>
        {
            try
            {
                changed(!on);
                on = !on;
                Paint(true);
            }
            catch (Exception e) { Program.Log("Setting failed: " + e.Message); }
        };
        return row;
    }

    /// <summary>Key chip + description rows.</summary>
    sealed class UniformGrid2
    {
        public readonly StackPanel Panel = new();

        public void Add(string key, string text)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var chip = new Border
            {
                Width = 44,
                CornerRadius = new CornerRadius(6),
                Background = Theme.Brush(Theme.Current.Field),
                BorderBrush = Theme.Brush(Theme.InkSoft, 0.25),
                BorderThickness = new Thickness(1, 1, 1, 2),
                Padding = new Thickness(0, 2, 0, 3),
                Child = Theme.Text(key, 11, Theme.Ink, FontWeights.SemiBold),
            };
            ((TextBlock)chip.Child).HorizontalAlignment = HorizontalAlignment.Center;
            row.Children.Add(chip);
            var t = Theme.Text(text, 12, Theme.InkSoft);
            t.Margin = new Thickness(10, 0, 0, 0);
            row.Children.Add(t);
            Panel.Children.Add(row);
        }
    }
}

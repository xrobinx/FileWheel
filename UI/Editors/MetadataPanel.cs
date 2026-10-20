using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FileWheel.Core;

namespace FileWheel.UI.Editors;

/// <summary>"Metadata": everything the file says about itself, with one click to save a clean copy.</summary>
public sealed class MetadataPanel : GlassPanel
{
    readonly IReadOnlyList<string> files;
    readonly StackPanel list = new() { Margin = new Thickness(18, 0, 14, 8) };
    readonly Button strip;
    List<MetaEntry> entries = [];

    public MetadataPanel(IReadOnlyList<string> files) : base("Metadata", 420, 560)
    {
        this.files = files;
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list, Margin = new Thickness(0, 0, 4, 0) };
        list.Children.Add(Preview.LoadingLabel());
        Body.Children.Add(scroll);

        strip = Theme.Primary(files.Count > 1 ? $"Remove from {files.Count} Files" : "Remove Metadata", Strip);
        strip.Visibility = files.All(ImageTools.CanStripMetadata) ? Visibility.Visible : Visibility.Collapsed;
        SetFooter(Theme.Soft("Copy All", CopyAll), strip);

        Loaded += async (_, _) =>
        {
            try
            {
                entries = await Task.Run(() => ImageTools.ReadMetadata(files[0]));
                Fill();
            }
            catch (Exception e) { ShowError(Converter.Friendly(e)); }
        };
    }

    void Fill()
    {
        list.Children.Clear();
        if (files.Count > 1)
        {
            var note = Theme.Text($"Showing the first of {files.Count} files.", 11.5, Theme.InkMuted);
            note.Margin = new Thickness(0, 0, 0, 6);
            list.Children.Add(note);
        }
        foreach (var group in entries.GroupBy(e => e.Group))
        {
            var header = Theme.Text(group.Key.ToUpperInvariant(), 10.5, Theme.InkMuted, FontWeights.Bold);
            header.Margin = new Thickness(0, 10, 0, 4);
            list.Children.Add(header);
            var card = new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = Theme.SurfaceBrush,
                BorderBrush = Theme.SurfaceBorderBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12, 6, 12, 6),
            };
            var rows = new StackPanel();
            foreach (var e in group)
            {
                var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                var name = Theme.Text(e.Name, 12, Theme.InkSoft);
                name.Width = 120;
                name.VerticalAlignment = VerticalAlignment.Top;
                row.Children.Add(name);
                var value = new TextBox
                {
                    Text = e.Value,
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = Theme.Brush(Theme.Ink),
                    FontFamily = Theme.UiFont,
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Padding = new Thickness(0),
                    SelectionBrush = Theme.Brush(Theme.Accent),
                };
                row.Children.Add(value);
                rows.Children.Add(row);
            }
            card.Child = rows;
            list.Children.Add(card);
        }
    }

    void CopyAll()
    {
        Clipboard.SetText(string.Join(Environment.NewLine, entries.Select(e => $"{e.Group} · {e.Name}: {e.Value}")));
        ToastWindow.Flash("Copied", "Metadata copied to the clipboard", CenterOnScreen());
    }

    void Strip() => SaveInBackground("Removing…", strip, (_, _) => files.Select(ImageTools.StripMetadata).ToList(), "Metadata removed");
}

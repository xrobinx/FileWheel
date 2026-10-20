using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FileWheel.Core;
using FileWheel.Native;

namespace FileWheel.UI;

/// <summary>
/// RENAME on the Copy / Rename ring: a small bar where the wheel was. Type a name and press Enter;
/// the extension stays. With several files they become "name 1", "name 2"…
/// ← goes back to the ring, × or Esc cancels.
/// </summary>
public sealed class RenamePanel : GlassPanel
{
    readonly IReadOnlyList<string> files;
    readonly POINT at;
    readonly TextBox box;
    readonly TextBlock note;
    readonly Button apply;

    public RenamePanel(IReadOnlyList<string> files, POINT at) : base(files.Count == 1 ? "Rename" : $"Rename {files.Count} files", 400, 120, near: at)
    {
        this.files = files;
        this.at = at;
        Anchor = at;
        EnableBack(() => App.Current?.OpenWheelAt(files, WheelMode.Same, at, back: () => App.Current?.OpenWheelAt(files, WheelMode.Convert, at)));
        HideFooter();

        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var ext = Path.GetExtension(files[0]);
        var row = new Grid { Margin = new Thickness(16, 2, 16, 0), VerticalAlignment = VerticalAlignment.Top };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        box = new TextBox
        {
            Style = Theme.Style("FieldBox"),
            Text = Path.GetFileNameWithoutExtension(files[0]),
            FontSize = 13.5,
            Height = 34,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Children.Add(box);

        var suffix = Theme.Text(files.Count == 1 ? ext : $" 1{ext}, 2{ext}…", 13, Theme.InkMuted, FontWeights.Medium);
        suffix.Margin = new Thickness(6, 0, 10, 0);
        Grid.SetColumn(suffix, 1);
        row.Children.Add(suffix);

        apply = Theme.Primary("Rename", Apply);
        apply.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(apply, 2);
        row.Children.Add(apply);
        Body.Children.Add(row);

        note = Theme.Text(files.Count == 1 ? "Press Enter to rename · Esc to cancel" : $"{files.Count} files, numbered in the order you dragged them", 11.5, Theme.InkMuted);
        note.Margin = new Thickness(18, 8, 18, 12);
        Grid.SetRow(note, 1);
        Body.Children.Add(note);

        DefaultAction = Apply;
        box.TextChanged += (_, _) => apply.IsEnabled = box.Text.Trim().Length > 0;
        Loaded += (_, _) =>
        {
            // Ready to type: the name (not the extension) is selected.
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
        };
    }

    void Apply()
    {
        if (!apply.IsEnabled) return;
        try
        {
            var renamed = FileOps.Rename(files, box.Text);
            CloseAnimated();
            ToastWindow.Flash("Renamed", renamed.Count == 1 ? Path.GetFileName(renamed[0]) : $"{renamed.Count} files renamed", at, renamed.FirstOrDefault());
        }
        catch (Exception e)
        {
            note.Text = e is UnauthorizedAccessException ? "Windows won't allow renaming here (the file may be open or protected)." : Converter.Friendly(e);
            note.Foreground = Theme.Brush(Palette.Danger);
            box.Focus();
        }
    }
}

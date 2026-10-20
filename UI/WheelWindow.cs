using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FileWheel.Core;
using FileWheel.Native;

namespace FileWheel.UI;

/// <summary>
/// Hosts the wheel at the pointer. During an Explorer drag it is a native OLE drop target, so the
/// dragged files arrive for real when the user releases over a wedge. It can also be opened with a
/// known selection (tray, "Send to") and driven by mouse or keyboard.
/// </summary>
public sealed class WheelWindow : Window
{
    public delegate void PickedHandler(IReadOnlyList<string> files, WheelMode mode, WheelItem item, POINT at);

    public event PickedHandler? Picked;

    readonly WheelView view = new();
    DropTarget? drop;
    string[] files = [];
    WheelMode mode;
    bool open, interactive, dropped, revealed;
    POINT center;
    Action? back;
    readonly Stack<WheelMode> parents = new(); // the rings an inner ring (Share, Copy / Edit…) was opened from
    DispatcherTimer? dwell; // resting on Share (or on the middle of a sub-ring) during a drag
    int dwellAt = NoDwell;
    const int NoDwell = -3;

    bool CenterGoesBack => parents.Count > 0 || back != null;

    public bool IsOpen => open;
    public WheelMode Mode => mode;

    public WheelWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Width = Height = WheelView.ViewSize;
        Title = "FileWheel";
        Content = view;
        UseLayoutRounding = true;

        SourceInitialized += (_, _) =>
        {
            Win32.MakeToolWindow(this, noActivate: true);
            drop = new DropTarget(new WindowInteropHelper(this).Handle);
            drop.Entered += OnDragEnter;
            drop.Moved += OnDragMove;
            drop.Left += () => Track(-2);
            drop.Dropped += OnDrop;
            drop.HintAt = HintAt;
        };

        MouseMove += (_, e) => { if (interactive) Track(view.HitTest(e.GetPosition(view))); };
        MouseLeave += (_, _) => { if (interactive) Track(-2); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!interactive) return;
            int index = view.HitTest(e.GetPosition(view));
            if (index >= 0) Pick(index);
            else if (index == -1 && parents.Count > 0) LeaveSubRing();
            else if (index == -1 && back != null) GoBack();
            else Dismiss();
        };
        PreviewKeyDown += OnKey;
        Deactivated += (_, _) => { if (interactive && open) Dismiss(); };
    }

    // ---------- Opening ----------

    /// <summary>
    /// Opens centered on <paramref name="at"/>. With no files, waits for the drag to arrive.
    /// With <paramref name="goBack"/>, the middle of the wheel becomes a Back button that runs it.
    /// </summary>
    public void OpenAt(WheelMode wheelMode, POINT at, IReadOnlyList<string>? selection = null, Action? goBack = null)
    {
        if (open)
        {
            SwitchMode(wheelMode);
            return;
        }
        mode = wheelMode;
        back = selection != null ? goBack : null;
        parents.Clear();
        StopDwell();
        interactive = selection != null;
        files = selection?.ToArray() ?? [];
        dropped = false;

        var (work, _, scale) = Backdrop.Screen(at);
        int size = (int)Math.Round(WheelView.ViewSize * scale);
        int x = Math.Clamp(at.X - size / 2, work.Left, Math.Max(work.Left, work.Right - size));
        int y = Math.Clamp(at.Y - size / 2, work.Top, Math.Max(work.Top, work.Bottom - size));
        center = new POINT(x + size / 2, y + size / 2);

        // Capture what's behind the disc before we appear on top of it, and take our colors from it.
        int r = (int)Math.Round(WheelView.DiscR * scale);
        var behind = Backdrop.Capture(new RECT { Left = center.X - r, Top = center.Y - r, Right = center.X + r, Bottom = center.Y + r });
        var palette = Palette.For(behind);
        Theme.Current = palette;
        palette.ApplyTo(Resources);
        view.ApplyPalette(palette);
        view.SetBackdrop(behind);

        view.SetIdleText(IdleText());
        view.SetCenterGlyph(back != null ? "←" : "×");
        view.ResetMotion();
        view.SetItems([], IconStyle, animate: false);
        // During a drag we stay (almost) invisible until real files arrive, so dragging a window
        // or selecting text while pressing Shift never flashes an empty wheel. 1% opacity keeps
        // the window a valid drop target under the pointer.
        revealed = false;
        view.Opacity = 0.01;
        Left = x / scale;
        Top = y / scale;
        Show();
        Win32.MoveTo(this, x, y);
        open = true;

        if (interactive)
        {
            Reveal();
            Win32.ForceForeground(this);
            Activate();
            Focus();
        }
        else
        {
            // Nudge the pointer by a pixel so the drag loop notices the window now under it
            // and hands us the files right away, even if the user holds still.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, NudgePointer);
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            timeout.Tick += (_, _) =>
            {
                timeout.Stop();
                if (open && !revealed) Dismiss();
            };
            timeout.Start();
        }
    }

    void Reveal()
    {
        revealed = true;
        view.Opacity = 1;
        view.AnimateOpen();
        Refresh(animate: true);
    }

    bool IconStyle => mode != WheelMode.Convert;

    public void SwitchMode(WheelMode next)
    {
        if (!open || next == mode) return;
        mode = next;
        parents.Clear();
        StopDwell();
        view.SetCenterGlyph(CenterGoesBack ? "←" : "×");
        if (revealed) Refresh(animate: true);
    }

    /// <summary>
    /// Slices that open another ring in place: Share → the apps, a file's own format → Copy / Rename. In every inner ring the middle goes back one step.
    /// </summary>
    WheelMode? SubRing(WheelItem item) => (mode, item.Id) switch
    {
        (WheelMode.Actions, nameof(FileAction.Share)) => WheelMode.Share,
        (WheelMode.Convert, Formats.SameFormat) => WheelMode.Same,
        (WheelMode.Tools, var id) when Enum.TryParse<Tool>(id, out var tool) && Formats.RingFor(tool) is { } ring => ring,
        _ => null,
    };

    void EnterSubRing(WheelMode next)
    {
        if (!open || mode == next) return;
        parents.Push(mode);
        mode = next;
        view.SetCenterGlyph("←");
        Refresh(animate: true);
    }

    void LeaveSubRing()
    {
        if (!open || parents.Count == 0) return;
        mode = parents.Pop();
        view.SetCenterGlyph(CenterGoesBack ? "←" : "×");
        Refresh(animate: true);
    }

    /// <summary>After a drop the files are known: stay open and let the user click.</summary>
    void BecomeInteractive()
    {
        if (interactive) return;
        interactive = true;
        Win32.ForceForeground(this);
        Activate();
        Focus();
    }

    /// <summary>During a drag, resting on a slice that opens a ring opens it; resting in the middle of an inner ring goes back.</summary>
    void Dwell(int hit)
    {
        var next = hit >= 0 && hit < view.Items.Count ? SubRing(view.Items[hit]) : null;
        bool goesBack = hit == -1 && parents.Count > 0 && view.Items.Count > 0;
        int key = next != null || goesBack ? hit : NoDwell;
        if (key == dwellAt) return;
        StopDwell();
        dwellAt = key;
        if (key == NoDwell) return;
        dwell = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(goesBack ? 500 : 420) };
        dwell.Tick += (_, _) =>
        {
            StopDwell();
            if (goesBack) LeaveSubRing();
            else if (next is { } ring) EnterSubRing(ring);
        };
        dwell.Start();
    }

    void StopDwell()
    {
        dwell?.Stop();
        dwell = null;
        dwellAt = NoDwell;
    }

    void Refresh(bool animate)
    {
        var items = files.Length == 0 ? [] : ItemsFor(mode, files);
        view.SetIdleText(IdleText());
        if (files.Length == 0)
        {
            // Files haven't arrived yet (drag still entering): show the disc without the "nothing" note.
            view.SetItems([], IconStyle, animate);
            return;
        }
        view.SetItems(items, IconStyle, animate);
    }

    static IReadOnlyList<WheelItem> ItemsFor(WheelMode mode, IReadOnlyList<string> files) =>
        mode == WheelMode.Share ? Share.Items(files) : Formats.Items(mode, files);

    string IdleText()
    {
        if (mode == WheelMode.Share) return "Share";
        if (mode is WheelMode.Speed or WheelMode.Channels or WheelMode.Visualize) return mode.ToString();
        if (files.Length == 0) return mode switch { WheelMode.Tools => "Tools", WheelMode.Actions => "Actions", _ => "Convert" };
        if (files.Length > 1) return $"{files.Length} files";
        long size = Formats.TotalSize(files);
        return size < 0 ? "Folder" : Formats.SizeText(size);
    }

    // ---------- Drag & drop ----------

    void OnDragEnter(string[] received, int x, int y)
    {
        if (!open || received.Length == 0) return;
        if (!revealed)
        {
            files = received;
            Reveal();
        }
        else if (!received.SequenceEqual(files))
        {
            files = received;
            Refresh(animate: true);
        }
        Hover(x, y);
    }

    void OnDragMove(string[] received, int x, int y) => Hover(x, y);

    void Hover(int x, int y)
    {
        if (!IsVisible || !revealed) return;
        Track(view.HitTest(view.PointFromScreen(new Point(x, y))));
    }

    /// <summary>Lights the wedge under the pointer, or the middle pill as Cancel/Back.</summary>
    void Track(int hit)
    {
        view.SetHover(Math.Max(-1, hit));
        view.SetCenterHover(hit == -1 && view.Items.Count > 0 ? (CenterGoesBack ? "Back" : "Cancel") : null);
        if (!interactive) Dwell(hit);
    }

    bool OnDrop(string[] received, int x, int y)
    {
        dropped = true;
        if (!open || !revealed) return false;
        files = received;
        var p = view.PointFromScreen(new Point(x, y));
        int index = view.HitTest(p);
        StopDwell();
        if (index >= 0 && index < view.Items.Count)
        {
            // Let the OLE call return before we start work.
            Dispatcher.BeginInvoke(() => Pick(index));
            return true;
        }
        if (index == -1 && parents.Count > 0)
        {
            // "Back" in a sub-ring: return to the previous ring and wait for a click.
            Dispatcher.BeginInvoke(() => { LeaveSubRing(); BecomeInteractive(); });
            return false;
        }
        Dispatcher.BeginInvoke(Dismiss);
        return false;
    }

    DropHint? HintAt(int x, int y)
    {
        if (!IsVisible || !revealed) return null;
        int index = view.HitTest(view.PointFromScreen(new Point(x, y)));
        if (index == -1) return new DropHint("Cancel", "", false);
        if (index < 0 || index >= view.Items.Count) return null;
        var item = view.Items[index];
        return mode switch
        {
            WheelMode.Share => new DropHint("%1", Share.DropCaption(item), true),
            WheelMode.Convert when item.Id == Formats.SameFormat => new DropHint("%1", "Copy or rename", true),
            WheelMode.Speed or WheelMode.Channels or WheelMode.Visualize => new DropHint("%1", item.Hint ?? item.Label, true),
            WheelMode.Same => new DropHint("%1", item.Id == Formats.SameCopy ? (files.Length == 1 ? "Make a copy" : "Make copies") : "Rename", true),
            WheelMode.Convert => new DropHint("Convert to %1", item.Label, true),
            WheelMode.Tools => new DropHint("%1", Formats.ToolTitle(Enum.Parse<Tool>(item.Id)), true),
            _ => new DropHint("%1", ToTitle(item.Label), true),
        };
    }

    static string ToTitle(string label) =>
        System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(label.ToLowerInvariant());

    /// <summary>The mouse button went up somewhere. If it wasn't a drop on us, quietly close.</summary>
    public void OnPointerReleased()
    {
        if (!open || interactive) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (open && !dropped) Dismiss();
        };
        timer.Start();
    }

    // ---------- Keyboard (when opened with a selection) ----------

    void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        int count = view.Items.Count;
        switch (key)
        {
            case Key.Escape:
                Dismiss();
                break;
            case Key.Back when parents.Count > 0:
                LeaveSubRing();
                break;
            case Key.Back when back != null:
                GoBack();
                break;
            case Key.Right or Key.Down when count > 0:
                view.SetHover((view.Hover + 1) % count);
                break;
            case Key.Left or Key.Up when count > 0:
                view.SetHover(view.Hover <= 0 ? count - 1 : view.Hover - 1);
                break;
            case Key.Enter or Key.Space when view.Hover >= 0:
                Pick(view.Hover);
                break;
            case Key.LeftShift or Key.RightShift:
                SwitchMode(WheelMode.Convert);
                break;
            case Key.LeftCtrl or Key.RightCtrl:
                SwitchMode(WheelMode.Tools);
                break;
            case Key.LeftAlt or Key.RightAlt:
                SwitchMode(WheelMode.Actions);
                break;
            case Key.Tab:
                SwitchMode((WheelMode)(((int)mode + 1) % 3));
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ---------- Closing ----------

    void Pick(int index)
    {
        if (!open || index < 0 || index >= view.Items.Count) return;
        var item = view.Items[index];
        if (SubRing(item) is { } inner)
        {
            // Share, a file's own format and Edit open their ring right here instead of closing.
            EnterSubRing(inner);
            BecomeInteractive();
            return;
        }
        var chosenFiles = files.ToArray();
        var chosenMode = mode;
        view.SetHover(index);
        open = false;
        // Hand off only once the wheel is gone, so the toast or panel that follows sees the real
        // desktop behind it (for its glass) rather than the fading wheel.
        back = null;
        view.AnimateClose(() =>
        {
            Hide();
            Theme.After(40, () => Picked?.Invoke(chosenFiles, chosenMode, item, center));
        }, picked: true);
    }

    /// <summary>Closes and returns to the step that opened this wheel.</summary>
    void GoBack()
    {
        if (!open || back is not { } then) return;
        open = false;
        back = null;
        view.AnimateClose(() =>
        {
            Hide();
            Theme.After(40, then);
        });
    }

    public void Dismiss()
    {
        if (!open) return;
        open = false;
        back = null;
        parents.Clear();
        StopDwell();
        if (revealed) view.AnimateClose(Hide);
        else Hide();
    }

    static void NudgePointer()
    {
        var inputs = new INPUT[2];
        inputs[0] = new INPUT { type = 0, mi = new MOUSEINPUT { dx = 1, dy = 0, dwFlags = 0x0001 } };
        inputs[1] = new INPUT { type = 0, mi = new MOUSEINPUT { dx = -1, dy = 0, dwFlags = 0x0001 } };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public MOUSEINPUT mi; }

    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] inputs, int size);

    /// <summary>For snapshot tests: render the wheel for a selection with a hovered wedge.</summary>
    public static WheelView BuildPreview(IReadOnlyList<string> selection, WheelMode wheelMode, int hoverIndex)
    {
        var v = new WheelView();
        long size = Formats.TotalSize(selection);
        v.SetIdleText(selection.Count > 1 ? $"{selection.Count} files" : size < 0 ? "Folder" : Formats.SizeText(size));
        v.SetItems(ItemsFor(wheelMode, selection), wheelMode != WheelMode.Convert, animate: false);
        if (wheelMode is not (WheelMode.Convert or WheelMode.Tools or WheelMode.Actions)) v.SetCenterGlyph("←");
        if (hoverIndex >= 0) v.SetHover(hoverIndex, instant: true);
        return v;
    }
}

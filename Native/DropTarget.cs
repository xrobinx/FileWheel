using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace FileWheel.Native;

[StructLayout(LayoutKind.Sequential)]
public struct POINTL { public int X, Y; }

[ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IOleDropTarget
{
    [PreserveSig] int DragEnter([In, MarshalAs(UnmanagedType.Interface)] IDataObject data, [In] int keys, [In] POINTL pt, [In, Out] ref int effect);
    [PreserveSig] int DragOver([In] int keys, [In] POINTL pt, [In, Out] ref int effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop([In, MarshalAs(UnmanagedType.Interface)] IDataObject data, [In] int keys, [In] POINTL pt, [In, Out] ref int effect);
}

[ComImport, Guid("4657278B-411B-11d2-839A-00C04FD918D0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDropTargetHelper
{
    void DragEnter(IntPtr hwndTarget, [In, MarshalAs(UnmanagedType.Interface)] IDataObject data, ref POINT pt, int effect);
    void DragLeave();
    void DragOver(ref POINT pt, int effect);
    void Drop([In, MarshalAs(UnmanagedType.Interface)] IDataObject data, ref POINT pt, int effect);
    void Show([MarshalAs(UnmanagedType.Bool)] bool show);
}

[ComImport, Guid("4657278A-411B-11d2-839A-00C04FD918D0")]
class DragDropHelper { }

/// <summary>What the drag target tells the source to show beside the cursor (e.g. "Convert to JPG").</summary>
public readonly record struct DropHint(string Message, string Insert, bool Copy);

/// <summary>
/// Native OLE drop target for a window. Receives real file paths from Explorer and keeps the
/// shell's drag image (thumbnail + caption) visible while the pointer is over our window.
/// </summary>
public sealed class DropTarget : IOleDropTarget
{
    public delegate void DragHandler(string[] files, int screenX, int screenY);

    public event DragHandler? Entered;
    public event DragHandler? Moved;
    public event Action? Left;
    /// <summary>Return true to accept the drop.</summary>
    public event Func<string[], int, int, bool>? Dropped;

    /// <summary>Asks the owner what caption to show at a point.</summary>
    public Func<int, int, DropHint?>? HintAt { get; set; }

    readonly IntPtr hwnd;
    IDropTargetHelper? helper;
    IDataObject? current;
    string[] files = [];
    static readonly short DescriptionFormat = (short)RegisterClipboardFormat("DropDescription");

    public DropTarget(IntPtr hwnd)
    {
        this.hwnd = hwnd;
        OleInitialize(IntPtr.Zero);
        RevokeDragDrop(hwnd);
        int hr = RegisterDragDrop(hwnd, this);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        try { helper = (IDropTargetHelper)new DragDropHelper(); } catch { helper = null; }
    }

    public int DragEnter(IDataObject data, int keys, POINTL pt, ref int effect)
    {
        current = data;
        try { files = ReadFiles(data); } catch { files = []; }
        effect = files.Length > 0 ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        var p = new POINT(pt.X, pt.Y);
        try { helper?.DragEnter(hwnd, data, ref p, effect); } catch { }
        Entered?.Invoke(files, pt.X, pt.Y);
        UpdateHint(pt.X, pt.Y);
        return 0;
    }

    public int DragOver(int keys, POINTL pt, ref int effect)
    {
        effect = files.Length > 0 ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        Moved?.Invoke(files, pt.X, pt.Y);
        UpdateHint(pt.X, pt.Y);
        var p = new POINT(pt.X, pt.Y);
        try { helper?.DragOver(ref p, effect); } catch { }
        return 0;
    }

    public int DragLeave()
    {
        ClearHint();
        try { helper?.DragLeave(); } catch { }
        current = null;
        Left?.Invoke();
        return 0;
    }

    public int Drop(IDataObject data, int keys, POINTL pt, ref int effect)
    {
        ClearHint();
        try { files = ReadFiles(data); } catch { files = []; }
        bool accepted = files.Length > 0 && (Dropped?.Invoke(files, pt.X, pt.Y) ?? false);
        // Always report Copy (or None): Explorer then leaves the original exactly where it is.
        effect = accepted ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        var p = new POINT(pt.X, pt.Y);
        try { helper?.Drop(data, ref p, effect); } catch { }
        current = null;
        return 0;
    }

    string? lastHint;

    void UpdateHint(int x, int y)
    {
        if (current == null) return;
        var hint = HintAt?.Invoke(x, y);
        var key = hint is { } h ? h.Message + "|" + h.Insert + "|" + h.Copy : null;
        if (key == lastHint) return;
        lastHint = key;
        if (hint is { } value) SetDescription(current, value.Copy ? DROPIMAGE_COPY : DROPIMAGE_LABEL, value.Message, value.Insert);
        else SetDescription(current, DROPIMAGE_INVALID, "", "");
    }

    void ClearHint()
    {
        if (current != null && lastHint != null) SetDescription(current, DROPIMAGE_INVALID, "", "");
        lastHint = null;
    }

    static void SetDescription(IDataObject data, int type, string message, string insert)
    {
        try
        {
            const int size = 4 + 260 * 2 + 260 * 2;
            var buffer = new byte[size];
            BitConverter.GetBytes(type).CopyTo(buffer, 0);
            Encoding.Unicode.GetBytes(message.Length > 259 ? message[..259] : message).CopyTo(buffer, 4);
            Encoding.Unicode.GetBytes(insert.Length > 259 ? insert[..259] : insert).CopyTo(buffer, 4 + 520);
            var global = GlobalAlloc(0x0042, (UIntPtr)size); // GMEM_MOVEABLE | GMEM_ZEROINIT
            var locked = GlobalLock(global);
            Marshal.Copy(buffer, 0, locked, size);
            GlobalUnlock(global);
            var format = new FORMATETC { cfFormat = DescriptionFormat, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
            var medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = global };
            data.SetData(ref format, ref medium, true);
        }
        catch
        {
            // Some drag sources don't accept descriptions; the wheel itself still shows the choice.
        }
    }

    public static string[] ReadFiles(IDataObject data)
    {
        var format = new FORMATETC { cfFormat = CF_HDROP, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL };
        if (data.QueryGetData(ref format) != 0) return [];
        data.GetData(ref format, out var medium);
        try
        {
            uint count = DragQueryFile(medium.unionmember, uint.MaxValue, null, 0);
            var result = new List<string>();
            for (uint i = 0; i < Math.Min(count, 2000u); i++)
            {
                uint length = DragQueryFile(medium.unionmember, i, null, 0);
                var text = new StringBuilder((int)length + 1);
                DragQueryFile(medium.unionmember, i, text, length + 1);
                var path = text.ToString();
                if (File.Exists(path) || Directory.Exists(path)) result.Add(path);
            }
            return [.. result];
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    public void Revoke() => RevokeDragDrop(hwnd);

    const short CF_HDROP = 15;
    const int DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1;
    const int DROPIMAGE_INVALID = -1, DROPIMAGE_COPY = 1, DROPIMAGE_LABEL = 6;

    [DllImport("ole32.dll")] static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] static extern int RegisterDragDrop(IntPtr hwnd, [MarshalAs(UnmanagedType.Interface)] IOleDropTarget target);
    [DllImport("ole32.dll")] static extern int RevokeDragDrop(IntPtr hwnd);
    [DllImport("ole32.dll")] static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW")] static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? name, uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr handle);
}

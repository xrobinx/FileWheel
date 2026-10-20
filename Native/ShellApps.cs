using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileWheel.Native;

/// <summary>
/// The Start menu's app list (shell:AppsFolder — Store and desktop apps alike), app icons, and
/// the shell's item ids for putting files on the clipboard.
/// </summary>
public static class ShellApps
{
    public sealed record InstalledApp(string Name, string ParsingName);

    static readonly Guid AppsFolder = new("1e87508d-89c2-42f0-8a7e-645a0f50ca58");
    static readonly Guid BhidEnumItems = new("94f60519-2850-4924-aa5a-d15e84868039");
    static readonly Guid IidShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    static readonly Guid IidEnumShellItems = new("70629033-e363-4a28-a567-0db78006e6d7");
    static readonly Guid IidImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    /// <summary>Every app in the Start menu's "All apps" list. Call from an STA thread.</summary>
    public static List<InstalledApp> List()
    {
        var apps = new List<InstalledApp>();
        var folderId = AppsFolder;
        var iidItem = IidShellItem;
        if (SHGetKnownFolderItem(ref folderId, 0, IntPtr.Zero, ref iidItem, out var folder) != 0 || folder == null) return apps;
        var bhid = BhidEnumItems;
        var iidEnum = IidEnumShellItems;
        folder.BindToHandler(IntPtr.Zero, ref bhid, ref iidEnum, out var enumerator);
        while (enumerator.Next(1, out var item, out var fetched) == 0 && fetched == 1)
        {
            try
            {
                var name = DisplayName(item, 0); // SIGDN_NORMALDISPLAY
                var parsing = DisplayName(item, unchecked((int)0x80018001)); // SIGDN_PARENTRELATIVEPARSING (the app's id)
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(parsing)) apps.Add(new InstalledApp(name, parsing));
            }
            catch { }
            finally { Marshal.ReleaseComObject(item); }
        }
        Marshal.ReleaseComObject(enumerator);
        Marshal.ReleaseComObject(folder);
        return apps;
    }

    static string DisplayName(IShellItem item, int sigdn)
    {
        item.GetDisplayName(sigdn, out var ptr);
        try { return Marshal.PtrToStringUni(ptr) ?? ""; }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    /// <summary>The app's own icon at <paramref name="size"/> px, with transparency; frozen so any thread can use it.</summary>
    public static BitmapSource? Icon(string parsingName, int size)
    {
        var iid = IidImageFactory;
        if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + parsingName, IntPtr.Zero, ref iid, out var unknown) != 0 || unknown == null) return null;
        var factory = (IShellItemImageFactory)unknown;
        IntPtr bitmap = IntPtr.Zero;
        try
        {
            // SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK
            if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x4 | 0x1, out bitmap) != 0 || bitmap == IntPtr.Zero) return null;
            return FromHBitmap(bitmap);
        }
        catch { return null; }
        finally
        {
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            Marshal.ReleaseComObject(unknown);
        }
    }

    /// <summary>Reads a 32-bit (premultiplied alpha) HBITMAP top-down into a WPF bitmap.</summary>
    static BitmapSource? FromHBitmap(IntPtr hbitmap)
    {
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), out var info) == 0 || info.bmWidth <= 0 || info.bmHeight == 0) return null;
        int w = info.bmWidth, h = Math.Abs(info.bmHeight);
        var header = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        var pixels = new byte[w * h * 4];
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbitmap, 0, (uint)h, pixels, ref header, 0) == 0) return null;
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        // Some icons come back with no alpha at all; treat those as opaque.
        bool anyAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4) if (pixels[i] != 0) { anyAlpha = true; break; }
        if (!anyAlpha) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var source = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, w * 4);
        source.Freeze();
        return source;
    }

    /// <summary>Starts an app from the Start menu list.</summary>
    public static void Launch(string parsingName) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", @"shell:AppsFolder\" + parsingName) { UseShellExecute = true });

    /// <summary>
    /// The files as a "Shell IDList Array" (what Explorer's Copy puts on the clipboard next to the plain
    /// file list): the desktop as parent, then each file's full item id list.
    /// </summary>
    public static byte[]? ItemIdList(IReadOnlyList<string> paths)
    {
        var items = new List<byte[]>();
        foreach (var path in paths)
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return null;
            try
            {
                var bytes = new byte[ILGetSize(pidl)];
                Marshal.Copy(pidl, bytes, 0, bytes.Length);
                items.Add(bytes);
            }
            finally { Marshal.FreeCoTaskMem(pidl); }
        }
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((uint)items.Count);
        uint offset = (uint)(4 + 4 * (items.Count + 1));
        writer.Write(offset); // the parent: the desktop, an empty id list
        offset += 2;
        foreach (var item in items)
        {
            writer.Write(offset);
            offset += (uint)item.Length;
        }
        writer.Write((ushort)0);
        foreach (var item in items) writer.Write(item);
        writer.Flush();
        return stream.ToArray();
    }

    // ---------- Interop ----------

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IEnumShellItems ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(int sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IEnumShellItems
    {
        [PreserveSig] int Next(uint celt, out IShellItem rgelt, out uint pceltFetched);
        void Skip(uint celt);
        void Reset();
        void Clone(out IEnumShellItems ppenum);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("shell32.dll")] static extern int SHGetKnownFolderItem(ref Guid rfid, uint flags, IntPtr token, ref Guid riid, out IShellItem ppv);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHParseDisplayName(string name, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);
    [DllImport("shell32.dll")] static extern uint ILGetSize(IntPtr pidl);
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP bitmap);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER info, uint usage);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
}

using System.Runtime.InteropServices;
using System.Windows.Media;

namespace FileWheel.Native;

/// <summary>Windows' standard color dialog (the full one, with the custom-color picker open).</summary>
public static class ColorPicker
{
    static readonly int[] customColors = new int[16];

    public static Color? Pick(IntPtr owner, Color initial)
    {
        var custom = GCHandle.Alloc(customColors, GCHandleType.Pinned);
        try
        {
            var data = new CHOOSECOLOR
            {
                lStructSize = Marshal.SizeOf<CHOOSECOLOR>(),
                hwndOwner = owner,
                rgbResult = initial.R | initial.G << 8 | initial.B << 16,
                lpCustColors = custom.AddrOfPinnedObject(),
                Flags = 0x1 | 0x2, // CC_RGBINIT | CC_FULLOPEN
            };
            if (!ChooseColor(ref data)) return null;
            int c = data.rgbResult;
            return Color.FromRgb((byte)(c & 0xFF), (byte)(c >> 8 & 0xFF), (byte)(c >> 16 & 0xFF));
        }
        finally { custom.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CHOOSECOLOR
    {
        public int lStructSize;
        public IntPtr hwndOwner, hInstance;
        public int rgbResult;
        public IntPtr lpCustColors;
        public int Flags;
        public IntPtr lCustData, lpfnHook, lpTemplateName;
    }

    [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW")] static extern bool ChooseColor(ref CHOOSECOLOR data);
}

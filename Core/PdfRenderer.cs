using System.Runtime.InteropServices;
using ImageMagick;

namespace FileWheel.Core;

/// <summary>Renders PDF pages with PDFium. PDFium is not thread-safe, so every call is serialized.</summary>
public static class PdfRenderer
{
    static readonly object gate = new();
    static bool initialized;

    static void Init()
    {
        if (initialized) return;
        FPDF_InitLibrary();
        initialized = true;
    }

    public static int PageCount(string file)
    {
        lock (gate)
        {
            Init();
            var doc = Open(file);
            try { return FPDF_GetPageCount(doc); }
            finally { FPDF_CloseDocument(doc); }
        }
    }

    static IntPtr Open(string file)
    {
        var doc = FPDF_LoadDocument(file, null);
        if (doc == IntPtr.Zero) throw new IOException("This PDF can't be opened. It may be password-protected or damaged.");
        return doc;
    }

    /// <summary>Renders a page at <paramref name="dpi"/>, scaled down if it would exceed <paramref name="maxPixels"/>.</summary>
    public static MagickImage Render(string file, int index, double dpi, long maxPixels = 40_000_000)
    {
        lock (gate)
        {
            Init();
            var doc = Open(file);
            try
            {
                if (index < 0 || index >= FPDF_GetPageCount(doc)) throw new IOException("That page isn't in the document.");
                var page = FPDF_LoadPage(doc, index);
                if (page == IntPtr.Zero) throw new IOException("This PDF page could not be rendered.");
                try
                {
                    double pw = FPDF_GetPageWidth(page), ph = FPDF_GetPageHeight(page);
                    double scale = dpi / 72.0;
                    if (pw * ph * scale * scale > maxPixels) scale = Math.Sqrt(maxPixels / (pw * ph));
                    int w = Math.Max(1, (int)Math.Round(pw * scale)), h = Math.Max(1, (int)Math.Round(ph * scale));
                    var bitmap = FPDFBitmap_Create(w, h, 0);
                    if (bitmap == IntPtr.Zero) throw new IOException("Not enough memory to render this page.");
                    try
                    {
                        FPDFBitmap_FillRect(bitmap, 0, 0, w, h, 0xffffffff);
                        FPDF_RenderPageBitmap(bitmap, page, 0, 0, w, h, 0, 0x01 | 0x800); // annotations + print quality
                        int stride = FPDFBitmap_GetStride(bitmap);
                        var raw = new byte[stride * h];
                        Marshal.Copy(FPDFBitmap_GetBuffer(bitmap), raw, 0, raw.Length);
                        var pixels = raw;
                        if (stride != w * 4)
                        {
                            pixels = new byte[w * h * 4];
                            for (int y = 0; y < h; y++) Buffer.BlockCopy(raw, y * stride, pixels, y * w * 4, w * 4);
                        }
                        var image = new MagickImage();
                        image.ReadPixels(pixels, new PixelReadSettings((uint)w, (uint)h, StorageType.Char, PixelMapping.BGRA));
                        image.Alpha(AlphaAction.Off);
                        image.Density = new Density(scale * 72, scale * 72, DensityUnit.PixelsPerInch);
                        return image;
                    }
                    finally { FPDFBitmap_Destroy(bitmap); }
                }
                finally { FPDF_ClosePage(page); }
            }
            finally { FPDF_CloseDocument(doc); }
        }
    }

    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] static extern void FPDF_InitLibrary();
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr FPDF_LoadDocument([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPStr)] string? password);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] static extern void FPDF_ClosePage(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] static extern double FPDF_GetPageWidth(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] static extern double FPDF_GetPageHeight(IntPtr page);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr FPDFBitmap_Create(int width, int height, int alpha);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] static extern void FPDFBitmap_Destroy(IntPtr bitmap);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] static extern void FPDFBitmap_FillRect(IntPtr bitmap, int x, int y, int width, int height, uint color);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] static extern void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotation, int flags);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [DllImport("pdfium", CallingConvention = CallingConvention.Cdecl)] static extern int FPDFBitmap_GetStride(IntPtr bitmap);
}

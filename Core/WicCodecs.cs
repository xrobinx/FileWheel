using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FileWheel.Core;

/// <summary>Uses Windows' own imaging codecs (HEIF Image Extensions) where Magick has no encoder.</summary>
public static class WicCodecs
{
    static readonly Guid HeifContainer = new("E1E62521-6787-405B-A339-500715B5763F");

    public static bool CanEncodeHeif()
    {
        try
        {
            var pixels = new byte[16 * 16 * 4];
            var source = BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
            using var ms = new MemoryStream();
            EncodeHeif(source, ms);
            return ms.Length > 0;
        }
        catch { return false; }
    }

    public static void EncodeHeif(BitmapSource source, Stream output)
    {
        var encoder = BitmapEncoder.Create(HeifContainer);
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(output);
    }
}

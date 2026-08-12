using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace CaravanCMS.Admin.Services;

/// <summary>
/// Resizes images client-side before upload, matching the Worker's own resize parameters exactly
/// (2000px longest edge, 85% JPEG). This exists because @cf-wasm/photon (the Worker-side resize
/// library) has an unresolved WASM lifecycle bug that intermittently fails resize calls in
/// production — see src/lib/imageResize.ts in CaravanCMS.Worker for the full writeup. Doing the
/// resize here instead, using .NET's well-tested System.Drawing, avoids that entirely for the
/// dominant upload path (the document sync service) rather than relying on the Worker's
/// upload-original-unresized fallback and eating into R2's free-tier storage.
/// </summary>
public static class ImageResizeService
{
    private const int MaxDimension = 2000;
    private const long JpegQuality = 85L;

    private static readonly HashSet<string> ResizableExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };

    public static bool IsResizable(string filePath) => ResizableExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>
    /// Returns resized JPEG bytes and the filename to use (extension normalized to .jpg to match
    /// the actual re-encoded format, same convention the Worker uses). Images already smaller than
    /// MaxDimension are only re-encoded as JPEG, never upscaled.
    /// </summary>
    public static (byte[] Bytes, string FileName) Resize(string filePath)
    {
        using Bitmap original = new(filePath);
        double scale = Math.Min(1.0, (double)MaxDimension / Math.Max(original.Width, original.Height));

        int newWidth = (int)Math.Round(original.Width * scale);
        int newHeight = (int)Math.Round(original.Height * scale);

        using Bitmap target = new(newWidth, newHeight);
        using (Graphics g = Graphics.FromImage(target))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(original, 0, 0, newWidth, newHeight);
        }

        ImageCodecInfo jpegEncoder = ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        EncoderParameters encoderParams = new(1);
        encoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);

        using MemoryStream ms = new();
        target.Save(ms, jpegEncoder, encoderParams);

        string fileName = Path.GetFileNameWithoutExtension(filePath) + ".jpg";
        return (ms.ToArray(), fileName);
    }
}

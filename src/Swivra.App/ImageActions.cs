using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace Swivra.App;

/// <summary>
/// Image encoders that GDI+ cannot do (WebP, PDF) and the offline background remover.
/// </summary>
internal static class ImageActions
{
    private const int WebpQuality = 82;

    // A4 in PDF points; pages keep the image aspect but never exceed A4 on the long side.
    private const double A4Long = 842, A4Short = 595;

    /// <summary>Encodes the bitmap as WebP (lossy, alpha kept) via SkiaSharp.</summary>
    public static void SaveWebp(Bitmap bmp, string target)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        using SKBitmap sk = SKBitmap.Decode(ms.ToArray());
        using SKImage img = SKImage.FromBitmap(sk);
        using SKData data = img.Encode(SKEncodedImageFormat.Webp, WebpQuality);
        using var fs = File.Create(target);
        data.SaveTo(fs);
    }

    /// <summary>
    /// One-page PDF. The page has exactly the image's aspect (no white bars) and is scaled to fit
    /// inside A4 in the matching orientation, so it prints at a sensible size whatever the pixel count.
    /// Photos are embedded as JPEG (q92) to keep the PDF small; images with transparency stay PNG.
    /// </summary>
    public static void SavePdf(Bitmap bmp, string target)
    {
        using var ms = new MemoryStream();
        if (HasTransparency(bmp)) bmp.Save(ms, ImageFormat.Png);
        else
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ps = new EncoderParameters(1);
            ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 92L);
            bmp.Save(ms, codec, ps);
        }
        ms.Position = 0;

        bool landscape = bmp.Width > bmp.Height;
        double maxW = landscape ? A4Long : A4Short, maxH = landscape ? A4Short : A4Long;
        double scale = Math.Min(maxW / bmp.Width, maxH / bmp.Height);
        double pw = bmp.Width * scale, ph = bmp.Height * scale;

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(pw);
        page.Height = XUnit.FromPoint(ph);
        using (var gfx = XGraphics.FromPdfPage(page))
        using (var xi = XImage.FromStream(ms))
            gfx.DrawImage(xi, 0, 0, pw, ph);
        doc.Save(target);
    }

    /// <summary>True when at least one pixel is not fully opaque. Scans the alpha bytes (early exit), no GetPixel.</summary>
    public static bool HasTransparency(Bitmap bmp)
    {
        var fmt = bmp.PixelFormat;
        if ((fmt & PixelFormat.Indexed) != 0) return (bmp.Palette.Flags & (int)PaletteFlags.HasAlpha) != 0; // GIF with a transparent entry
        if (!Image.IsAlphaPixelFormat(fmt)) return false;
        // Other alpha layouts (64bpp keeps alpha as 0..8192, 16bpp ARGB1555) are rare here: be safe and say yes.
        if (fmt is not (PixelFormat.Format32bppArgb or PixelFormat.Format32bppPArgb)) return true;
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, fmt);
        try
        {
            byte[] row = new byte[data.Width * 4]; // one row at a time keeps 48 MP photos cheap
            for (int y = 0; y < data.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                for (int x = 3; x < row.Length; x += 4) // BGRA: alpha is the fourth byte
                    if (row[x] != 255) return true;
            }
            return false;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>
    /// Copy of the bitmap drawn onto an opaque white 24bpp canvas of the same size (for formats without alpha).
    /// The metadata items come along, so flattening never decides on its own what EXIF the output keeps.
    /// </summary>
    public static Bitmap FlattenOnWhite(Bitmap bmp)
    {
        var flat = new Bitmap(bmp.Width, bmp.Height, PixelFormat.Format24bppRgb);
        flat.SetResolution(bmp.HorizontalResolution, bmp.VerticalResolution);
        using (var g = Graphics.FromImage(flat))
        {
            g.Clear(Color.White);
            g.DrawImage(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel);
        }
        foreach (var p in bmp.PropertyItems)
            try { flat.SetPropertyItem(p); } catch { /* some tags are not writable */ }
        return flat;
    }

    // ---------- background removal (u2netp via ONNX Runtime, CPU) ----------

    private const int ModelSize = 320;
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };
    private static readonly Lazy<InferenceSession> Session = new(() =>
    {
        string model = Path.Combine(AppContext.BaseDirectory, "models", "u2netp.onnx");
        using var opts = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
        return new InferenceSession(model, opts);
    }, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly object RunLock = new();

    /// <summary>Returns a new 32bpp ARGB bitmap whose alpha is the predicted subject mask.</summary>
    public static Bitmap RemoveBackground(Bitmap src)
    {
        // Model input: 320x320, scaled by the image max, then mean/std normalised, planar RGB (as rembg does).
        float[] input = new float[3 * ModelSize * ModelSize];
        using (var small = new Bitmap(ModelSize, ModelSize, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                using var wrap = new ImageAttributes();
                wrap.SetWrapMode(WrapMode.TileFlipXY);
                g.DrawImage(src, new Rectangle(0, 0, ModelSize, ModelSize), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, wrap);
            }
            var data = small.LockBits(new Rectangle(0, 0, ModelSize, ModelSize), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte[] px = new byte[data.Stride * ModelSize];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
                byte max = 1;
                for (int y = 0; y < ModelSize; y++)
                    for (int x = 0; x < ModelSize * 3; x++) max = Math.Max(max, px[y * data.Stride + x]);
                int plane = ModelSize * ModelSize;
                for (int y = 0; y < ModelSize; y++)
                    for (int x = 0; x < ModelSize; x++)
                    {
                        int o = y * data.Stride + x * 3; // BGR
                        for (int c = 0; c < 3; c++)
                            input[c * plane + y * ModelSize + x] = (px[o + 2 - c] / (float)max - Mean[c]) / Std[c];
                    }
            }
            finally { small.UnlockBits(data); }
        }

        float[] mask = new float[ModelSize * ModelSize];
        var session = Session.Value;
        using (var tensorValue = OrtValue.CreateTensorValueFromMemory(input, new long[] { 1, 3, ModelSize, ModelSize }))
        {
            var inputs = new Dictionary<string, OrtValue> { [session.InputNames[0]] = tensorValue };
            lock (RunLock)
            {
                using var runOptions = new RunOptions();
                using var outputs = session.Run(runOptions, inputs, new[] { session.OutputNames[0] });
                outputs[0].GetTensorDataAsSpan<float>().Slice(0, mask.Length).CopyTo(mask);
            }
        }

        // min-max normalise the mask
        float lo = mask.Min(), hi = mask.Max(), range = Math.Max(hi - lo, 1e-6f);
        for (int i = 0; i < mask.Length; i++) mask[i] = (mask[i] - lo) / range;

        // Upscale the mask bilinearly to the original size and use it as alpha.
        int w = src.Width, h = src.Height;
        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var sd = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var rd = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] s = new byte[sd.Stride * h];
            byte[] r = new byte[rd.Stride * h];
            System.Runtime.InteropServices.Marshal.Copy(sd.Scan0, s, 0, s.Length);
            for (int y = 0; y < h; y++)
            {
                float fy = Math.Clamp((y + 0.5f) * ModelSize / h - 0.5f, 0, ModelSize - 1);
                int y0 = (int)fy, y1 = Math.Min(y0 + 1, ModelSize - 1);
                float ty = fy - y0;
                for (int x = 0; x < w; x++)
                {
                    float fx = Math.Clamp((x + 0.5f) * ModelSize / w - 0.5f, 0, ModelSize - 1);
                    int x0 = (int)fx, x1 = Math.Min(x0 + 1, ModelSize - 1);
                    float tx = fx - x0;
                    float top = mask[y0 * ModelSize + x0] * (1 - tx) + mask[y0 * ModelSize + x1] * tx;
                    float bot = mask[y1 * ModelSize + x0] * (1 - tx) + mask[y1 * ModelSize + x1] * tx;
                    float m = top * (1 - ty) + bot * ty;
                    int so = y * sd.Stride + x * 4, ro = y * rd.Stride + x * 4;
                    byte a0 = s[so + 3];
                    r[ro] = s[so]; r[ro + 1] = s[so + 1]; r[ro + 2] = s[so + 2];
                    r[ro + 3] = (byte)Math.Round(m * a0); // keep any alpha the source already had
                }
            }
            System.Runtime.InteropServices.Marshal.Copy(r, 0, rd.Scan0, r.Length);
        }
        finally { src.UnlockBits(sd); result.UnlockBits(rd); }
        return result;
    }
}

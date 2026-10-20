using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Swivra.App;

/// <summary>Raised when a HEIC/AVIF file cannot be decoded, most likely because the Windows codec is not installed.</summary>
internal sealed class MissingCodecException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Reads every image Swivra accepts. GDI+ first (JPG, PNG, BMP, GIF, TIFF; keeps EXIF as property items),
/// then SkiaSharp for WebP, then the Windows Imaging Component for HEIC/HEIF/AVIF. WIC decodes those
/// only when the free HEIF (and HEVC or AV1) extensions from the Microsoft Store are installed.
/// </summary>
internal static class ImageIO
{
    /// <summary>
    /// Decodes upright (orientation applied). <paramref name="props"/> are the GDI+ EXIF items; formats
    /// GDI+ cannot read return none, so their output carries no metadata at all, location included.
    /// </summary>
    public static Bitmap Load(string path, out PropertyItem[] props)
    {
        byte[] data = File.ReadAllBytes(path); // in memory, so the file is not locked while we work
        try
        {
            using var ms = new MemoryStream(data);
            using var img = Image.FromStream(ms);
            props = img.PropertyItems;
            var bmp = new Bitmap(img);
            var o = props.FirstOrDefault(p => p.Id == 0x0112);
            if (o?.Value is { Length: >= 2 } v) Orient(bmp, BitConverter.ToUInt16(v, 0));
            return bmp;
        }
        catch (Exception e) when (IsGdiRefusal(e))
        {
            // Not a format GDI+ knows; fall through to the other decoders.
        }

        props = [];
        Exception? skiaError = null;
        try
        {
            if (LoadSkia(data) is Bitmap b) return b;
        }
        catch (Exception ex)
        {
            skiaError = ex;
        }
        try
        {
            return Wic.Load(path);
        }
        catch (Exception ex)
        {
            Log.Write($"decode failed {path}: skia={skiaError?.Message ?? "no codec"} wic={ex.Message}");
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".heic" or ".heif")
                throw new MissingCodecException(Lang.T("HEIC için Microsoft Store'dan HEIF ve HEVC uzantıları gerekebilir", "HEIC may need the HEIF and HEVC extensions from the Microsoft Store"), ex);
            if (ext == ".avif")
                throw new MissingCodecException(Lang.T("AVIF için Microsoft Store'dan HEIF ve AV1 uzantıları gerekebilir", "AVIF may need the HEIF and AV1 extensions from the Microsoft Store"), ex);
            throw;
        }
    }

    /// <summary>GDI+ reports an unknown or broken format as ArgumentException, sometimes as OutOfMemory or a generic error.</summary>
    public static bool IsGdiRefusal(Exception e) => e is ArgumentException or OutOfMemoryException or ExternalException;

    /// <summary>Location and camera model for the radar, from formats GDI+ cannot read. Metadata only, no pixels.</summary>
    public static (bool Gps, string? Model) ReadPrivacy(string path) => Wic.ReadPrivacy(path);

    /// <summary>Applies an EXIF orientation value (1-8) to the pixels.</summary>
    public static void Orient(Bitmap bmp, int orientation)
    {
        RotateFlipType? flip = orientation switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.RotateNoneFlipY,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => null,
        };
        if (flip is not null) bmp.RotateFlip(flip.Value);
    }

    /// <summary>WebP (and anything else this Skia build reads). Null when Skia has no codec for the data.</summary>
    private static Bitmap? LoadSkia(byte[] data)
    {
        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData);
        if (codec is null) return null;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var bmp = new Bitmap(info.Width, info.Height, PixelFormat.Format32bppArgb);
        try
        {
            var bits = bmp.LockBits(new Rectangle(0, 0, info.Width, info.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            SKCodecResult result;
            try
            {
                result = codec.GetPixels(info, bits.Scan0, bits.Stride, new SKCodecOptions());
            }
            finally
            {
                bmp.UnlockBits(bits);
            }
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) throw new InvalidOperationException($"skia {result}");
            Orient(bmp, (int)codec.EncodedOrigin); // SKEncodedOrigin uses the EXIF numbering
            return bmp;
        }
        catch
        {
            bmp.Dispose(); // a 48 MP photo is ~190 MB; do not leave it to the finalizer
            throw;
        }
    }

    /// <summary>Minimal Windows Imaging Component interop: decode the first frame to 32-bit BGRA, read two metadata names.</summary>
    private static class Wic
    {
        private static readonly Guid ClsidFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
        private static readonly Guid PixelFormatBgra = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");
        private const uint GenericRead = 0x80000000;
        private const int CacheOnLoad = 1;

        public static Bitmap Load(string path)
        {
            var factory = CreateFactory();
            IWICBitmapDecoder? decoder = null;
            IWICBitmapFrameDecode? frame = null;
            IWICFormatConverter? converter = null;
            try
            {
                Check(factory.CreateDecoderFromFilename(path, IntPtr.Zero, GenericRead, CacheOnLoad, out decoder));
                Check(decoder.GetFrame(0, out frame));
                Check(factory.CreateFormatConverter(out converter));
                Guid bgra = PixelFormatBgra;
                Check(converter.Initialize(frame, ref bgra, 0, IntPtr.Zero, 0, 0));
                Check(converter.GetSize(out uint w, out uint h));

                // No EXIF rotation here: the HEIF decoder already applies the container's rotation
                // (irot/imir), and iPhones also keep Orientation=6 in EXIF, so applying it would turn the photo twice.
                var bmp = new Bitmap((int)w, (int)h, PixelFormat.Format32bppArgb);
                try
                {
                    var bits = bmp.LockBits(new Rectangle(0, 0, (int)w, (int)h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        Check(converter.CopyPixels(IntPtr.Zero, (uint)bits.Stride, (uint)bits.Stride * h, bits.Scan0));
                    }
                    finally
                    {
                        bmp.UnlockBits(bits);
                    }
                    return bmp;
                }
                catch
                {
                    bmp.Dispose();
                    throw;
                }
            }
            finally
            {
                Release(converter);
                Release(frame);
                Release(decoder);
                Release(factory);
            }
        }

        public static (bool Gps, string? Model) ReadPrivacy(string path)
        {
            var factory = CreateFactory();
            IWICBitmapDecoder? decoder = null;
            IWICBitmapFrameDecode? frame = null;
            IWICMetadataQueryReader? reader = null;
            try
            {
                Check(factory.CreateDecoderFromFilename(path, IntPtr.Zero, GenericRead, CacheOnLoad, out decoder));
                Check(decoder.GetFrame(0, out frame));
                if (frame.GetMetadataQueryReader(out reader) < 0 || reader is null) return (false, null);
                // The rational-array names can come back empty even when the GPS block is there; the Ref tags
                // (N/S, E/W) always sit next to the coordinates, so any of them means "has a location".
                bool gps = new[] { "System.GPS.Latitude", "System.GPS.Longitude", "System.GPS.LatitudeRef", "System.GPS.LongitudeRef" }
                    .Any(n => HasValue(reader, n));
                return (gps, ReadString(reader, "System.Photo.CameraModel"));
            }
            finally
            {
                Release(reader);
                Release(frame);
                Release(decoder);
                Release(factory);
            }
        }

        private static bool HasValue(IWICMetadataQueryReader reader, string name)
        {
            var v = new PropVariant();
            try
            {
                return reader.GetMetadataByName(name, ref v) == 0 && v.vt != 0;
            }
            finally
            {
                PropVariantClear(ref v);
            }
        }

        private static string? ReadString(IWICMetadataQueryReader reader, string name)
        {
            const ushort VtLpwstr = 31, VtLpstr = 30;
            var v = new PropVariant();
            try
            {
                if (reader.GetMetadataByName(name, ref v) != 0) return null;
                string? s = v.vt switch
                {
                    VtLpwstr => Marshal.PtrToStringUni(v.ptr),
                    VtLpstr => Marshal.PtrToStringAnsi(v.ptr),
                    _ => null,
                };
                s = s?.Trim('\0', ' ');
                return string.IsNullOrEmpty(s) ? null : s;
            }
            finally
            {
                PropVariantClear(ref v);
            }
        }

        private static IWICImagingFactory CreateFactory() =>
            (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidFactory, throwOnError: true)!)!;

        private static void Check(int hr)
        {
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }

        private static void Release(object? com)
        {
            if (com is not null) Marshal.ReleaseComObject(com);
        }

        [DllImport("ole32.dll")]
        private static extern int PropVariantClear(ref PropVariant pvar);

        [StructLayout(LayoutKind.Explicit, Size = 24)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr ptr;
        }

        // Vtable order matters: every method up to the last one we call is declared, in the SDK's order.
        [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICImagingFactory
        {
            [PreserveSig] int CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr vendor, uint access, int options, out IWICBitmapDecoder decoder);
            [PreserveSig] int CreateDecoderFromStream();
            [PreserveSig] int CreateDecoderFromFileHandle();
            [PreserveSig] int CreateComponentInfo();
            [PreserveSig] int CreateDecoder();
            [PreserveSig] int CreateEncoder();
            [PreserveSig] int CreatePalette();
            [PreserveSig] int CreateFormatConverter(out IWICFormatConverter converter);
        }

        [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICBitmapDecoder
        {
            [PreserveSig] int QueryCapability();
            [PreserveSig] int Initialize();
            [PreserveSig] int GetContainerFormat(out Guid format);
            [PreserveSig] int GetDecoderInfo();
            [PreserveSig] int CopyPalette();
            [PreserveSig] int GetMetadataQueryReader();
            [PreserveSig] int GetPreview();
            [PreserveSig] int GetColorContexts();
            [PreserveSig] int GetThumbnail();
            [PreserveSig] int GetFrameCount(out uint count);
            [PreserveSig] int GetFrame(uint index, out IWICBitmapFrameDecode frame);
        }

        // IWICBitmapSource methods first, then IWICBitmapFrameDecode's own.
        [ComImport, Guid("3b16811b-6a43-4ec9-a813-3d930c13b940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICBitmapFrameDecode
        {
            [PreserveSig] int GetSize(out uint width, out uint height);
            [PreserveSig] int GetPixelFormat(out Guid format);
            [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
            [PreserveSig] int CopyPalette(IntPtr palette);
            [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint size, IntPtr buffer);
            [PreserveSig] int GetMetadataQueryReader(out IWICMetadataQueryReader reader);
        }

        // IWICBitmapSource methods first, then IWICFormatConverter's own.
        [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICFormatConverter
        {
            [PreserveSig] int GetSize(out uint width, out uint height);
            [PreserveSig] int GetPixelFormat(out Guid format);
            [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
            [PreserveSig] int CopyPalette(IntPtr palette);
            [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint size, IntPtr buffer);
            [PreserveSig] int Initialize(IWICBitmapFrameDecode source, ref Guid dstFormat, int dither, IntPtr palette, double alphaThreshold, int paletteType);
        }

        [ComImport, Guid("30989668-e1c9-4597-b395-458eedb808df"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICMetadataQueryReader
        {
            [PreserveSig] int GetContainerFormat(out Guid format);
            [PreserveSig] int GetLocation();
            [PreserveSig] int GetMetadataByName([MarshalAs(UnmanagedType.LPWStr)] string name, ref PropVariant value);
        }
    }
}

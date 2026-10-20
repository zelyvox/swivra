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
/// Pixels come back in sRGB whenever the file embeds a colour profile: phones embed Display P3, and every
/// output path (JPG/PNG without metadata, WebP, PDF) would otherwise show those numbers as sRGB, duller and
/// shifted. Files without a profile are taken as sRGB as they are (PNG gAMA/cHRM alone is not applied).
/// </summary>
internal static class ImageIO
{
    private const int IccTag = 0x8773;

    /// <summary>
    /// Decodes upright (orientation applied) and in sRGB. <paramref name="props"/> are the GDI+ EXIF items;
    /// formats GDI+ cannot read return none, so their output carries no metadata at all, location included.
    /// </summary>
    public static Bitmap Load(string path, out PropertyItem[] props)
    {
        byte[] data = File.ReadAllBytes(path); // in memory, so the file is not locked while we work
        try
        {
            using var ms = new MemoryStream(data);
            using var img = Image.FromStream(ms);
            props = img.PropertyItems;
            // GDI+ ignores an embedded profile. Skia applies it; TIFF has no Skia codec, so WIC does it there.
            if (props.Any(p => p.Id == IccTag))
            {
                try
                {
                    Bitmap? srgb = LoadSkia(data);
                    if (srgb is null)
                    {
                        srgb = Wic.Load(path, out bool converted);
                        if (!converted)
                        {
                            srgb.Dispose();
                            srgb = null;
                        }
                        else
                        {
                            // Unlike the HEIF decoder, WIC's TIFF decoder leaves the EXIF orientation to us.
                            var to = props.FirstOrDefault(p => p.Id == 0x0112);
                            if (to?.Value is { Length: >= 2 } tv) Orient(srgb, BitConverter.ToUInt16(tv, 0));
                        }
                    }
                    if (srgb is not null)
                    {
                        props = AsSrgb(props);
                        return srgb;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"colour conversion failed {path}: {ex.Message}");
                }
            }
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
            return Wic.Load(path, out _);
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

    /// <summary>
    /// Metadata for pixels that were converted to sRGB: the old profile would recolour them, and EXIF ColorSpace
    /// (iPhones write 0xFFFF, "uncalibrated") now really is 1 = sRGB.
    /// </summary>
    private static PropertyItem[] AsSrgb(PropertyItem[] props)
    {
        var kept = props.Where(p => p.Id != IccTag).ToArray();
        foreach (var p in kept.Where(p => p.Id == 0xA001))
        {
            p.Type = 3; // SHORT
            p.Len = 2;
            p.Value = BitConverter.GetBytes((ushort)1);
        }
        return kept;
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

    /// <summary>WebP (and anything else this Skia build reads), converted to sRGB. Null when Skia has no codec for the data.</summary>
    private static Bitmap? LoadSkia(byte[] data)
    {
        using var skData = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(skData);
        if (codec is null) return null;
        using var srgb = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul, srgb);
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

    /// <summary>Minimal Windows Imaging Component interop: decode the first frame to 32-bit BGRA in sRGB, read two metadata names.</summary>
    private static class Wic
    {
        private static readonly Guid ClsidFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
        private static readonly Guid PixelFormatBgra = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");
        private const uint GenericRead = 0x80000000;
        private const int CacheOnLoad = 1;
        private const uint ExifSrgb = 1;
        private const int ContextProfile = 1; // WICColorContextProfile

        /// <param name="converted">True when a colour transform to sRGB was applied.</param>
        public static Bitmap Load(string path, out bool converted)
        {
            var factory = CreateFactory();
            IWICBitmapDecoder? decoder = null;
            IWICBitmapFrameDecode? frame = null;
            IWICFormatConverter? converter = null;
            IWICColorTransform? transform = null;
            try
            {
                Check(factory.CreateDecoderFromFilename(path, IntPtr.Zero, GenericRead, CacheOnLoad, out decoder));
                Check(decoder.GetFrame(0, out frame));
                Check(factory.CreateFormatConverter(out converter));
                Guid bgra = PixelFormatBgra;
                Check(converter.Initialize(frame, ref bgra, 0, IntPtr.Zero, 0, 0));
                transform = ToSrgb(factory, frame, converter, path);
                converted = transform is not null;
                IWICBitmapSource source = transform is not null ? transform : (IWICBitmapSource)converter;
                Check(source.GetSize(out uint w, out uint h));

                // No EXIF rotation here: the HEIF decoder already applies the container's rotation
                // (irot/imir), and iPhones also keep Orientation=6 in EXIF, so applying it would turn the photo twice.
                var bmp = new Bitmap((int)w, (int)h, PixelFormat.Format32bppArgb);
                try
                {
                    var bits = bmp.LockBits(new Rectangle(0, 0, (int)w, (int)h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        Check(source.CopyPixels(IntPtr.Zero, (uint)bits.Stride, (uint)bits.Stride * h, bits.Scan0));
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
                Release(transform);
                Release(converter);
                Release(frame);
                Release(decoder);
                Release(factory);
            }
        }

        /// <summary>
        /// A transform from the frame's own colour context (iPhone HEIC: a Display P3 ICC profile) to sRGB,
        /// or null when the frame declares none or the transform cannot be built; the pixels are then used as they are.
        /// </summary>
        private static IWICColorTransform? ToSrgb(IWICImagingFactory factory, IWICBitmapFrameDecode frame, IWICFormatConverter converter, string path)
        {
            if (frame.GetColorContexts(0, null, out uint count) < 0 || count == 0) return null;
            var contexts = new IWICColorContext[count];
            IWICColorContext? srgb = null;
            IWICColorTransform? transform = null;
            try
            {
                for (int i = 0; i < count; i++) Check(factory.CreateColorContext(out contexts[i]));
                Check(frame.GetColorContexts(count, contexts, out _));
                // An ICC profile beats an EXIF colour-space tag when a file carries both.
                var source = contexts.FirstOrDefault(c => c.GetContextType(out int type) == 0 && type == ContextProfile) ?? contexts[0];
                Check(factory.CreateColorContext(out srgb));
                Check(srgb.InitializeFromExifColorSpace(ExifSrgb));
                Check(factory.CreateColorTransformer(out transform));
                Guid bgra = PixelFormatBgra;
                Check(transform.Initialize((IWICBitmapSource)converter, source, srgb, ref bgra));
                var result = transform;
                transform = null;
                return result;
            }
            catch (Exception ex)
            {
                Log.Write($"colour conversion failed {path}: {ex.Message}");
                return null;
            }
            finally
            {
                Release(transform);
                Release(srgb);
                foreach (var c in contexts) Release(c);
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
            [PreserveSig] int CreateBitmapScaler();
            [PreserveSig] int CreateBitmapClipper();
            [PreserveSig] int CreateBitmapFlipRotator();
            [PreserveSig] int CreateStream();
            [PreserveSig] int CreateColorContext(out IWICColorContext context);
            [PreserveSig] int CreateColorTransformer(out IWICColorTransform transform);
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
            // The caller creates the contexts; the decoder initialises them.
            [PreserveSig] int GetColorContexts(uint count, [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IWICColorContext[]? contexts, out uint actual);
        }

        [ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICBitmapSource
        {
            [PreserveSig] int GetSize(out uint width, out uint height);
            [PreserveSig] int GetPixelFormat(out Guid format);
            [PreserveSig] int GetResolution(out double dpiX, out double dpiY);
            [PreserveSig] int CopyPalette(IntPtr palette);
            [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint size, IntPtr buffer);
        }

        [ComImport, Guid("3c613a02-34b2-44ea-9a7c-45aea9c6fd6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICColorContext
        {
            [PreserveSig] int InitializeFromFilename();
            [PreserveSig] int InitializeFromMemory();
            [PreserveSig] int InitializeFromExifColorSpace(uint value);
            [PreserveSig] int GetContextType(out int type); // GetType in the SDK; renamed so it does not read like object.GetType
        }

        // IWICBitmapSource methods first, then IWICColorTransform's own.
        [ComImport, Guid("b66f034f-d0e2-40ab-b436-6de39e321a94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IWICColorTransform : IWICBitmapSource
        {
            [PreserveSig] new int GetSize(out uint width, out uint height);
            [PreserveSig] new int GetPixelFormat(out Guid format);
            [PreserveSig] new int GetResolution(out double dpiX, out double dpiY);
            [PreserveSig] new int CopyPalette(IntPtr palette);
            [PreserveSig] new int CopyPixels(IntPtr rect, uint stride, uint size, IntPtr buffer);
            [PreserveSig] int Initialize(IWICBitmapSource source, IWICColorContext contextSource, IWICColorContext contextDest, ref Guid pixelFormat);
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

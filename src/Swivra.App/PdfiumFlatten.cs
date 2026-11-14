using System.Runtime.InteropServices;

namespace Swivra.App;

/// <summary>
/// The few PDFium calls PDFtoImage does not offer, made straight into the pdfium library it ships, and the one
/// lock every PDFium use in Swivra goes through. PDFium is not thread-safe and PDFtoImage's own lock does not
/// cover calls made around it, so two jobs running at once must never be inside the library together.
/// </summary>
internal static class Pdfium
{
    /// <summary>Held around every PDFium call: PDFtoImage conversions and the raw calls below.</summary>
    public static readonly object Gate = new();

    /// <summary>
    /// A copy of the PDF in which every note and form field is burned into its page as it is displayed
    /// (appearance streams only, nothing regenerated) and the security is removed, so an owner-locked file is
    /// read like any other. What this copy's pages draw without notes is exactly what its page content holds,
    /// so searching that content searches everything that is shown. Null when PDFium could not do it.
    /// </summary>
    public static byte[]? Flatten(byte[] pdf)
    {
        lock (Gate)
        {
            try
            {
                // PDFtoImage initialises the library on first use; asking it for the page count does that and
                // gives the count the copy must keep.
                int expected;
                using (var ms = new MemoryStream(pdf)) expected = PDFtoImage.Conversion.GetPageCount(ms);
                byte[]? copy = FlattenLocked(pdf, expected);
                if (copy is null) return null;
                using (var ms = new MemoryStream(copy))
                    if (PDFtoImage.Conversion.GetPageCount(ms) != expected) { Log.Write("pdf flatten: page count changed"); return null; }
                return copy;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Write($"pdf flatten failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }
    }

    private static byte[]? FlattenLocked(byte[] pdf, int expected)
    {
        var pin = GCHandle.Alloc(pdf, GCHandleType.Pinned);
        IntPtr doc = IntPtr.Zero;
        try
        {
            doc = FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)pdf.Length, null);
            if (doc == IntPtr.Zero) { Log.Write($"pdf flatten: load error {FPDF_GetLastError()}"); return null; }
            int count = FPDF_GetPageCount(doc);
            if (count != expected || count < 1) { Log.Write($"pdf flatten: {count} pages, expected {expected}"); return null; }
            for (int i = 0; i < count; i++)
            {
                IntPtr page = FPDF_LoadPage(doc, i);
                if (page == IntPtr.Zero) { Log.Write($"pdf flatten: page {i + 1} did not load"); return null; }
                try
                {
                    // FPDFPage_Flatten reads the page's own MediaBox / CropBox only: with an inherited box it falls
                    // back to Letter size, clips the burned-in notes to that and writes it back as the MediaBox,
                    // which moves or cuts the page. The drawn area (crop box clipped to the media box, inherited
                    // or not) is pinned on the page before and after, so the page shows exactly what it showed.
                    if (FPDF_GetPageBoundingBox(page, out FsRectF box) == 0) { Log.Write($"pdf flatten: page {i + 1} has no box"); return null; }
                    bool pinBox = box.Right > box.Left && box.Top > box.Bottom;
                    if (pinBox) FPDFPage_SetMediaBox(page, box.Left, box.Bottom, box.Right, box.Top);
                    int result = FPDFPage_Flatten(page, FlatNormalDisplay);
                    if (result == FlattenFail) { Log.Write($"pdf flatten: page {i + 1} failed"); return null; }
                    if (pinBox)
                    {
                        FPDFPage_SetMediaBox(page, box.Left, box.Bottom, box.Right, box.Top);
                        FPDFPage_SetCropBox(page, box.Left, box.Bottom, box.Right, box.Top);
                    }
                }
                finally
                {
                    FPDF_ClosePage(page);
                }
            }

            using var output = new MemoryStream();
            WriteBlock write = (_, data, size) =>
            {
                try
                {
                    var chunk = new byte[size];
                    Marshal.Copy(data, chunk, 0, (int)size);
                    output.Write(chunk);
                    return 1;
                }
                catch
                {
                    return 0;   // never let an exception cross into native code
                }
            };
            var writer = new FileWrite { Version = 1, WriteBlock = Marshal.GetFunctionPointerForDelegate(write) };
            bool saved = FPDF_SaveAsCopy(doc, ref writer, RemoveSecurity) != 0;
            GC.KeepAlive(write);
            if (!saved || output.Length == 0) { Log.Write("pdf flatten: save failed"); return null; }
            return output.ToArray();
        }
        finally
        {
            if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
            pin.Free();
        }
    }

    private const int FlatNormalDisplay = 0;
    private const int FlattenFail = 0;
    private const uint RemoveSecurity = 3;   // FPDF_REMOVE_SECURITY

    [StructLayout(LayoutKind.Sequential)]
    private struct FsRectF
    {
        public float Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileWrite
    {
        public int Version;
        public IntPtr WriteBlock;
    }

    // unsigned long is 32 bits on Windows
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int WriteBlock(IntPtr self, IntPtr data, uint size);

    // Same library name as PDFtoImage's own imports, so the same pdfium.dll (and the same loaded instance) is used.
    private const string Lib = "pdfium";

    [DllImport(Lib)] private static extern IntPtr FPDF_LoadMemDocument64(IntPtr data, nuint size, [MarshalAs(UnmanagedType.LPStr)] string? password);
    [DllImport(Lib)] private static extern uint FPDF_GetLastError();
    [DllImport(Lib)] private static extern int FPDF_GetPageCount(IntPtr doc);
    [DllImport(Lib)] private static extern IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [DllImport(Lib)] private static extern void FPDF_ClosePage(IntPtr page);
    [DllImport(Lib)] private static extern void FPDF_CloseDocument(IntPtr doc);
    [DllImport(Lib)] private static extern int FPDF_GetPageBoundingBox(IntPtr page, out FsRectF rect);
    [DllImport(Lib)] private static extern int FPDFPage_Flatten(IntPtr page, int flag);
    [DllImport(Lib)] private static extern void FPDFPage_SetMediaBox(IntPtr page, float left, float bottom, float right, float top);
    [DllImport(Lib)] private static extern void FPDFPage_SetCropBox(IntPtr page, float left, float bottom, float right, float top);
    [DllImport(Lib)] private static extern int FPDF_SaveAsCopy(IntPtr doc, ref FileWrite writer, uint flags);
}

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

    // No single PDFium call takes this long on a page that can be drawn at all. A call that never returns
    // (a drawing the checks did not catch) keeps the gate for good; the next job must say so, not wait unseen.
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(3);

    /// <summary>The gate could not be had: an earlier PDFium call is stuck and holds it.</summary>
    public sealed class BusyException() : Exception("PDFium is held by a call that does not return");

    /// <summary>Takes the gate for a using block; throws <see cref="BusyException"/> instead of waiting without end.</summary>
    public static Held Enter()
    {
        if (!Monitor.TryEnter(Gate, Patience)) throw new BusyException();
        return default;
    }

    /// <summary>Takes the gate only if it is free within <paramref name="wait"/> (for work that must not keep anyone waiting); release it with <see cref="Exit"/>.</summary>
    public static bool TryEnter(TimeSpan wait) => Monitor.TryEnter(Gate, wait);

    public static void Exit() => Monitor.Exit(Gate);

    public readonly struct Held : IDisposable
    {
        public void Dispose() => Monitor.Exit(Gate);
    }

    /// <summary>
    /// A copy of the PDF in which every note and form field is burned into its page as it is displayed
    /// (appearance streams only, nothing regenerated) and the security is removed, so an owner-locked file is
    /// read like any other. What this copy's pages draw without notes is exactly what its page content holds,
    /// so searching that content searches everything that is shown. Null when PDFium could not do it.
    /// </summary>
    public static byte[]? Flatten(byte[] pdf)
    {
        using (Enter())
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

    /// <summary>A box in a drawn page: points, origin top-left, page rotation applied.</summary>
    public readonly record struct Area(double X0, double Y0, double X1, double Y1);

    /// <summary>
    /// PDFium's own text layer of one page, valid only inside the <see cref="ReadText"/> callback that hands it out:
    /// what PDFium reads from the content it draws, with the place it draws each character.
    /// </summary>
    public sealed class TextLayer
    {
        // Device pixels per point used to ask PDFium where a page point lands: 1/20 point is precise enough.
        private const double Scale = 20;
        private readonly IntPtr page, text;
        private readonly int devW, devH;

        /// <summary>The page as drawn, in points (rotation applied).</summary>
        public double Width { get; }
        public double Height { get; }
        /// <summary>The page's characters, one UTF-16 unit each; line breaks PDFium adds read as spaces.</summary>
        public string Text { get; }
        /// <summary>For each character of <see cref="Text"/>, its index in PDFium's text page.</summary>
        public int[] CharAt { get; }

        internal TextLayer(IntPtr page, IntPtr text)
        {
            (this.page, this.text) = (page, text);
            Width = FPDF_GetPageWidthF(page);
            Height = FPDF_GetPageHeightF(page);
            devW = (int)Math.Round(Width * Scale);
            devH = (int)Math.Round(Height * Scale);
            int count = Math.Max(0, FPDFText_CountChars(text));
            var sb = new System.Text.StringBuilder(count);
            var at = new List<int>(count);
            for (int k = 0; k < count; k++)
            {
                uint u = FPDFText_GetUnicode(text, k);
                string ch = u is > 0 and < 0x110000 && (u < 0xD800 || u > 0xDFFF) ? char.ConvertFromUtf32((int)u) : " ";
                if (ch is "\r" or "\n") ch = " ";
                foreach (char c in ch) { sb.Append(c); at.Add(k); }
            }
            Text = sb.ToString();
            CharAt = [.. at];
        }

        /// <summary>True for a character PDFium made up (a space or line break between text runs): nothing is drawn for it.</summary>
        public bool Generated(int k) => FPDFText_IsGenerated(text, k) == 1;

        /// <summary>
        /// Where character <paramref name="k"/> is drawn: <paramref name="ink"/> its glyph box, <paramref name="reach"/>
        /// the box from the font's ascent and descent. False when PDFium has neither.
        /// </summary>
        public bool Box(int k, out Area? ink, out Area? reach)
        {
            ink = reach = null;
            if (FPDFText_GetCharBox(text, k, out double l, out double r, out double b, out double t) != 0 && r > l && t > b)
                ink = Drawn(l, b, r, t);
            if (FPDFText_GetLooseCharBox(text, k, out FsRectF loose) != 0 && loose.Right > loose.Left && loose.Top > loose.Bottom)
                reach = Drawn(loose.Left, loose.Bottom, loose.Right, loose.Top);
            return ink is not null || reach is not null;
        }

        private Area Drawn(double l, double b, double r, double t)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (var (px, py) in new[] { (l, b), (l, t), (r, b), (r, t) })
            {
                FPDF_PageToDevice(page, 0, 0, devW, devH, 0, px, py, out int dx, out int dy);
                x0 = Math.Min(x0, dx); y0 = Math.Min(y0, dy); x1 = Math.Max(x1, dx); y1 = Math.Max(y1, dy);
            }
            return new Area(x0 / Scale, y0 / Scale, x1 / Scale, y1 / Scale);
        }
    }

    /// <summary>
    /// Hands PDFium's text layer of every page to <paramref name="each"/> (page index, layer), under the lock; the
    /// layer is null for a page whose text could not be loaded. False when the document itself could not be loaded.
    /// Exceptions thrown by <paramref name="each"/> pass through, after everything is closed.
    /// </summary>
    public static bool ReadText(byte[] pdf, Action<int, TextLayer?> each)
    {
        using (Enter())
        {
            try
            {
                // Initialises the library on first use, as in Flatten.
                using var ms = new MemoryStream(pdf);
                PDFtoImage.Conversion.GetPageCount(ms);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.Write($"pdf text: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            var pin = GCHandle.Alloc(pdf, GCHandleType.Pinned);
            IntPtr doc = IntPtr.Zero;
            try
            {
                doc = FPDF_LoadMemDocument64(pin.AddrOfPinnedObject(), (nuint)pdf.Length, null);
                if (doc == IntPtr.Zero) { Log.Write($"pdf text: load error {FPDF_GetLastError()}"); return false; }
                int count = FPDF_GetPageCount(doc);
                for (int i = 0; i < count; i++)
                {
                    IntPtr page = FPDF_LoadPage(doc, i);
                    if (page == IntPtr.Zero) { each(i, null); continue; }
                    IntPtr text = IntPtr.Zero;
                    try
                    {
                        text = FPDFText_LoadPage(page);
                        each(i, text == IntPtr.Zero ? null : new TextLayer(page, text));
                    }
                    finally
                    {
                        if (text != IntPtr.Zero) FPDFText_ClosePage(text);
                        FPDF_ClosePage(page);
                    }
                }
                return true;
            }
            finally
            {
                if (doc != IntPtr.Zero) FPDF_CloseDocument(doc);
                pin.Free();
            }
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
    [DllImport(Lib)] private static extern float FPDF_GetPageWidthF(IntPtr page);
    [DllImport(Lib)] private static extern float FPDF_GetPageHeightF(IntPtr page);
    [DllImport(Lib)] private static extern int FPDF_PageToDevice(IntPtr page, int startX, int startY, int sizeX, int sizeY, int rotate, double pageX, double pageY, out int deviceX, out int deviceY);
    [DllImport(Lib)] private static extern IntPtr FPDFText_LoadPage(IntPtr page);
    [DllImport(Lib)] private static extern void FPDFText_ClosePage(IntPtr text);
    [DllImport(Lib)] private static extern int FPDFText_CountChars(IntPtr text);
    [DllImport(Lib)] private static extern uint FPDFText_GetUnicode(IntPtr text, int index);
    [DllImport(Lib)] private static extern int FPDFText_IsGenerated(IntPtr text, int index);
    [DllImport(Lib)] private static extern int FPDFText_GetCharBox(IntPtr text, int index, out double left, out double right, out double bottom, out double top);
    [DllImport(Lib)] private static extern int FPDFText_GetLooseCharBox(IntPtr text, int index, out FsRectF rect);
}

using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using PigDocument = UglyToad.PdfPig.PdfDocument;
using PdfRectangle = UglyToad.PdfPig.Core.PdfRectangle;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;
using UglyToad.PdfPig.Tokenization.Scanner;

namespace Swivra.App;

/// <summary>
/// Real redaction of what the radar finds. PDFium first flattens a copy of the file: every note and form field is
/// burned into its page as it is displayed. PdfPig locates the matches in that copy's page content, PDFium renders
/// the copy's pages without notes or form fields (so what is drawn is exactly what was searched), opaque boxes are
/// painted into the pixels and the pages are written as images into a brand-new PDF (PDFsharp).
/// Nothing of the original text, metadata, annotations or form fields survives, so there is nothing to extract.
/// Note texts and field values that are not shown (a closed pop-up, a field without an appearance) are searched too:
/// a match there still counts and still produces the image-only file, which cannot carry them.
/// Embedded images are not read (no OCR) and letters without a Unicode value cannot be searched: the outcome
/// counts those pages so the user is told to check them.
/// PDFium's own text layer of the same copy is searched too (<see cref="CrossCheck"/>): the two libraries do not
/// always read a content stream alike, and whatever PDFium reads where it draws it is covered as well.
/// </summary>
internal static class PdfRedact
{
    private const int Dpi = 200;
    private const int JpegQuality = 90;
    private const double PadPoints = 1.5;   // box padding around the glyph boxes, in PDF points

    // An image smaller than this on the page cannot hold legible text: eleven digits at a barely readable 6 pt
    // are about 30 pt long and 6 pt tall, and need at least as many pixels. Rules, bullets and spacers stay out;
    // logos and photos are counted, which only adds a warning.
    private const double MinImageLong = 30, MinImageShort = 6;

    /// <param name="Items">Number of matches covered (page text, notes and form fields).</param>
    /// <param name="Written">True when the redacted file was written: there were matches, or notes had to be left out.</param>
    /// <param name="ImagePages">Pages with images large enough to hold text: they cannot be checked without OCR.</param>
    /// <param name="UnreadPages">Pages with text that is drawn but not searchable: letters without a usable Unicode value (Type 3, broken or unmapped fonts), text or images inside a pattern, a Type 3 glyph or a soft mask, or a page whose PDFium text layer could not be loaded.</param>
    /// <param name="LeftOutPages">The file could not be flattened: pages whose visible notes and form fields were left out of the output.</param>
    /// <param name="NoLookPages">Pages with visible notes or form fields that have no appearance of their own: flattening drops them.</param>
    public sealed record Outcome(int Items, bool Written, int ImagePages, int UnreadPages, int LeftOutPages, int NoLookPages);

    /// <summary>PDFium drew a page at a different size than the page box the boxes were measured in: painting would miss.</summary>
    public sealed class AlignmentException(int page) : Exception($"rendered size of page {page} does not match its page box")
    {
        public int Page { get; } = page;
    }

    /// <summary>
    /// Where PDFium draws some text on a page could not be worked out (a form drawn there could not be read while
    /// raised text was in effect): boxes measured from PdfPig's letters could miss it.
    /// </summary>
    public sealed class PlacementException(int page) : Exception($"text placement on page {page} could not be determined")
    {
        public int Page { get; } = page;
    }

    /// <summary>Everything needed to paint one page: displayed size in points and the boxes to black out (origin top-left).</summary>
    private sealed record PageBoxes(double W, double H, List<XRect> Boxes);

    /// <summary>
    /// Finds matches on every page; when there are any, writes the redacted image-only PDF to <paramref name="target"/>.
    /// With no matches nothing is written, unless the file could not be flattened and notes or form fields had to be
    /// left out: then the output is the only copy in which everything shown was searched.
    /// </summary>
    public static Outcome Hide(string input, string target)
    {
        byte[] original = SafeFile.ReadAll(input);
        // Before PdfPig reads a page or PDFium flattens one: a looping or runaway content tree takes the app down in
        // the first and never finishes in the second. The copy is checked too, it is read the same way.
        using var orig = PigDocument.Open(original);
        var tree = PdfTree.Check(orig);
        byte[]? flat = Pdfium.Flatten(original);

        var perPage = new List<PageBoxes>();
        var foundPerPage = new List<HashSet<string>>();
        var unread = new bool[orig.NumberOfPages];
        int items = 0, imagePages = 0, leftOutPages = 0, noLookPages = 0;
        using (var copy = OpenCopy(flat, orig.NumberOfPages))
        {
            if (copy is not null) tree = PdfTree.Check(copy);
            if (copy is null) flat = null;
            PigDocument drawn = flat is null ? orig : copy!;
            var blind = new BlindFonts(drawn);
            for (int n = 1; n <= orig.NumberOfPages; n++)
            {
                // Page content comes from the copy that is drawn; note texts and field values from the original,
                // where the notes still are (flattening removes them from the copy).
                Page page = drawn.GetPage(n), source = flat is null ? page : orig.GetPage(n);
                var map = PageMap.For(drawn, page);
                double drift = RiseDrift.Of(drawn, page);
                if (double.IsNaN(drift)) throw new PlacementException(n);
                var (boxes, count, found) = Locate(page, map, drift);
                var notes = ScanNotes(orig, source, flat is null ? map : PageMap.For(orig, source), boxes, found);
                items += count + notes.Hits;
                if (notes.Visible && flat is null) leftOutPages++;
                if (notes.NoLook && flat is not null) noLookPages++;
                // Letters without a usable value, and text or images shown inside a pattern, a Type 3 glyph or a
                // soft mask, which neither text layer has: drawn, but not searched.
                if (HasUnreadableText(page, blind) || tree.HidesText(page.Dictionary)) unread[n - 1] = true;
                if (page.GetImages().Any(CanHoldText)) imagePages++;
                perPage.Add(new PageBoxes(map.Width, map.Height, boxes));
                foundPerPage.Add(found);
            }
        }
        items += CrossCheck(flat ?? original, perPage, foundPerPage, unread);
        int unreadPages = unread.Count(u => u);
        bool written = items > 0 || leftOutPages > 0;
        if (!written) return new Outcome(0, false, imagePages, unreadPages, 0, noLookPages);

        using var output = new PdfSharp.Pdf.PdfDocument();
        output.Info.Creator = "Swivra";
        byte[] bytes = flat ?? original;
        for (int i = 0; i < perPage.Count; i++)
        {
            var pb = perPage[i];
            using SKBitmap bmp = Render(bytes, i, pb);
            double sx = bmp.Width / pb.W, sy = bmp.Height / pb.H;
            SKRect Px(XRect r) => SKRect.Create((float)(r.X * sx), (float)(r.Y * sy), (float)(r.Width * sx), (float)(r.Height * sy));
            using (var canvas = new SKCanvas(bmp))
            using (var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false })
            {
                foreach (XRect b in pb.Boxes)
                    canvas.DrawRect(Px(b), paint);
            }

            using var image = SKImage.FromBitmap(bmp);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            using var jpg = new MemoryStream(data.ToArray());

            PdfPage outPage = output.AddPage();
            outPage.Width = XUnit.FromPoint(pb.W);
            outPage.Height = XUnit.FromPoint(pb.H);
            using var gfx = XGraphics.FromPdfPage(outPage);
            using XImage xi = XImage.FromStream(jpg);
            gfx.DrawImage(xi, 0, 0, pb.W, pb.H);
        }
        output.Save(target);
        return new Outcome(items, true, imagePages, unreadPages, leftOutPages, noLookPages);
    }

    /// <summary>The flattened copy for PdfPig, or null (notes and fields are then left out) when it does not read back whole.</summary>
    private static PigDocument? OpenCopy(byte[]? flat, int pages)
    {
        if (flat is null) return null;
        PigDocument? doc = null;
        try
        {
            doc = PigDocument.Open(flat);
            if (doc.NumberOfPages == pages) return doc;
            Log.Write($"pdf flatten: {doc.NumberOfPages} pages read back, expected {pages}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Write($"pdf flatten: copy unreadable: {ex.Message}");
        }
        doc?.Dispose();
        return null;
    }

    // Page content only: no notes and no form fields. In the flattened copy they are already part of the page;
    // without flattening they were never searched as drawn, so they must not appear.
    private static readonly PDFtoImage.RenderOptions PageOnly = new(Dpi: Dpi, WithAnnotations: false, WithFormFill: false);

    /// <summary>
    /// One page as PDFium draws it: its crop area with /Rotate applied, where the boxes were measured. If the size
    /// disagrees, PDFium saw another page box and every box would land in the wrong place, so the work stops.
    /// </summary>
    private static SKBitmap Render(byte[] bytes, int index, PageBoxes pb)
    {
        using var ms = new MemoryStream(bytes);
        SKBitmap bmp;
        lock (Pdfium.Gate) bmp = PDFtoImage.Conversion.ToImage(ms, index, options: PageOnly);
        double ew = pb.W * Dpi / 72, eh = pb.H * Dpi / 72;
        if (Math.Abs(bmp.Width - ew) > Math.Max(2, ew * 0.005) || Math.Abs(bmp.Height - eh) > Math.Max(2, eh * 0.005))
        {
            bmp.Dispose();
            throw new AlignmentException(index + 1);
        }
        return bmp;
    }

    // A PDFium glyph box may poke this far (points) out of a painted box and still count as covered.
    private const double CoverSlack = 0.25;

    /// <summary>
    /// Second opinion from PDFium's own text layer of the copy that is drawn. PdfPig and PDFium do not always read a
    /// content stream alike (extra operands, malformed numbers, inline image data, two names before one Do): one can
    /// place or even see text the other does not. Every match in PDFium's text must lie inside the boxes found from
    /// PdfPig's letters; a character outside them gets PDFium's own box, and a match PdfPig did not have at all counts
    /// as an item. A page whose text layer cannot be loaded is marked unread, so it is never called clean.
    /// Throws <see cref="PlacementException"/> when PDFium cannot say where a matched character is.
    /// </summary>
    /// <returns>Matches PDFium found that the page's other searches had not.</returns>
    private static int CrossCheck(byte[] pdf, List<PageBoxes> pages, List<HashSet<string>> found, bool[] unread)
    {
        int added = 0;
        bool loaded = Pdfium.ReadText(pdf, (i, layer) =>
        {
            if (i >= pages.Count) return;
            if (layer is null)
            {
                unread[i] = true;
                return;
            }
            var pb = pages[i];
            if (Math.Abs(layer.Width - pb.W) > Math.Max(0.5, pb.W * 0.005) || Math.Abs(layer.Height - pb.H) > Math.Max(0.5, pb.H * 0.005))
                throw new AlignmentException(i + 1);
            double fx = pb.W / layer.Width, fy = pb.H / layer.Height;
            XRect Drawn(Pdfium.Area a) => new(a.X0 * fx, a.Y0 * fy, (a.X1 - a.X0) * fx, (a.Y1 - a.Y0) * fy);

            string text = Fold(layer.Text);
            foreach (var hit in Radar.FindAll(text))
            {
                bool open = false;
                var paint = new List<XRect>();
                for (int c = hit.Index; c < hit.Index + hit.Length; c++)
                {
                    int k = layer.CharAt[c];
                    if (k < 0 || char.IsWhiteSpace(text[c]) || layer.Generated(k)) continue;
                    if (!layer.Box(k, out Pdfium.Area? ink, out Pdfium.Area? reach)) throw new PlacementException(i + 1);
                    XRect test = Drawn((ink ?? reach)!.Value);
                    if (pb.Boxes.Any(b => Inside(test, b))) continue;
                    open = true;
                    paint.Add(ink is Pdfium.Area a && reach is Pdfium.Area r ? Union(Drawn(a), Drawn(r)) : test);
                }
                foreach (XRect p in paint) AddPadded(pb.Boxes, p, pb.W, pb.H);
                if (open && found[i].Add(Key(text, hit))) added++;
            }
        });
        if (!loaded) Array.Fill(unread, true);
        return added;
    }

    private static bool Inside(XRect r, XRect box) =>
        r.X >= box.X - CoverSlack && r.Y >= box.Y - CoverSlack
        && r.X + r.Width <= box.X + box.Width + CoverSlack && r.Y + r.Height <= box.Y + box.Height + CoverSlack;

    /// <summary>
    /// Symbol fonts (Symbol, Wingdings, many PowerPoint and math fonts) put their characters at U+F020..U+F0FF, the
    /// private-use copy of U+0020..U+00FF: Word bullets come out as U+F0B7. Folded back, they read as ordinary
    /// characters, and digits written in such a font can be searched. One character for one, so positions stay.
    /// </summary>
    internal static string Fold(string s)
    {
        if (!s.Any(IsSymbolArea)) return s;
        var chars = s.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
            if (IsSymbolArea(chars[i])) chars[i] = (char)(chars[i] - 0xF000);
        return new string(chars);
    }

    private static bool IsSymbolArea(char c) => c is >= '\uF020' and <= '\uF0FF';

    /// <summary>
    /// Maps PdfPig's page coordinates to the area PDFium draws: the effective crop box (inherited or not, clipped to
    /// the media box) with /Rotate applied, in points with the origin top-left. PdfPig measures letters and notes
    /// from the crop box it found, rotated, origin bottom-left; it does not see a CropBox inherited from /Pages, so
    /// its points go back to user space first and from there into the box PDFium uses.
    /// </summary>
    private sealed class PageMap
    {
        private readonly PdfRectangle pig, crop;   // normalised boxes in user space
        private readonly int rot;

        public double Width => rot is 90 or 270 ? crop.Height : crop.Width;
        public double Height => rot is 90 or 270 ? crop.Width : crop.Height;

        private PageMap(PdfRectangle pig, PdfRectangle crop, int rot) => (this.pig, this.crop, this.rot) = (pig, crop, rot);

        public static PageMap For(PigDocument doc, Page page)
        {
            PdfRectangle media = Normal(page.MediaBox.Bounds);
            PdfRectangle crop = InheritedBox(doc, page.Dictionary, NameToken.CropBox) is PdfRectangle c ? Clip(Normal(c), media) ?? media : media;
            return new PageMap(Normal(page.CropBox.Bounds), crop, ((page.Rotation.Value % 360) + 360) % 360);
        }

        /// <summary>A PdfPig rectangle (any corner order, possibly rotated) as an axis-aligned box in the drawn page.</summary>
        public XRect Map(PdfRectangle r)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
            foreach (PdfPoint p in new[] { r.TopLeft, r.TopRight, r.BottomLeft, r.BottomRight })
            {
                var (x, y) = Map(p.X, p.Y);
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
            }
            return new XRect(x0, y0, x1 - x0, y1 - y0);
        }

        private (double X, double Y) Map(double x, double y)
        {
            // PdfPig display -> user space
            double w = pig.Width, h = pig.Height;
            var (u, v) = rot switch { 90 => (w - y, x), 180 => (w - x, h - y), 270 => (y, h - x), _ => (x, y) };
            double ux = pig.Left + u, uy = pig.Bottom + v;
            // user space -> PDFium's drawn page, origin top-left
            w = crop.Width; h = crop.Height;
            u = ux - crop.Left; v = uy - crop.Bottom;
            var (dx, dy) = rot switch { 90 => (v, w - u), 180 => (w - u, h - v), 270 => (h - v, u), _ => (u, v) };
            return (dx, Height - dy);
        }

        private static PdfRectangle Normal(PdfRectangle r) =>
            new(Math.Min(r.Left, r.Right), Math.Min(r.Bottom, r.Top), Math.Max(r.Left, r.Right), Math.Max(r.Bottom, r.Top));

        private static PdfRectangle? Clip(PdfRectangle a, PdfRectangle b)
        {
            double l = Math.Max(a.Left, b.Left), btm = Math.Max(a.Bottom, b.Bottom), r = Math.Min(a.Right, b.Right), t = Math.Min(a.Top, b.Top);
            return r - l > 0 && t - btm > 0 ? new PdfRectangle(l, btm, r, t) : null;
        }

        /// <summary>A page box from the page or, failing that, from its /Pages ancestors.</summary>
        private static PdfRectangle? InheritedBox(PigDocument doc, DictionaryToken dict, NameToken key)
        {
            DictionaryToken? d = dict;
            for (int depth = 0; d is not null && depth < 64; depth++)
            {
                if (d.TryGet(key, out IToken? box))
                {
                    if (Resolve(doc, box) is ArrayToken { Length: 4 } a)
                    {
                        var n = a.Data.Select(t => Resolve(doc, t)).OfType<NumericToken>().Select(t => t.Double).ToArray();
                        if (n.Length == 4) return new PdfRectangle(n[0], n[1], n[2], n[3]);
                    }
                    return null;   // malformed: PDFium falls back to the media box too, the size check catches a disagreement
                }
                d = d.TryGet(NameToken.Parent, out IToken? parent) ? Resolve(doc, parent) as DictionaryToken : null;
            }
            return null;
        }
    }

    private static IToken? Resolve(PigDocument doc, IToken? token)
    {
        for (int i = 0; token is IndirectReferenceToken r && i < 16; i++)
            token = doc.Structure.GetObject(r.Data)?.Data;
        return token is IndirectReferenceToken ? null : token;
    }

    private static bool CanHoldText(IPdfImage img)
    {
        var b = img.BoundingBox;
        double w = Math.Sqrt(Math.Pow(b.TopRight.X - b.TopLeft.X, 2) + Math.Pow(b.TopRight.Y - b.TopLeft.Y, 2));
        double h = Math.Sqrt(Math.Pow(b.TopLeft.X - b.BottomLeft.X, 2) + Math.Pow(b.TopLeft.Y - b.BottomLeft.Y, 2));
        int pw = img.WidthInSamples, ph = img.HeightInSamples;
        return Math.Max(w, h) >= MinImageLong && Math.Min(w, h) >= MinImageShort
            && Math.Max(pw, ph) >= MinImageLong && Math.Min(pw, ph) >= MinImageShort;
    }

    /// <param name="Hits">Matches in note texts and field values that the page content does not already show.</param>
    /// <param name="Visible">The page has notes or form fields that are drawn when the page is displayed.</param>
    /// <param name="NoLook">Some of them have no appearance of their own: flattening, which burns appearances only, drops them.</param>
    private readonly record struct NoteScan(int Hits, bool Visible, bool NoLook);

    /// <summary>
    /// Searches the notes and form fields of a page: note text (/Contents, rich text /RC) and field values (/V, /DV and
    /// list options, inherited from parent fields, rich values, button captions). What they show is in the flattened
    /// page content and searched there; this finds what is not shown (a closed pop-up, a field without an appearance,
    /// a default value) so such a file is never called clean. A match already found in the page content is not counted
    /// twice. A visible note or field with a match is also blacked out whole where it sits.
    /// </summary>
    private static NoteScan ScanNotes(PigDocument doc, Page page, PageMap map, List<XRect> boxes, HashSet<string> found)
    {
        int count = 0;
        bool visible = false, noLook = false;
        foreach (Annotation a in page.GetAnnotations())
        {
            var dict = a.AnnotationDictionary;
            var texts = new List<string>();
            if (!string.IsNullOrEmpty(a.Content)) texts.Add(a.Content);
            if (TextOf(doc, Get(doc, dict, "RC"), out string? rich)) texts.Add(StripMarkup(rich!));
            if (a.Type == AnnotationType.Widget)
            {
                foreach (string key in new[] { "V", "DV", "RV", "Opt" })
                    if (TextOf(doc, Inherited(doc, dict, key), out string? v)) texts.Add(key == "RV" ? StripMarkup(v!) : v!);
                if (Resolve(doc, Get(doc, dict, "MK")) is DictionaryToken mk)
                    foreach (string key in new[] { "CA", "RC", "AC" })
                        if (TextOf(doc, Get(doc, mk, key), out string? caption)) texts.Add(caption!);
            }

            int hits = 0;
            foreach (string t in texts)
                foreach (var h in Radar.FindAll(t))
                    if (found.Add(Key(t, h))) hits++;
            count += hits;

            if ((a.Flags & (AnnotationFlags.Hidden | AnnotationFlags.NoView)) != 0) continue;   // not drawn
            // A link or pop-up without an appearance of its own draws nothing on the page.
            bool draws = a.HasNormalAppearance || a.Type is not (AnnotationType.Link or AnnotationType.Popup);
            if (!draws) continue;
            visible = true;
            if (!a.HasNormalAppearance) noLook = true;
            if (texts.Any(t => Radar.FindAll(t).Count > 0)) AddPadded(boxes, map.Map(a.Rectangle), map.Width, map.Height);
        }
        return new NoteScan(count, visible, noLook);
    }

    /// <summary>A match as kind plus its letters and digits only, so the same number spelled with other separators is one item.</summary>
    private static string Key(string text, Radar.Hit h) =>
        h.Kind + ":" + new string(text.AsSpan(h.Index, h.Length).ToArray().Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    // Unreadable letters on one page that are always worth a warning, wherever they sit: the shortest item the radar
    // looks for (a mobile number without separators) has ten characters, so ten unreadable letters could hide one
    // even when each stands alone (boxed form cells).
    private const int UnreadableEnough = 10;
    // Neighbouring letters further apart than this many font sizes are not read as one word or number.
    private const double NeighbourReach = 3;

    /// <summary>
    /// True when the page draws letters that cannot be searched in a way that could hide an item: letters PdfPig
    /// cannot turn into text (no value, a control character, U+FFFD, a private-use code outside the symbol-font
    /// area, see <see cref="Fold"/>) or letters in a font whose codes have no Unicode meaning (<see cref="BlindFonts"/>).
    /// A lone ornament between readable words (a check mark, an arrow, a math sign) is not worth a warning; one next
    /// to a digit (it could be one digit of a number whose others are readable), next to another unreadable letter
    /// (a word or number in that font) or <see cref="UnreadableEnough"/> of them on a page are.
    /// </summary>
    private static bool HasUnreadableText(Page page, BlindFonts blind)
    {
        var letters = page.Letters.Where(l => string.IsNullOrEmpty(l.Value) || !string.IsNullOrWhiteSpace(l.Value)).ToList();
        var bad = letters.Select(l => IsUnreadable(l, blind)).ToArray();
        if (bad.Count(b => b) >= UnreadableEnough) return true;
        for (int i = 0; i < letters.Count; i++)
        {
            if (!bad[i]) continue;
            foreach (int j in new[] { i - 1, i + 1 })
            {
                if (j < 0 || j >= letters.Count || !Near(letters[Math.Min(i, j)], letters[Math.Max(i, j)])) continue;
                if (bad[j] || Fold(letters[j].Value).Any(char.IsAsciiDigit)) return true;
            }
        }
        return false;

        static bool Near(Letter a, Letter b)
        {
            double gap = Math.Sqrt(Math.Pow(b.StartBaseLine.X - a.EndBaseLine.X, 2) + Math.Pow(b.StartBaseLine.Y - a.EndBaseLine.Y, 2));
            double size = Math.Max(Math.Max(a.PointSize, b.PointSize), 1);
            return gap <= NeighbourReach * size;
        }
    }

    private static bool IsUnreadable(Letter l, BlindFonts blind)
    {
        if (string.IsNullOrEmpty(l.Value) || blind.Has(l)) return true;
        foreach (Rune r in Fold(l.Value).EnumerateRunes())
        {
            if (r.Value == 0xFFFD) return true;
            var cat = Rune.GetUnicodeCategory(r);
            if (cat == System.Globalization.UnicodeCategory.PrivateUse) return true;
            if (cat == System.Globalization.UnicodeCategory.Control && !Rune.IsWhiteSpace(r)) return true;
        }
        return false;
    }

    /// <summary>
    /// Composite (Type 0) fonts whose codes mean nothing without a /ToUnicode map: an Identity ordering only numbers
    /// the glyphs of the embedded font, often in order of first use in a subset. PdfPig still hands out "letters" for
    /// them (the codes taken as characters, e.g. "BCDEFGHIJFA" for eleven digits), which look readable and are not.
    /// </summary>
    private sealed class BlindFonts(PigDocument doc)
    {
        private readonly Dictionary<IndirectReference, bool> known = [];

        public bool Has(Letter l)
        {
            if (l.FontDetails?.FontDictionaryReference is not IndirectReference r) return false;
            if (!known.TryGetValue(r, out bool blind)) known[r] = blind = IsBlind(r);
            return blind;
        }

        private bool IsBlind(IndirectReference r)
        {
            if (Resolve(doc, new IndirectReferenceToken(r)) is not DictionaryToken font) return false;
            if (Get(doc, font, "Subtype") is not NameToken { Data: "Type0" } || Get(doc, font, "ToUnicode") is not null) return false;
            if (Get(doc, font, "DescendantFonts") is not ArrayToken { Length: > 0 } kids || Resolve(doc, kids.Data[0]) is not DictionaryToken cid) return false;
            return Get(doc, cid, "CIDSystemInfo") is DictionaryToken info && TextOf(doc, Get(doc, info, "Ordering"), out string? ordering)
                && ordering!.Trim() == "Identity";
        }
    }

    private static IToken? Get(PigDocument doc, DictionaryToken dict, string key) =>
        dict.TryGet(NameToken.Create(key), out IToken? t) ? Resolve(doc, t) : null;

    /// <summary>A field attribute from the widget or the field above it (terminal fields inherit /V, /FT, /Opt).</summary>
    private static IToken? Inherited(PigDocument doc, DictionaryToken dict, string key)
    {
        DictionaryToken? d = dict;
        for (int depth = 0; d is not null && depth < 32; depth++)
        {
            if (Get(doc, d, key) is IToken t) return t;
            d = Get(doc, d, "Parent") as DictionaryToken;
        }
        return null;
    }

    /// <summary>All text in a value: strings, names, arrays of them (multi-select values, option pairs) and text streams.</summary>
    private static bool TextOf(PigDocument doc, IToken? token, out string? text)
    {
        var sb = new StringBuilder();
        bool ok = Collect(doc, token, sb, 0);
        text = ok ? sb.ToString() : null;
        return ok && token is not null;
    }

    private static bool Collect(PigDocument doc, IToken? token, StringBuilder sb, int depth)
    {
        switch (Resolve(doc, token))
        {
            case null: return true;
            case StringToken s: sb.Append(s.Data).Append('\n'); return true;
            case HexToken h: sb.Append(h.Data).Append('\n'); return true;
            case NameToken n: sb.Append(n.Data).Append('\n'); return true;
            case NumericToken num: sb.Append(num.Data.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n'); return true;
            case BooleanToken or NullToken: return true;
            case ArrayToken arr when depth < 8:
                foreach (IToken t in arr.Data) if (!Collect(doc, t, sb, depth + 1)) return false;
                return true;
            case StreamToken stream:
                try
                {
                    var bytes = UglyToad.PdfPig.PdfExtensions.Decode(stream, doc.Structure.FilterProvider, doc.Structure.TokenScanner).ToArray();
                    sb.Append(bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2) : Encoding.UTF8.GetString(bytes)).Append('\n');
                    return true;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    return false;
                }
            default: return false;
        }
    }

    /// <summary>Rich text (XHTML) to plain text: tags become spaces, entities are decoded.</summary>
    private static string StripMarkup(string xml) => WebUtility.HtmlDecode(Regex.Replace(xml, "<[^>]*>", " "));

    /// <summary>
    /// Boxes to paint for one page, in the drawn page (points, origin top-left).
    /// The text is rebuilt from the letters in drawing order (a space wherever the letters are not neighbours),
    /// so it works for rotated pages and for IBANs or phone numbers written with spaces; each matched character maps back to its letter box.
    /// </summary>
    /// <param name="grow">Distance (page units) by which PDFium may draw a letter away from where PdfPig puts it: see <see cref="RiseDrift"/>.</param>
    private static (List<XRect> Boxes, int Count, HashSet<string> Found) Locate(Page page, PageMap map, double grow)
    {
        var rects = new List<PdfRectangle?>();   // one entry per character of the text; null for separators
        string text = PageText(page, rects, grow);

        var boxes = new List<XRect>();
        var hits = Radar.FindAll(text);
        var found = new HashSet<string>();
        foreach (var hit in hits)
        {
            found.Add(Key(text, hit));
            XRect? run = null;
            for (int i = hit.Index; i < hit.Index + hit.Length; i++)
            {
                if (rects[i] is not PdfRectangle r) continue;
                XRect cur = map.Map(r);
                if (run is XRect p && Overlaps(p, cur)) run = Union(p, cur);
                else
                {
                    if (run is XRect done) AddPadded(boxes, done, map.Width, map.Height);
                    run = cur;
                }
            }
            if (run is XRect last) AddPadded(boxes, last, map.Width, map.Height);
        }
        return (boxes, hits.Count, found);
    }

    /// <summary>Page text rebuilt from the letters in drawing order (also used for the text-only Word export of rotated pages); <paramref name="rects"/> gets one box per character.</summary>
    internal static string PageText(Page page, List<PdfRectangle?>? rects = null, double grow = 0)
    {
        rects ??= [];
        var sb = new StringBuilder();
        Letter? prev = null;
        foreach (Letter l in page.Letters)
        {
            if (string.IsNullOrWhiteSpace(l.Value))
            {
                if (sb.Length > 0 && rects[^1] is not null) { sb.Append(' '); rects.Add(null); }
                prev = null;
                continue;
            }
            if (prev is not null)
            {
                double gap = Math.Sqrt(Math.Pow(l.StartBaseLine.X - prev.EndBaseLine.X, 2) + Math.Pow(l.StartBaseLine.Y - prev.EndBaseLine.Y, 2));
                if (gap > 0.15 * Math.Max(prev.PointSize, l.PointSize)) { sb.Append(' '); rects.Add(null); }
            }
            var box = CoverBox(l);
            if (grow > 0) box = new PdfRectangle(box.Left - grow, box.Bottom - grow, box.Right + grow, box.Top + grow);
            foreach (char ch in l.Value) { sb.Append(ch); rects.Add(box); }
            prev = l;
        }
        return sb.ToString();
    }

    // Line box around the baseline, in multiples of the font size: below for descenders (g, ş, ç), above for
    // capitals with accents (İ, Ğ, Ş). Generous on purpose: covering a little too much is fine, too little is a leak.
    // Along the baseline each letter reaches a little further than its advance: an outlined (stroked) glyph spills
    // half its line width past its edges, and only the first and last letter of a run are exposed to that. A share
    // of the font size stays inside the word gap, so the next word is not swallowed.
    private const double Descent = 0.3, Ascent = 1.05, Reach = 0.15;

    /// <summary>
    /// The area a letter can paint, axis-aligned: its glyph bounding box joined with a box spanned along the baseline
    /// from the font size. The glyph box alone depends on font metrics and can miss accents or come out empty.
    /// </summary>
    private static PdfRectangle CoverBox(Letter l)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        void Add(double x, double y) { xs.Add(x); ys.Add(y); }

        var g = l.BoundingBox;
        if (g.Width > 0 || g.Height > 0)
        {
            Add(g.TopLeft.X, g.TopLeft.Y); Add(g.TopRight.X, g.TopRight.Y);
            Add(g.BottomLeft.X, g.BottomLeft.Y); Add(g.BottomRight.X, g.BottomRight.Y);
        }

        // No font size and no glyph box (broken or Type 3 font): assume ordinary 10 pt text rather than a 1 pt sliver.
        double size = l.PointSize > 0 ? l.PointSize : g.Height > 0 ? g.Height : 10;
        var (sx, sy, ex, ey) = (l.StartBaseLine.X, l.StartBaseLine.Y, l.EndBaseLine.X, l.EndBaseLine.Y);
        double dx = ex - sx, dy = ey - sy, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) (dx, dy, len) = (1, 0, 1);   // zero-width letter: assume horizontal text
        double ux = -dy / len, uy = dx / len;          // "up", perpendicular to the baseline
        double rx = dx / len * Reach * size, ry = dy / len * Reach * size;
        foreach (var (bx, by) in new[] { (sx - rx, sy - ry), (ex + rx, ey + ry) })
            foreach (double k in new[] { -Descent * size, Ascent * size })
                Add(bx + ux * k, by + uy * k);

        return new PdfRectangle(xs.Min(), ys.Min(), xs.Max(), ys.Max());
    }

    /// <summary>True when two consecutive letter boxes belong to the same run (same line, or same column on rotated text): they touch or nearly do.</summary>
    private static bool Overlaps(XRect a, XRect b)
    {
        double slack = Math.Max(Math.Max(a.Width, a.Height), Math.Max(b.Width, b.Height)) * 0.6;
        return b.X <= a.X + a.Width + slack && a.X <= b.X + b.Width + slack && b.Y <= a.Y + a.Height + slack && a.Y <= b.Y + b.Height + slack;
    }

    private static XRect Union(XRect a, XRect b)
    {
        double x0 = Math.Min(a.X, b.X), y0 = Math.Min(a.Y, b.Y);
        double x1 = Math.Max(a.X + a.Width, b.X + b.Width), y1 = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new XRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// Text rise (Ts) is part of the graphics state, and PdfPig carries the rise in effect at a form XObject's Do
    /// into the form. PDFium does not: every form it draws starts at rise 0 (note appearances burned in by
    /// flattening are such forms too). Text a form shows before setting its own rise is then drawn by PDFium away
    /// from where PdfPig puts its letters. This walks the page content and every form it draws, keeps both rises
    /// (and the text and CTM scale, since rise is in text space) and returns the largest distance, in page units,
    /// between the two placements of any shown text: 0 when they always agree. NaN when a form drawn while they
    /// may disagree cannot be read, so the distance is unknown.
    /// Horizontal scaling (Tz) and leading (TL) behave the same way: PdfPig carries them into a form, PDFium starts
    /// it at 100 and 0 (measured; character and word spacing, render mode and font are carried by both). Their
    /// effect grows along the line and from line to line, so no single distance bounds it: text a form shows while
    /// either disagrees (leading only once a line move has used it) also gives NaN, and the file is refused.
    /// </summary>
    private static class RiseDrift
    {
        private const int MaxDepth = 32;

        /// <summary>Resources in effect: a form's own, then the ones it was drawn with, out to the page's.</summary>
        private sealed record Scope(DictionaryToken Dict, Scope? Outer);

        /// <summary>Linear part of a PDF matrix [a b c d]; translation never changes a distance.</summary>
        private readonly record struct Lin(double A, double B, double C, double D)
        {
            public static readonly Lin Identity = new(1, 0, 0, 1);
            public Lin Times(Lin m) => new(A * m.A + B * m.C, A * m.B + B * m.D, C * m.A + D * m.C, C * m.B + D * m.D);
        }

        /// <summary>Each value as PdfPig keeps it and as PDFium draws it: rise, horizontal scaling, leading.</summary>
        private readonly record struct State(double PigRise, double DrawnRise, Lin Ctm, double PigTz = 100, double DrawnTz = 100, double PigTL = 0, double DrawnTL = 0)
        {
            public bool Agrees => PigRise == DrawnRise && PigTz == DrawnTz && PigTL == DrawnTL;
        }

        public static double Of(PigDocument doc, Page page)
        {
            Scope? resources = InheritedDict(doc, page.Dictionary, NameToken.Resources) is DictionaryToken r ? new Scope(r, null) : null;
            byte[]? content;
            try
            {
                content = PageContent(doc, page.Dictionary);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return 0;   // PdfPig cannot read this content either: it has no letters, so there is nothing to place
            }
            if (content is null) return 0;
            return Walk(doc, content, resources, new State(0, 0, Lin.Identity), 0, new HashSet<IndirectReference>());
        }

        private static double Walk(PigDocument doc, byte[] content, Scope? resources, State start, int depth, HashSet<IndirectReference> open)
        {
            // Nothing can disagree in a stream that starts in agreement and never sets a rise or draws a form.
            if (start.Agrees && !Has(content, "Ts") && !Has(content, "Do")) return 0;
            // If reading stops part way, what was not read could only be misplaced when a rise, a scaling or a leading is in play.
            bool matters = !start.Agrees || start.PigRise != 0 || start.PigTz != 100 || start.PigTL != 0
                || Has(content, "Ts") || Has(content, "Tz") || Has(content, "TL") || Has(content, "TD");
            double drift = 0;
            var stack = new Stack<State>();
            State s = start;
            Lin tm = Lin.Identity;
            bool lineMoved = false;   // a line move (T*, ', ") used a leading the two disagree on: the rest of the block is off
            var operands = new List<IToken>();
            try
            {
                var scanner = new CoreTokenScanner(new MemoryInputBytes(content), false, new StackDepthGuard(256), ScannerScope.None, null, true);
                while (scanner.MoveNext())
                {
                    if (scanner.CurrentToken is not OperatorToken op) { operands.Add(scanner.CurrentToken); continue; }
                    switch (op.Data)
                    {
                        case "q": if (stack.Count < 4096) stack.Push(s); break;
                        case "Q": if (stack.Count > 0) s = stack.Pop(); break;
                        case "cm" when Numbers(operands, 6) is double[] m: s = s with { Ctm = new Lin(m[0], m[1], m[2], m[3]).Times(s.Ctm) }; break;
                        case "BT": tm = Lin.Identity; lineMoved = false; break;
                        case "Tm" when Numbers(operands, 6) is double[] m: tm = new Lin(m[0], m[1], m[2], m[3]); lineMoved = false; break;
                        case "Ts" when operands.Count > 0 && operands[^1] is NumericToken r: s = s with { PigRise = r.Double, DrawnRise = r.Double }; break;
                        case "Tz" when operands.Count > 0 && operands[^1] is NumericToken z: s = s with { PigTz = z.Double, DrawnTz = z.Double }; break;
                        case "TL" when operands.Count > 0 && operands[^1] is NumericToken l: s = s with { PigTL = l.Double, DrawnTL = l.Double }; break;
                        case "TD" when Numbers(operands, 2) is double[] td: s = s with { PigTL = -td[1], DrawnTL = -td[1] }; break;
                        case "T*": if (s.PigTL != s.DrawnTL) lineMoved = true; break;
                        case "Tj" or "TJ" or "'" or "\"":
                            if (op.Data is "'" or "\"" && s.PigTL != s.DrawnTL) lineMoved = true;
                            if (lineMoved || s.PigTz != s.DrawnTz) return double.NaN;
                            if (s.PigRise != s.DrawnRise)
                            {
                                Lin full = tm.Times(s.Ctm);
                                drift = Math.Max(drift, Math.Abs(s.PigRise - s.DrawnRise) * Math.Sqrt(full.C * full.C + full.D * full.D));
                            }
                            break;
                        case "Do":
                            // Every name given: with more than one, PdfPig draws the first and PDFium the last.
                            foreach (NameToken name in operands.OfType<NameToken>())
                            {
                                double inner = Form(doc, resources, name, s, depth, open);
                                if (double.IsNaN(inner)) return double.NaN;
                                drift = Math.Max(drift, inner);
                            }
                            break;
                    }
                    operands.Clear();
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return matters ? double.NaN : drift;
            }
            return drift;
        }

        /// <summary>A form XObject drawn with state <paramref name="s"/>: PdfPig keeps the rise, scaling and leading, PDFium starts at 0, 100 and 0.</summary>
        private static double Form(PigDocument doc, Scope? resources, NameToken name, State s, int depth, HashSet<IndirectReference> open)
        {
            bool matters = s.PigRise != 0 || s.PigTz != 100 || s.PigTL != 0;
            // Looked up like both libraries do: the form's own resources first, then the ones it was drawn with, up to the page's.
            IToken? entry = null;
            for (Scope? r = resources; r is not null && entry is null; r = r.Outer)
                if (Get(doc, r.Dict, "XObject") is DictionaryToken xobjects) xobjects.TryGet(name, out entry);
            if (entry is null) return matters ? double.NaN : 0;   // not found: where it is drawn, if anywhere, is unknown
            var reference = entry as IndirectReferenceToken;
            if (Resolve(doc, entry) is not StreamToken form || Get(doc, form.StreamDictionary, "Subtype") is not NameToken { Data: "Form" })
                return 0;   // an image, or nothing: no text
            if (depth >= MaxDepth || (reference is not null && open.Contains(reference.Data)))
                return matters ? double.NaN : 0;   // PdfPig does not draw past this either; refuse only when it could matter
            byte[] content;
            try
            {
                content = UglyToad.PdfPig.PdfExtensions.Decode(form, doc.Structure.FilterProvider, doc.Structure.TokenScanner).ToArray();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return matters ? double.NaN : 0;
            }
            Lin ctm = s.Ctm;
            if (Get(doc, form.StreamDictionary, "Matrix") is ArrayToken { Length: 6 } mt
                && mt.Data.Select(t => Resolve(doc, t)).OfType<NumericToken>().Select(t => t.Double).ToArray() is { Length: 6 } m)
                ctm = new Lin(m[0], m[1], m[2], m[3]).Times(ctm);
            Scope? own = Get(doc, form.StreamDictionary, "Resources") is DictionaryToken d ? new Scope(d, resources) : resources;
            if (reference is not null) open.Add(reference.Data);
            try
            {
                return Walk(doc, content, own, new State(s.PigRise, 0, ctm, s.PigTz, 100, s.PigTL, 0), depth + 1, open);
            }
            finally
            {
                if (reference is not null) open.Remove(reference.Data);
            }
        }

        private static double[]? Numbers(List<IToken> operands, int n)
        {
            if (operands.Count < n) return null;
            var r = new double[n];
            for (int i = 0; i < n; i++)
            {
                if (operands[operands.Count - n + i] is not NumericToken t) return null;
                r[i] = t.Double;
            }
            return r;
        }

        private static bool Has(byte[] data, string word) => data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(word)) >= 0;

        /// <summary>The page's content streams joined, or null when there are none.</summary>
        private static byte[]? PageContent(PigDocument doc, DictionaryToken page)
        {
            var streams = Get(doc, page, "Contents") switch
            {
                StreamToken one => new List<StreamToken> { one },
                ArrayToken many => many.Data.Select(t => Resolve(doc, t)).OfType<StreamToken>().ToList(),
                _ => [],
            };
            if (streams.Count == 0) return null;
            using var ms = new MemoryStream();
            foreach (var st in streams)
            {
                ms.Write(UglyToad.PdfPig.PdfExtensions.Decode(st, doc.Structure.FilterProvider, doc.Structure.TokenScanner).ToArray());
                ms.WriteByte((byte)'\n');
            }
            return ms.ToArray();
        }

        private static DictionaryToken? InheritedDict(PigDocument doc, DictionaryToken dict, NameToken key)
        {
            DictionaryToken? d = dict;
            for (int depth = 0; d is not null && depth < 64; depth++)
            {
                if (d.TryGet(key, out IToken? v)) return Resolve(doc, v) as DictionaryToken;
                d = d.TryGet(NameToken.Parent, out IToken? parent) ? Resolve(doc, parent) as DictionaryToken : null;
            }
            return null;
        }
    }

    /// <summary>
    /// Adds the box, padded and clipped to the drawn page. A match wholly outside the page is not drawn, so there is
    /// nothing to paint: it still counts as an item, only its box is left out (clipping it would give a negative size).
    /// </summary>
    private static void AddPadded(List<XRect> boxes, XRect r, double maxW, double maxH)
    {
        double x0 = Math.Max(0, r.X - PadPoints), y0 = Math.Max(0, r.Y - PadPoints);
        double x1 = Math.Min(maxW, r.X + r.Width + PadPoints), y1 = Math.Min(maxH, r.Y + r.Height + PadPoints);
        if (x1 > x0 && y1 > y0) boxes.Add(new XRect(x0, y0, x1 - x0, y1 - y0));
    }
}

/// <summary>
/// The content streams a page runs besides its own: forms it draws (Do), tiling patterns it paints with (scn), the
/// glyph procedures of Type 3 fonts it shows text with (Tf), the soft-mask groups of graphics states it sets (gs) and
/// the appearances of its notes, each with its own resources or the ones it was drawn with. PdfPig follows forms by
/// plain recursion: a form that draws itself through the resources it inherits, two that draw each other or a chain
/// of about a thousand overflows the stack, which no catch can stop, so the whole app ended (dropping such a file
/// onto Swivra was enough, through the radar). A tree whose shared parts are drawn twice on every level never
/// finishes and eats memory, in PdfPig and PDFium alike, and PDFium never finishes a pattern that paints with
/// itself, also when a glyph or a soft mask is what paints with it (the shared PDFium lock then stays taken).
/// This walks the tree from the dictionaries and a token scan of each stream, before PdfPig reads any page content,
/// and says what is wrong with it; every path that has PdfPig read page content checks it first.
/// A name is taken as used by an operator when it is any name operand given to it: with more operands than the
/// operator takes, PdfPig uses the first and PDFium the last. A page's streams are scanned joined, as both libraries
/// read them, so an operator split across two streams is still seen. Where a reader could take bytes for something
/// other than what the scanner made of them (inline image data, comments, a stream the scanner cannot finish),
/// every name in them counts as used by every operator: that only adds a stream to check, never hides one.
/// The walk also tells which pages show text or images inside a pattern, a glyph or a soft mask: PDFium draws them,
/// but neither text layer has their letters, so they cannot be searched.
/// The checker itself only recurses to a fixed depth, so no input can overflow its stack or keep it busy for long.
/// </summary>
internal static partial class PdfTree
{
    // Deeper nesting than this is not made by any real producer; PdfPig itself still reads a chain of 300 (measured).
    private const int MaxDepth = 64;
    // Page trees are balanced and a few levels deep, even for tens of thousands of pages.
    private const int MaxPageTreeDepth = 64;
    // Streams entered for one page, once for every place that draws them: far above any real page (a page that
    // draws the same 100 template forms takes 100), far below a tree whose shared parts double on every level.
    private const int MaxPageVisits = 100_000;
    // The same across the whole document, so a huge file cannot keep the check busy for minutes.
    private const long MaxDocVisits = 2_000_000;
    // Streams one page runs, counting every Do and scn: far above any real page, far below a tree that doubles per level.
    private const double MaxDraws = 1_000_000;

    /// <summary>What is wrong: drawings that draw themselves, nesting too deep, or a tree too large to check.</summary>
    public enum Fault { Loop, Deep, Large }

    public sealed class TreeException(Fault fault, string why) : Exception(why)
    {
        public Fault Fault { get; } = fault;
    }

    /// <summary>Throws <see cref="TreeException"/> when the document's content tree loops, is too deep or too large; otherwise the checked tree.</summary>
    public static Tree Check(UglyToad.PdfPig.PdfDocument doc)
    {
        var tree = new Tree(doc);
        if (tree.Walk() is var (fault, why)) throw new TreeException(fault, why);
        return tree;
    }

    /// <summary>What is wrong with the content tree, or null when PdfPig can safely read every page.</summary>
    public static string? Problem(UglyToad.PdfPig.PdfDocument doc) => new Tree(doc).Walk()?.Why;

    /// <summary>Resources in effect: a stream's own, then the ones it was drawn with (a lookup that misses goes outward).</summary>
    private sealed record Res(DictionaryToken Dict, Res? Outer);

    /// <summary>Where a stream is drawn: on the page, as a glyph of a Type 3 font, or inside a pattern or soft mask.</summary>
    private enum Ctx { Page, Glyph, Hidden }

    /// <summary>Names a stream hands to each operator and how many times; whether it shows text or inline images.</summary>
    private sealed class Uses
    {
        public readonly Dictionary<string, int> Do = [], Scn = [], Gs = [], Tf = [];
        public bool Text, Image, ImageNoMask;

        /// <summary>A name the scanner could not place: taken as used by every operator.</summary>
        public void Any(string name)
        {
            foreach (var map in new[] { Do, Scn, Gs, Tf }) map[name] = map.GetValueOrDefault(name) + 1;
        }
    }

    public sealed class Tree
    {
        private readonly UglyToad.PdfPig.PdfDocument doc;
        private readonly Dictionary<IndirectReference, Uses> uses = [];
        private readonly HashSet<IndirectReference> open = [];
        // Type 3 fonts and soft masks entered on the current page: shown again, they run nothing new.
        private readonly HashSet<IndirectReference> fontsDone = [], masksDone = [];
        // Type 3 fonts whose glyphs are being run right now: showing one of them again is a loop.
        private readonly HashSet<IndirectReference> fontsOpen = [];
        private int pageVisits;
        private long docVisits;
        private string? problem;
        private Fault fault;
        private bool hidden;

        internal Tree(UglyToad.PdfPig.PdfDocument doc) => this.doc = doc;

        internal (Fault Fault, string Why)? Walk()
        {
            IToken? root = doc.Structure.Catalog.CatalogDictionary.TryGet(NameToken.Pages, out IToken? p) ? p : null;
            Pages(root, null, 0, []);
            return problem is null ? null : (fault, problem);
        }

        /// <summary>
        /// True when the page shows text or images inside a pattern, a Type 3 glyph or a soft mask: drawn, but in no
        /// text layer. Also true when the page cannot be walked, so it is never called readable without a look.
        /// </summary>
        public bool HidesText(DictionaryToken page)
        {
            var chain = new List<DictionaryToken>();
            DictionaryToken? d = page;
            for (int depth = 0; d is not null && depth <= MaxPageTreeDepth; depth++)
            {
                if (Resolve(Get(d, "Resources")) is DictionaryToken r) chain.Add(r);
                d = Resolve(Get(d, "Parent")) as DictionaryToken;
            }
            Res? res = null;
            for (int i = chain.Count - 1; i >= 0; i--) res = new Res(chain[i], res);
            problem = null;
            docVisits = 0;
            open.Clear();
            Leaf(page, res);
            return hidden || problem is not null;
        }

        private void Fail(Fault f, string why)
        {
            if (problem is not null) return;
            problem = why;
            fault = f;
        }

        private void Pages(IToken? node, Res? res, int depth, HashSet<IndirectReference> path)
        {
            if (depth > MaxPageTreeDepth) { Fail(Fault.Deep, $"page tree nested deeper than {MaxPageTreeDepth} levels"); return; }
            if (++docVisits > MaxDocVisits) { Fail(Fault.Large, $"document has more than {MaxDocVisits} drawings to check"); return; }
            var reference = node as IndirectReferenceToken;
            if (reference is not null && !path.Add(reference.Data)) { Fail(Fault.Loop, $"page tree node {reference.Data} contains itself"); return; }
            try
            {
                if (Resolve(node) is not DictionaryToken dict) return;
                if (Resolve(Get(dict, "Resources")) is DictionaryToken own) res = new Res(own, res);
                if (Resolve(Get(dict, "Kids")) is ArrayToken kids)
                {
                    foreach (IToken kid in kids.Data)
                    {
                        Pages(kid, res, depth + 1, path);
                        if (problem is not null) return;
                    }
                    return;
                }
                Leaf(dict, res);
            }
            finally
            {
                if (reference is not null) path.Remove(reference.Data);
            }
        }

        /// <summary>One page: its content, then the appearances of its notes (drawn with it, and burned in by flattening).</summary>
        private void Leaf(DictionaryToken page, Res? res)
        {
            pageVisits = 0;
            hidden = false;
            fontsDone.Clear();
            fontsOpen.Clear();
            masksDone.Clear();
            double draws = Children(Scan(PageContent(page)), res, 0, Ctx.Page);
            if (problem is null && Resolve(Get(page, "Annots")) is ArrayToken annots)
            {
                foreach (IToken a in annots.Data)
                {
                    if (Resolve(a) is not DictionaryToken annot || Resolve(Get(annot, "AP")) is not DictionaryToken ap) continue;
                    foreach (string key in new[] { "N", "R", "D" })
                    {
                        IToken? look = Get(ap, key);
                        if (Resolve(look) is DictionaryToken states)
                            foreach (IToken state in states.Data.Values) draws += Enter(state, null, res, 0, Ctx.Page);
                        else if (look is not null)
                            draws += Enter(look, null, res, 0, Ctx.Page);
                        if (problem is not null) return;
                    }
                }
            }
            if (draws > MaxDraws) Fail(Fault.Large, $"a page draws more than {MaxDraws} streams");
        }

        /// <summary>
        /// Enters what a stream with these uses draws; <paramref name="depth"/> is the stream's own nesting. Returns
        /// how many streams that runs, counting every use: a part shared by two uses on each level runs twice per level.
        /// </summary>
        private double Children(Uses u, Res? res, int depth, Ctx ctx)
        {
            if (ctx != Ctx.Page && (u.Text || (ctx == Ctx.Glyph ? u.ImageNoMask : u.Image))) hidden = true;
            double draws = 0;
            foreach (var (n, times) in u.Do)
                if (Find(res, "XObject", n) is IToken t)
                {
                    draws += times * Enter(t, "Form", res, depth, ctx);
                    if (problem is not null) return 0;
                }
            foreach (var (n, times) in u.Scn)
                if (Find(res, "Pattern", n) is IToken t)
                {
                    draws += times * Enter(t, null, res, depth, Ctx.Hidden);
                    if (problem is not null) return 0;
                }
            foreach (var n in u.Gs.Keys)
            {
                // A soft mask: its group is drawn (into the mask) whenever the state is set.
                if (Resolve(Find(res, "ExtGState", n)) is not DictionaryToken gs || Resolve(Get(gs, "SMask")) is not DictionaryToken mask) continue;
                IToken? group = Get(mask, "G");
                // Entered again only when it is being drawn right now: that is a loop, and Enter says so.
                if (group is IndirectReferenceToken g && !open.Contains(g.Data) && !masksDone.Add(g.Data)) continue;
                if (group is not null) draws += Enter(group, null, res, depth, Ctx.Hidden);
                if (problem is not null) return 0;
            }
            foreach (var n in u.Tf.Keys)
            {
                // A Type 3 font: its glyphs are content streams, run with the font's resources or the ones in effect.
                IToken? f = Find(res, "Font", n);
                if (Resolve(f) is not DictionaryToken font || (Resolve(Get(font, "Subtype")) as NameToken)?.Data != "Type3") continue;
                var fontRef = (f as IndirectReferenceToken)?.Data;
                if (fontRef is IndirectReference open3 && fontsOpen.Contains(open3))
                {
                    Fail(Fault.Loop, $"Type 3 font {open3} draws itself");
                    return 0;
                }
                if (fontRef is IndirectReference fr && !fontsDone.Add(fr)) continue;
                if (Resolve(Get(font, "CharProcs")) is not DictionaryToken procs) continue;
                Res? inner = Resolve(Get(font, "Resources")) is DictionaryToken own ? new Res(own, res) : res;
                if (fontRef is IndirectReference fo) fontsOpen.Add(fo);
                try
                {
                    foreach (IToken proc in procs.Data.Values)
                    {
                        draws += Enter(proc, null, inner, depth, ctx == Ctx.Hidden ? Ctx.Hidden : Ctx.Glyph);
                        if (problem is not null) return 0;
                    }
                }
                finally
                {
                    if (fontRef is IndirectReference fc) fontsOpen.Remove(fc);
                }
            }
            return draws;
        }

        /// <summary>One stream drawn from depth <paramref name="depth"/>: a loop, too deep, too much work or too many draws is the problem.</summary>
        private double Enter(IToken token, string? subtype, Res? res, int depth, Ctx ctx)
        {
            if (Resolve(token) is not StreamToken st) return 0;
            if (subtype is not null)
            {
                string? actual = (Resolve(Get(st.StreamDictionary, "Subtype")) as NameToken)?.Data;
                if (actual == "Image")
                {
                    // An image drawn where no one looks for text; a glyph's stencil mask is just the glyph's shape.
                    if (ctx == Ctx.Hidden || (ctx == Ctx.Glyph && !IsMask(st))) hidden = true;
                    return 0;
                }
                if (actual != subtype) return 0;
            }
            var reference = token as IndirectReferenceToken;
            if (depth + 1 > MaxDepth) Fail(Fault.Deep, $"content nested deeper than {MaxDepth} levels");
            else if (++pageVisits > MaxPageVisits) Fail(Fault.Large, $"a page has more than {MaxPageVisits} drawings to check");
            else if (++docVisits > MaxDocVisits) Fail(Fault.Large, $"document has more than {MaxDocVisits} drawings to check");
            else if (reference is not null && !open.Add(reference.Data)) Fail(Fault.Loop, $"content stream {reference.Data} draws itself");
            if (problem is not null) return 0;
            try
            {
                Res? inner = Resolve(Get(st.StreamDictionary, "Resources")) is DictionaryToken own ? new Res(own, res) : res;
                double draws = 1 + Children(StreamUses(st, reference?.Data), inner, depth + 1, ctx);
                if (draws > MaxDraws) Fail(Fault.Large, $"a page draws more than {MaxDraws} streams");
                return draws;
            }
            finally
            {
                if (reference is not null) open.Remove(reference.Data);
            }
        }

        private bool IsMask(StreamToken image) =>
            Resolve(Get(image.StreamDictionary, "ImageMask")) is BooleanToken { Data: true };

        private Uses StreamUses(StreamToken st, IndirectReference? key)
        {
            if (key is IndirectReference k && uses.TryGetValue(k, out Uses? known)) return known;
            var u = Scan(Decode(st));
            if (key is IndirectReference k2) uses[k2] = u;
            return u;
        }

        private byte[] Decode(StreamToken st)
        {
            try
            {
                return UglyToad.PdfPig.PdfExtensions.Decode(st, doc.Structure.FilterProvider, doc.Structure.TokenScanner).ToArray();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return [];   // PdfPig cannot read it either: it draws nothing from it
            }
        }

        /// <summary>The page's content streams joined, as both libraries read them.</summary>
        private byte[] PageContent(DictionaryToken page)
        {
            var streams = Resolve(Get(page, "Contents")) switch
            {
                StreamToken one => [one],
                ArrayToken many => many.Data.Select(Resolve).OfType<StreamToken>().ToList(),
                _ => new List<StreamToken>(),
            };
            if (streams.Count == 1) return Decode(streams[0]);
            using var ms = new MemoryStream();
            foreach (var st in streams)
            {
                ms.Write(Decode(st));
                ms.WriteByte((byte)'\n');
            }
            return ms.ToArray();
        }

        private IToken? Find(Res? res, string category, string name)
        {
            var key = NameToken.Create(name);
            for (Res? r = res; r is not null; r = r.Outer)
                if (Resolve(Get(r.Dict, category)) is DictionaryToken d && d.TryGet(key, out IToken? t)) return t;
            return null;
        }

        private IToken? Resolve(IToken? token)
        {
            for (int i = 0; token is IndirectReferenceToken r && i < 16; i++)
                token = doc.Structure.GetObject(r.Data)?.Data;
            return token is IndirectReferenceToken ? null : token;
        }

        private static IToken? Get(DictionaryToken dict, string key) => dict.TryGet(NameToken.Create(key), out IToken? t) ? t : null;
    }

    /// <summary>The names each operator is given in a content stream, and whether it shows text or inline images.</summary>
    private static Uses Scan(byte[] data)
    {
        var u = new Uses();
        var names = new List<string>();   // name operands since the last operator
        bool mask = false;                // the inline image being read is a stencil mask (/IM true)
        string? lastName = null;
        try
        {
            var scanner = new CoreTokenScanner(new MemoryInputBytes(data), false, new StackDepthGuard(256), ScannerScope.None, null, true);
            while (scanner.MoveNext())
            {
                switch (scanner.CurrentToken)
                {
                    case NameToken n:
                        names.Add(n.Data);
                        lastName = n.Data;
                        continue;
                    case BooleanToken { Data: true } when lastName is "IM" or "ImageMask":
                        mask = true;
                        lastName = null;
                        continue;
                    case InlineImageDataToken img:
                        // The scanner reads BI .. ID .. EI as BI, the dictionary's tokens, then this one.
                        u.Image = true;
                        if (!mask) u.ImageNoMask = true;
                        AnyNames(Encoding.Latin1.GetString(img.Data.Span), u);
                        names.Clear();
                        mask = false;
                        lastName = null;
                        continue;
                    case CommentToken c:
                        AnyNames(c.Data, u);
                        continue;
                    case OperatorToken op:
                        switch (op.Data)
                        {
                            case "Do": foreach (string n in names) Add(u.Do, n); break;
                            case "scn" or "SCN": foreach (string n in names) Add(u.Scn, n); break;
                            case "gs": foreach (string n in names) Add(u.Gs, n); break;
                            case "Tf": foreach (string n in names) Add(u.Tf, n); break;
                            case "Tj" or "TJ" or "'" or "\"": u.Text = true; break;
                            case "BI": mask = false; break;
                        }
                        names.Clear();
                        lastName = null;
                        continue;
                    default:
                        lastName = null;
                        continue;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The scanner stopped where a reader may go on: every name in the stream counts.
            AnyNames(Encoding.Latin1.GetString(data), u);
            u.Text = u.Image = u.ImageNoMask = true;
        }
        return u;

        static void Add(Dictionary<string, int> map, string n) => map[n] = map.GetValueOrDefault(n) + 1;
    }

    private static void AnyNames(string text, Uses u)
    {
        foreach (Match m in AnyName().Matches(text))
        {
            string n = m.Groups[1].Value;
            if (n.Contains('#')) n = Regex.Replace(n, "#([0-9A-Fa-f]{2})", h => ((char)Convert.ToByte(h.Groups[1].Value, 16)).ToString());
            u.Any(n);
        }
    }

    [GeneratedRegex(@"/([^\x00\t\n\f\r ()<>\[\]{}/%]+)")]
    private static partial Regex AnyName();
}

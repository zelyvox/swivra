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
    /// <param name="UnreadPages">Pages with letters that have no Unicode value (Type 3 or broken fonts): drawn, but not searchable.</param>
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
        byte[]? flat = Pdfium.Flatten(original);

        var perPage = new List<PageBoxes>();
        int items = 0, imagePages = 0, unreadPages = 0, leftOutPages = 0, noLookPages = 0;
        using (var orig = PigDocument.Open(original))
        using (var copy = OpenCopy(flat, orig.NumberOfPages))
        {
            if (copy is null) flat = null;
            PigDocument drawn = flat is null ? orig : copy!;
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
                if (HasUnreadableLetters(page)) unreadPages++;
                if (page.GetImages().Any(CanHoldText)) imagePages++;
                perPage.Add(new PageBoxes(map.Width, map.Height, boxes));
            }
        }
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
            if (texts.Any(t => Radar.FindAll(t).Count > 0)) boxes.Add(Pad(map.Map(a.Rectangle), map.Width, map.Height));
        }
        return new NoteScan(count, visible, noLook);
    }

    /// <summary>A match as kind plus its letters and digits only, so the same number spelled with other separators is one item.</summary>
    private static string Key(string text, Radar.Hit h) =>
        h.Kind + ":" + new string(text.AsSpan(h.Index, h.Length).ToArray().Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>
    /// True when the page draws letters PdfPig cannot turn into text (no value, a control character, U+FFFD or a
    /// private-use code: Type 3 or embedded fonts without a Unicode map). They are drawn but cannot be searched.
    /// </summary>
    private static bool HasUnreadableLetters(Page page)
    {
        foreach (Letter l in page.Letters)
        {
            if (string.IsNullOrEmpty(l.Value)) return true;
            foreach (Rune r in l.Value.EnumerateRunes())
            {
                if (r.Value == 0xFFFD) return true;
                var cat = Rune.GetUnicodeCategory(r);
                if (cat == System.Globalization.UnicodeCategory.PrivateUse) return true;
                if (cat == System.Globalization.UnicodeCategory.Control && !Rune.IsWhiteSpace(r)) return true;
            }
        }
        return false;
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
                    if (run is XRect done) boxes.Add(Pad(done, map.Width, map.Height));
                    run = cur;
                }
            }
            if (run is XRect last) boxes.Add(Pad(last, map.Width, map.Height));
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
            DictionaryToken? resources = InheritedDict(doc, page.Dictionary, NameToken.Resources);
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

        private static double Walk(PigDocument doc, byte[] content, DictionaryToken? resources, State start, int depth, HashSet<IndirectReference> open)
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
                        case "Do" when operands.Count > 0 && operands[^1] is NameToken name:
                            double inner = Form(doc, resources, name, s, depth, open);
                            if (double.IsNaN(inner)) return double.NaN;
                            drift = Math.Max(drift, inner);
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
        private static double Form(PigDocument doc, DictionaryToken? resources, NameToken name, State s, int depth, HashSet<IndirectReference> open)
        {
            IToken? entry = null;
            if (resources is not null && Get(doc, resources, "XObject") is DictionaryToken xobjects) xobjects.TryGet(name, out entry);
            var reference = entry as IndirectReferenceToken;
            if (Resolve(doc, entry) is not StreamToken form || Get(doc, form.StreamDictionary, "Subtype") is not NameToken { Data: "Form" })
                return 0;   // an image, or nothing: no text
            bool matters = s.PigRise != 0 || s.PigTz != 100 || s.PigTL != 0;
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
            var own = Get(doc, form.StreamDictionary, "Resources") as DictionaryToken ?? resources;
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

    private static XRect Pad(XRect r, double maxW, double maxH)
    {
        double x0 = Math.Max(0, r.X - PadPoints), y0 = Math.Max(0, r.Y - PadPoints);
        double x1 = Math.Min(maxW, r.X + r.Width + PadPoints), y1 = Math.Min(maxH, r.Y + r.Height + PadPoints);
        return new XRect(x0, y0, x1 - x0, y1 - y0);
    }
}

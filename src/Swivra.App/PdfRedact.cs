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

namespace Swivra.App;

/// <summary>
/// Real redaction of what the radar finds. PdfPig locates the matches (letter boxes, note texts, form field values),
/// PDFium renders every page as it is displayed (crop, rotation, notes and filled-in fields included), opaque boxes are
/// painted into the pixels and the pages are written as images into a brand-new PDF (PDFsharp).
/// Nothing of the original text, metadata, annotations or form fields survives, so there is nothing to extract.
/// Embedded images are not read (no OCR): the outcome counts those pages so the user is told to check them.
/// A note whose text cannot be read (a stamp, ink or signature that is only a drawing) is left out of the output
/// and counted too: drawing it could show a number nobody searched.
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
    /// <param name="ImagePages">Pages with images large enough to hold text: they cannot be checked without OCR.</param>
    /// <param name="NotePages">Pages with notes whose text could not be read (stamp, ink, signature...): left out of the output.</param>
    public sealed record Outcome(int Items, int ImagePages, int NotePages);

    /// <summary>PDFium drew a page at a different size than the page box the boxes were measured in: painting would miss.</summary>
    public sealed class AlignmentException(int page) : Exception($"rendered size of page {page} does not match its page box")
    {
        public int Page { get; } = page;
    }

    /// <summary>Everything needed to paint one page: displayed size in points, the boxes to black out and the unread notes to leave out (origin top-left).</summary>
    private sealed record PageBoxes(double W, double H, List<XRect> Boxes, List<XRect> LeaveOut);

    /// <summary>
    /// Finds matches on every page; when there are any, writes the redacted image-only PDF to <paramref name="target"/>.
    /// With no matches nothing is written and Items is 0.
    /// </summary>
    public static Outcome Hide(string input, string target)
    {
        byte[] bytes = SafeFile.ReadAll(input);

        var perPage = new List<PageBoxes>();
        int items = 0, imagePages = 0, notePages = 0;
        using (var doc = PigDocument.Open(bytes))
        {
            foreach (Page page in doc.GetPages())
            {
                var map = PageMap.For(doc, page);
                var (boxes, count) = Locate(page, map);
                var leaveOut = new List<XRect>();
                int noteCount = LocateNotes(doc, page, map, boxes, leaveOut);
                items += count + noteCount;
                if (leaveOut.Count > 0) notePages++;
                if (page.GetImages().Any(CanHoldText)) imagePages++;
                perPage.Add(new PageBoxes(map.Width, map.Height, boxes, leaveOut));
            }
        }
        if (items == 0) return new Outcome(0, imagePages, notePages);

        using var output = new PdfSharp.Pdf.PdfDocument();
        output.Info.Creator = "Swivra";
        // Notes and filled-in fields are drawn: they were searched above, and dropping them would lose real content.
        var withNotes = new PDFtoImage.RenderOptions(Dpi: Dpi, WithAnnotations: true, WithFormFill: true);
        var pageOnly = new PDFtoImage.RenderOptions(Dpi: Dpi);
        for (int i = 0; i < perPage.Count; i++)
        {
            var pb = perPage[i];
            using SKBitmap bmp = Render(bytes, i, withNotes, pb);
            double sx = bmp.Width / pb.W, sy = bmp.Height / pb.H;
            SKRect Px(XRect r) => SKRect.Create((float)(r.X * sx), (float)(r.Y * sy), (float)(r.Width * sx), (float)(r.Height * sy));
            using (var canvas = new SKCanvas(bmp))
            using (var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false })
            {
                if (pb.LeaveOut.Count > 0)
                {
                    // Unread notes: their area comes from a render without any notes, as if they were not there.
                    using SKBitmap plain = Render(bytes, i, pageOnly, pb);
                    using SKImage plainImage = SKImage.FromBitmap(plain);
                    foreach (XRect r in pb.LeaveOut)
                    {
                        canvas.Save();
                        canvas.ClipRect(Px(r));
                        canvas.DrawImage(plainImage, 0, 0, SKSamplingOptions.Default);
                        canvas.Restore();
                    }
                }
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
        return new Outcome(items, imagePages, notePages);
    }

    /// <summary>
    /// One page as PDFium draws it: its crop area with /Rotate applied, where the boxes were measured. If the size
    /// disagrees, PDFium saw another page box and every box would land in the wrong place, so the work stops.
    /// </summary>
    private static SKBitmap Render(byte[] bytes, int index, PDFtoImage.RenderOptions options, PageBoxes pb)
    {
        using var ms = new MemoryStream(bytes);
        SKBitmap bmp = PDFtoImage.Conversion.ToImage(ms, index, options: options);
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

    // Notes whose look comes only from their own text (searched below) or carries no text at all.
    private static readonly HashSet<AnnotationType> TextOrPlainNotes = new()
    {
        AnnotationType.Text, AnnotationType.FreeText, AnnotationType.Link, AnnotationType.Popup, AnnotationType.Widget,
        AnnotationType.Highlight, AnnotationType.Underline, AnnotationType.Squiggly, AnnotationType.StrikeOut,
    };

    /// <summary>
    /// Searches the notes and form fields of a page: note text (/Contents, rich text /RC) and field values (/V, inherited from
    /// parent fields, list options, button captions). A note or field with a match is blacked out whole and counts its matches.
    /// A visible note drawn from an appearance whose text is not known (stamp, ink, shape, signature) goes to
    /// <paramref name="leaveOut"/>: it is not drawn, since what it shows was never searched.
    /// </summary>
    private static int LocateNotes(PigDocument doc, Page page, PageMap map, List<XRect> boxes, List<XRect> leaveOut)
    {
        int count = 0;
        foreach (Annotation a in page.GetAnnotations())
        {
            var dict = a.AnnotationDictionary;
            var texts = new List<string>();
            bool unread = false;
            if (!string.IsNullOrEmpty(a.Content)) texts.Add(a.Content);
            if (TextOf(doc, Get(doc, dict, "RC"), out string? rich)) texts.Add(StripMarkup(rich!));
            else if (Get(doc, dict, "RC") is not null) unread = true;

            bool signature = false;
            if (a.Type == AnnotationType.Widget)
            {
                signature = Inherited(doc, dict, "FT") is NameToken { Data: "Sig" };
                foreach (string key in new[] { "V", "RV", "Opt" })
                {
                    var value = Inherited(doc, dict, key);
                    if (value is null) continue;
                    if (TextOf(doc, value, out string? v)) texts.Add(key == "RV" ? StripMarkup(v!) : v!);
                    else unread = true;
                }
                if (Resolve(doc, Get(doc, dict, "MK")) is DictionaryToken mk)
                    foreach (string key in new[] { "CA", "RC", "AC" })
                        if (TextOf(doc, Get(doc, mk, key), out string? caption)) texts.Add(caption!);
            }

            int hits = texts.Sum(t => Radar.FindAll(t).Count);
            count += hits;
            if ((a.Flags & (AnnotationFlags.Hidden | AnnotationFlags.NoView)) != 0) continue;   // not drawn
            if (a.HasNormalAppearance && (signature || !TextOrPlainNotes.Contains(a.Type))) unread = true;
            if (hits == 0 && !unread) continue;
            // A NoRotate / NoZoom note is drawn from its corner at its own size and angle, not stretched over its
            // rectangle: on a turned or scaled page it can stick out, so its area is widened by its size each way.
            XRect r = map.Map(a.Rectangle);
            if ((a.Flags & (AnnotationFlags.NoRotate | AnnotationFlags.NoZoom)) != 0)
            {
                double grow = Math.Max(r.Width, r.Height);
                r = new XRect(r.X - grow, r.Y - grow, r.Width + 2 * grow, r.Height + 2 * grow);
            }
            r = Pad(r, map.Width, map.Height);
            if (hits > 0) boxes.Add(r);
            else leaveOut.Add(r);
        }
        return count;
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
    private static (List<XRect> Boxes, int Count) Locate(Page page, PageMap map)
    {
        var rects = new List<PdfRectangle?>();   // one entry per character of the text; null for separators
        string text = PageText(page, rects);

        var boxes = new List<XRect>();
        var hits = Radar.FindAll(text);
        foreach (var hit in hits)
        {
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
        return (boxes, hits.Count);
    }

    /// <summary>Page text rebuilt from the letters in drawing order (also used for the text-only Word export of rotated pages); <paramref name="rects"/> gets one box per character.</summary>
    internal static string PageText(Page page, List<PdfRectangle?>? rects = null)
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

    private static XRect Pad(XRect r, double maxW, double maxH)
    {
        double x0 = Math.Max(0, r.X - PadPoints), y0 = Math.Max(0, r.Y - PadPoints);
        double x1 = Math.Min(maxW, r.X + r.Width + PadPoints), y1 = Math.Min(maxH, r.Y + r.Height + PadPoints);
        return new XRect(x0, y0, x1 - x0, y1 - y0);
    }
}

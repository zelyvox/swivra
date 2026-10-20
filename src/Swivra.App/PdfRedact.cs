using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using System.Text;
using UglyToad.PdfPig.Content;

namespace Swivra.App;

/// <summary>
/// Real redaction of what the radar finds. PdfPig locates the matches (letter boxes), PDFium renders every page,
/// opaque boxes are painted into the pixels and the pages are written as images into a brand-new PDF (PDFsharp).
/// Nothing of the original text, metadata, annotations or form fields survives, so there is nothing to extract.
/// Embedded images are not read (no OCR): sensitive data inside a scan is NOT found.
/// </summary>
internal static class PdfRedact
{
    private const int Dpi = 200;
    private const int JpegQuality = 90;
    private const double PadPoints = 1.5;   // box padding around the glyph boxes, in PDF points

    /// <param name="Items">Number of matches covered.</param>
    /// <param name="ScanPages">Pages that have no text but contain images: they cannot be checked without OCR.</param>
    public sealed record Outcome(int Items, int ScanPages);

    /// <summary>Everything needed to paint one page: displayed size in points and the boxes (origin top-left), plus the visible (crop) area.</summary>
    private sealed record PageBoxes(double W, double H, List<XRect> Boxes, XRect Visible);

    /// <summary>
    /// Finds matches on every page; when there are any, writes the redacted image-only PDF to <paramref name="target"/>.
    /// With no matches nothing is written and Items is 0.
    /// </summary>
    public static Outcome Hide(string input, string target)
    {
        byte[] bytes = File.ReadAllBytes(input);
        var crops = StripCropBoxes(ref bytes);   // PdfPig and PDFium disagree on CropBox offsets: both see the full MediaBox, we crop the pixels ourselves

        var perPage = new List<PageBoxes>();
        int items = 0, scanPages = 0;
        using (var doc = UglyToad.PdfPig.PdfDocument.Open(bytes))
        {
            foreach (Page page in doc.GetPages())
            {
                var (boxes, count) = Locate(page);
                items += count;
                if (page.Letters.Count == 0 && page.GetImages().Any()) scanPages++;
                double w = page.Width, h = page.Height;
                XRect visible = crops.TryGetValue(page.Number - 1, out var c) ? DisplayCrop(c, page.Rotation.Value, w, h) : new XRect(0, 0, w, h);
                perPage.Add(new PageBoxes(w, h, boxes, visible));
            }
        }
        if (items == 0) return new Outcome(0, scanPages);

        using var output = new PdfDocument();
        output.Info.Creator = "Swivra";
        var options = new PDFtoImage.RenderOptions(Dpi: Dpi);
        for (int i = 0; i < perPage.Count; i++)
        {
            using var ms = new MemoryStream(bytes);
            using SKBitmap full = PDFtoImage.Conversion.ToImage(ms, i, options: options);
            var pb = perPage[i];
            double sx = full.Width / pb.W, sy = full.Height / pb.H;
            using (var canvas = new SKCanvas(full))
            using (var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill, IsAntialias = false })
            {
                foreach (XRect b in pb.Boxes)
                    canvas.DrawRect(SKRect.Create((float)(b.X * sx), (float)(b.Y * sy), (float)(b.Width * sx), (float)(b.Height * sy)), paint);
            }

            var area = new SKRectI(
                Math.Max(0, (int)Math.Round(pb.Visible.X * sx)), Math.Max(0, (int)Math.Round(pb.Visible.Y * sy)),
                Math.Min(full.Width, (int)Math.Round((pb.Visible.X + pb.Visible.Width) * sx)), Math.Min(full.Height, (int)Math.Round((pb.Visible.Y + pb.Visible.Height) * sy)));
            using var bmp = new SKBitmap();
            if (!full.ExtractSubset(bmp, area)) throw new InvalidOperationException("crop area outside the page");
            using var image = SKImage.FromBitmap(bmp);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            using var jpg = new MemoryStream(data.ToArray());

            PdfPage outPage = output.AddPage();
            outPage.Width = XUnit.FromPoint(pb.Visible.Width);
            outPage.Height = XUnit.FromPoint(pb.Visible.Height);
            using var gfx = XGraphics.FromPdfPage(outPage);
            using XImage xi = XImage.FromStream(jpg);
            gfx.DrawImage(xi, 0, 0, pb.Visible.Width, pb.Visible.Height);
        }
        output.Save(target);
        return new Outcome(items, scanPages);
    }

    /// <summary>
    /// When a page has a CropBox different from its MediaBox, returns the box (user space, 0-based page index) and
    /// replaces <paramref name="bytes"/> by a copy without those CropBoxes. Files without crops are left untouched.
    /// </summary>
    private static Dictionary<int, (double X0, double Y0, double X1, double Y1, double MX, double MY, double MW, double MH)> StripCropBoxes(ref byte[] bytes)
    {
        var result = new Dictionary<int, (double, double, double, double, double, double, double, double)>();
        bool any;
        using (var doc = UglyToad.PdfPig.PdfDocument.Open(bytes))
            any = doc.GetPages().Any(p => p.CropBox.Bounds.Left != p.MediaBox.Bounds.Left || p.CropBox.Bounds.Bottom != p.MediaBox.Bounds.Bottom
                                          || p.CropBox.Bounds.Width != p.MediaBox.Bounds.Width || p.CropBox.Bounds.Height != p.MediaBox.Bounds.Height);
        if (!any) return result;

        using var ms = new MemoryStream(bytes);
        using var pdf = PdfReader.Open(ms, PdfDocumentOpenMode.Modify);
        for (int i = 0; i < pdf.PageCount; i++)
        {
            var page = pdf.Pages[i];
            if (!page.Elements.ContainsKey("/CropBox")) continue;
            var c = page.CropBox; var m = page.MediaBox;
            result[i] = (c.X1, c.Y1, c.X2, c.Y2, m.X1, m.Y1, m.Width, m.Height);
            page.Elements.Remove("/CropBox");
        }
        using var outMs = new MemoryStream();
        pdf.Save(outMs, false);
        bytes = outMs.ToArray();
        return result;
    }

    /// <summary>The crop rectangle in the displayed page (points, origin top-left, /Rotate applied).</summary>
    private static XRect DisplayCrop((double X0, double Y0, double X1, double Y1, double MX, double MY, double MW, double MH) c, int rotation, double dispW, double dispH)
    {
        int rot = ((rotation % 360) + 360) % 360;
        (double X, double Y) Map(double x, double y)
        {
            double u = x - c.MX, v = c.MY + c.MH - y;
            return rot switch { 90 => (c.MH - v, u), 180 => (c.MW - u, c.MH - v), 270 => (v, c.MW - u), _ => (u, v) };
        }
        var a = Map(c.X0, c.Y0); var b = Map(c.X1, c.Y1);
        double x0 = Math.Max(0, Math.Min(a.X, b.X)), y0 = Math.Max(0, Math.Min(a.Y, b.Y));
        double x1 = Math.Min(dispW, Math.Max(a.X, b.X)), y1 = Math.Min(dispH, Math.Max(a.Y, b.Y));
        return new XRect(x0, y0, x1 - x0, y1 - y0);
    }

    /// <summary>
    /// Boxes to paint for one page, in the displayed page (points, origin top-left); PdfPig already applies /Rotate.
    /// The text is rebuilt from the letters in drawing order (a space wherever the letters are not neighbours),
    /// so it works for rotated pages and for IBANs or phone numbers written with spaces; each matched character maps back to its letter box.
    /// </summary>
    private static (List<XRect> Boxes, int Count) Locate(Page page)
    {
        double dispW = page.Width, dispH = page.Height;
        var rects = new List<UglyToad.PdfPig.Core.PdfRectangle?>();   // one entry per character of the text; null for separators
        string text = PageText(page, rects);

        var boxes = new List<XRect>();
        var hits = Radar.FindAll(text);
        foreach (var hit in hits)
        {
            XRect? run = null;
            for (int i = hit.Index; i < hit.Index + hit.Length; i++)
            {
                if (rects[i] is not UglyToad.PdfPig.Core.PdfRectangle r) continue;
                var cur = new XRect(Math.Min(r.Left, r.Right), dispH - Math.Max(r.Top, r.Bottom), Math.Abs(r.Right - r.Left), Math.Abs(r.Top - r.Bottom));
                if (run is XRect p && Overlaps(p, cur)) run = Union(p, cur);
                else
                {
                    if (run is XRect done) boxes.Add(Pad(done, dispW, dispH));
                    run = cur;
                }
            }
            if (run is XRect last) boxes.Add(Pad(last, dispW, dispH));
        }
        return (boxes, hits.Count);
    }

    /// <summary>Page text rebuilt from the letters in drawing order (also used for the text-only Word export of rotated pages); <paramref name="rects"/> gets one box per character.</summary>
    internal static string PageText(Page page, List<UglyToad.PdfPig.Core.PdfRectangle?>? rects = null)
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
    private const double Descent = 0.3, Ascent = 1.05;

    /// <summary>
    /// The area a letter can paint, axis-aligned: its glyph bounding box joined with a box spanned along the baseline
    /// from the font size. The glyph box alone depends on font metrics and can miss accents or come out empty.
    /// </summary>
    private static UglyToad.PdfPig.Core.PdfRectangle CoverBox(Letter l)
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

        double size = l.PointSize > 0 ? l.PointSize : Math.Max(g.Height, 1);
        var (sx, sy, ex, ey) = (l.StartBaseLine.X, l.StartBaseLine.Y, l.EndBaseLine.X, l.EndBaseLine.Y);
        double dx = ex - sx, dy = ey - sy, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) (dx, dy, len) = (1, 0, 1);   // zero-width letter: assume horizontal text
        double ux = -dy / len, uy = dx / len;          // "up", perpendicular to the baseline
        foreach (var (bx, by) in new[] { (sx, sy), (ex, ey) })
            foreach (double k in new[] { -Descent * size, Ascent * size })
                Add(bx + ux * k, by + uy * k);

        return new UglyToad.PdfPig.Core.PdfRectangle(xs.Min(), ys.Min(), xs.Max(), ys.Max());
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

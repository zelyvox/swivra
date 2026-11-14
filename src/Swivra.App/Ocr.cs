using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using RapidOcrNet;
using SkiaSharp;

namespace Swivra.App;

/// <summary>
/// Offline OCR (RapidOcrNet with the PP-OCRv5 latin models in models/v5 next to the exe) and the search for
/// sensitive data in the text it reads.
/// <para>
/// <see cref="Read(Bitmap, TimeSpan, CancellationToken, int)"/> returns the lines of an image in reading order, each with
/// its words and their four-corner quads in the pixel coordinates of the image passed in. <c>null</c> means "not read"
/// (models missing, error, budget spent, cancelled); a page with no lines means "read, and there is no text". A partial
/// result is never returned: a page that ran out of time could hide exactly the line that mattered.
/// </para>
/// <para>
/// <see cref="FindSensitive"/> runs Radar's patterns over that text, raw and with OCR's letter-for-digit slips fixed,
/// and returns per match its kind, text and the polygons to paint over.
/// </para>
/// <para>
/// Nothing is loaded until the first read (the app can sit in the tray for days without needing it). One engine is shared
/// and calls run one at a time. No method throws, except <see cref="OutOfMemoryException"/>; failures go to the log,
/// which never gets recognised text or found values: they are personal data.
/// </para>
/// </summary>
internal static class Ocr
{
    /// <summary>A word: its text, where it starts in <see cref="Line.Text"/>, and its quad (4 corners, image pixels).</summary>
    public sealed record Word(string Text, int Start, PointF[] Quad);

    /// <summary>A line of text with its quad and its words in text order.</summary>
    public sealed record Line(string Text, PointF[] Quad, IReadOnlyList<Word> Words);

    /// <summary>What was read from one image; Width and Height are those of the image passed in.</summary>
    public sealed record Page(int Width, int Height, IReadOnlyList<Line> Lines)
    {
        /// <summary>The lines joined with '\n', in reading order: the text <see cref="FindSensitive"/> searches.</summary>
        public string Text => string.Join('\n', Lines.Select(l => l.Text));
    }

    /// <summary>
    /// One sensitive value. <paramref name="Text"/> is the matched text (with digit look-alikes fixed when only the fixed
    /// text matched). <paramref name="Region"/> holds one convex polygon per line the value is on, in image pixels: the
    /// outline of the words it touches, grown by a fifth of the line height (at least 3 px) and clipped to the image.
    /// </summary>
    public sealed record Finding(Radar.HitKind Kind, string Text, IReadOnlyList<PointF[]> Region);

    /// <summary>Reads a GDI+ bitmap (as <see cref="ImageIO.Load(string, out PropertyItem[])"/> returns it).</summary>
    /// <param name="budget">Time for the whole call, waiting for another read included; <see cref="Timeout.InfiniteTimeSpan"/> for none.</param>
    /// <param name="maxPixels">0 reads at full resolution, so text from about 12 px high is read whatever the image size.
    /// Above 0 the image is first scaled down to at most that many pixels: faster, but small text may be lost.</param>
    public static Page? Read(Bitmap image, TimeSpan budget, CancellationToken cancel = default, int maxPixels = 0)
    {
        BitmapData? data = null;
        try
        {
            // Skia reads GDI+'s pixels in place; the only copy made is the opaque (and maybe smaller) one that is read.
            data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            using var pixels = new SKPixmap(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul), data.Scan0, data.Stride);
            return Read(pixels, budget, cancel, maxPixels);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Write($"ocr failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            if (data is not null) image.UnlockBits(data);
        }
    }

    /// <summary>Reads a Skia bitmap (a rendered PDF page, for example). Same contract as the <see cref="Bitmap"/> overload.</summary>
    public static Page? Read(SKBitmap image, TimeSpan budget, CancellationToken cancel = default, int maxPixels = 0)
    {
        try
        {
            using var pixels = image.PeekPixels() ?? throw new InvalidOperationException("bitmap has no pixels");
            return Read(pixels, budget, cancel, maxPixels);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Write($"ocr failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static Page? Read(SKPixmap image, TimeSpan budget, CancellationToken cancel, int maxPixels)
    {
        var clock = Stopwatch.StartNew();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        if (budget != Timeout.InfiniteTimeSpan && budget.TotalMilliseconds < int.MaxValue) // longer than CancelAfter takes: no limit
            limit.CancelAfter(budget < TimeSpan.Zero ? TimeSpan.Zero : budget);
        bool entered = false;
        try
        {
            Gate.Wait(limit.Token);
            entered = true;
            var engine = Engine();
            if (engine is null) return null;
            float scale = 1;
            if (maxPixels > 0 && (long)image.Width * image.Height > maxPixels)
                scale = (float)Math.Sqrt((double)maxPixels / ((long)image.Width * image.Height));
            List<Line> lines;
            using (var work = Opaque(image, scale))
                lines = Run(engine, work, limit.Token);
            if (scale != 1) lines = lines.Select(l => Map(l, 0, 0, 1 / scale)).ToList();
            var page = new Page(image.Width, image.Height, ReadingOrder(lines));
            Log.Write($"ocr {image.Width}x{image.Height} scale={scale:0.###} lines={page.Lines.Count} in {clock.ElapsedMilliseconds} ms");
            return page;
        }
        catch (OperationCanceledException)
        {
            Log.Write(cancel.IsCancellationRequested
                ? $"ocr cancelled after {clock.ElapsedMilliseconds} ms"
                : $"ocr over budget ({budget.TotalMilliseconds:0} ms) on {image.Width}x{image.Height}: not read");
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Write($"ocr failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            if (entered)
            {
                IdleTimer.Change(IdleUnload, Timeout.InfiniteTimeSpan); // each read pushes the unload back
                Gate.Release();
            }
        }
    }

    // ---------- engine ----------

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static RapidOcr? _engine;
    private static bool _unavailable; // models missing or would not load: said once in the log, not retried in this run

    // The app lives in the tray for days and reads only now and then. The models, their sessions and what a large page
    // leaves behind stay around until this long after the last read; loading them again takes about 0.15 s.
    private static readonly TimeSpan IdleUnload = TimeSpan.FromMinutes(2);
    private static readonly System.Threading.Timer IdleTimer = new(_ => Unload(), null, Timeout.Infinite, Timeout.Infinite);

    /// <summary>Lets go of the engine if no read is running (the next read loads it again); a running read keeps it.</summary>
    internal static void Unload()
    {
        if (!Gate.Wait(0)) return; // busy: that read reschedules the unload when it ends
        try
        {
            if (_engine is null) return;
            _engine.Dispose();
            _engine = null;
            Log.Write("ocr engine unloaded after idle");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Write($"ocr unload failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The shared engine, loaded on first use. Only called inside <see cref="Gate"/>.</summary>
    private static RapidOcr? Engine()
    {
        if (_engine is not null || _unavailable) return _engine;
        // The package names the files relative to the working directory, which for a tray app started at sign-in is not
        // the exe's folder: anchor them there.
        var set = RapidOcrModelSet.PPOCRv5Latin;
        set = set with
        {
            DetModelPath = Path.Combine(AppContext.BaseDirectory, set.DetModelPath),
            ClsModelPath = Path.Combine(AppContext.BaseDirectory, set.ClsModelPath),
            RecModelPath = Path.Combine(AppContext.BaseDirectory, set.RecModelPath),
            KeysPath = Path.Combine(AppContext.BaseDirectory, set.KeysPath),
        };
        string? missing = new[] { set.DetModelPath, set.ClsModelPath, set.RecModelPath, set.KeysPath }.FirstOrDefault(p => !File.Exists(p));
        if (missing is not null)
        {
            _unavailable = true;
            Log.Write($"ocr unavailable: model file missing {missing}");
            return null;
        }
        var engine = new RapidOcr();
        try
        {
            // Every core makes a read 2-5x slower on a busy machine than four; and without the arena ONNX Runtime gives the
            // memory of a large page back after the run instead of keeping its peak for the rest of the day (about half
            // the peak, for some 20% more time).
            using var options = RapidOcr.GetDefaultSessionOptions(Math.Min(4, Environment.ProcessorCount));
            options.EnableCpuMemArena = false;
            options.EnableMemoryPattern = false;
            options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR;
            engine.InitModels(set, options);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            engine.Dispose();
            _unavailable = true;
            Log.Write($"ocr unavailable: models would not load: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        return _engine = engine;
    }

    // ---------- reading ----------

    // Detection memory grows with the area it sees (about 250 MB a megapixel without the arena, measured) and the
    // default 1024 px cap loses small text on big images: a 4000x3000 photo shrinks 4x and 12 px text becomes 3 px.
    // So a large image is read at full size in tiles of about a megapixel, which keeps the peak near a small image's.
    private const int TileArea = 1280 * 1024;
    private const int MaxTileSide = 2600;  // an A4 page at 300 DPI (2480 px) in full-width strips: no line is cut sideways
    private const int MinTileSide = 384;
    private const int Overlap = 96;        // a line up to this tall lies whole inside some tile
    private const int EdgeMargin = 2;      // a box this close to a tile edge that is not the image edge was cut
    private const int MaxCropArea = 2 * 1024 * 1024;
    private const int CropPadding = 16;    // a crop already has room around its line; the default 50 px border would double its area

    private static RapidOcrOptions Options(int longSide, int padding = -1)
    {
        var o = RapidOcrOptions.Default with
        {
            ReturnWordBox = true,
            ImgResize = Math.Max(1024, longSide), // never below the default, never shrink what was sized for reading
            MaxSideLen = 1 << 14,
        };
        return padding >= 0 ? o with { Padding = padding } : o;
    }

    private static List<Line> Run(RapidOcr engine, SKBitmap work, CancellationToken cancel)
    {
        int w = work.Width, h = work.Height;
        if ((long)w * h <= TileArea && Math.Max(w, h) <= 2048)
            return Detect(engine, work, 0, 0, cancel);
        // Tiles are wide strips, which cut every line of a page turned on its side, and the detector breaks such cut
        // vertical lines into loose pieces (measured on an A4 page at 300 DPI turned 90°: 53 pieces for 33 lines, three
        // values lost). Such a page is turned upright first, the way its text reads; the rest is read as usual.
        int turn = Sideways(engine, work, cancel, out float skew);
        if (turn == 0 && skew != 0) return RunStraightened(engine, work, skew, cancel);
        if (turn == 0) return RunTiled(engine, work, cancel);
        using var turned = new SKBitmap(new SKImageInfo(h, w, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(turned))
        {
            if (turn > 0) { canvas.Translate(h, 0); canvas.RotateDegrees(90); }  // (x, y) -> (h - y, x)
            else { canvas.Translate(0, w); canvas.RotateDegrees(-90); }          // (x, y) -> (y, w - x)
            canvas.DrawBitmap(work, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null); // quarter turns: pixel for pixel
        }
        PointF[] Back(PointF[] q) => q.Select(p => turn > 0 ? new PointF(p.Y, h - p.X) : new PointF(w - p.Y, p.X)).ToArray();
        return RunTiled(engine, turned, cancel)
            .Select(l => new Line(l.Text, Back(l.Quad), l.Words.Select(x => x with { Quad = Back(x.Quad) }).ToList())).ToList();
    }

    // A page whose long lines run so askew that they no longer fit inside the strip two tiles share (their rise across
    // the line plus their height is over Overlap) is straightened before it is cut into tiles: its lines then lie in one
    // strip instead of crossing several. Measured on a 12 MP photo of a page at 15°: 39 s read askew (nearly every line
    // cut and read again from its own crop), 15 s straightened; at 1° a value on a cut line was lost askew and is found
    // straightened. Lines that fit are read as they lie: turning resamples the pixels, and on a small photo 4° askew
    // that cost a digit of a phone number.
    private const float MinSkew = 0.5f;

    /// <summary>
    /// How to turn an image whose text mostly runs up or down it: +1 a quarter clockwise (text reads bottom to top),
    /// -1 anticlockwise (top to bottom), 0 leave it. Judged from a quick read of a small copy: the recogniser turns a tall
    /// crop one fixed way and the angle classifier says whether that left it upside down, which tells the reading direction.
    /// <paramref name="skew"/> is the angle (degrees, clockwise on screen) to straighten an upright page by: the
    /// length-weighted median direction of the clearly elongated boxes; 0 when there are none, when it is under
    /// <see cref="MinSkew"/>, or when its long lines fit inside the tile overlap as they lie.
    /// </summary>
    private static int Sideways(RapidOcr engine, SKBitmap work, CancellationToken cancel, out float skew)
    {
        skew = 0;
        float k = 1024f / Math.Max(work.Width, work.Height);
        using var small = work.Resize(new SKImageInfo(Math.Max(1, (int)(work.Width * k)), Math.Max(1, (int)(work.Height * k)), SKColorType.Bgra8888, SKAlphaType.Opaque),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (small is null) return 0;
        // boxes alone first: an upright page, the usual case, then costs one small detection and no recognition
        double across = 0, tall = 0;
        var angles = new List<(float Angle, float Length, float Thick)>();
        foreach (var box in engine.DetectBoxes(small, Options(1024), cancel))
        {
            var q = Points(box.BoxPoints, 0, 0);
            var b = Bounds(q);
            if (b.Width > 1.5f * b.Height) across += b.Width;
            else if (b.Height > 1.5f * b.Width) tall += b.Height;
            if (q.Length != 4) continue;
            float s01 = Dist(q[0], q[1]), s12 = Dist(q[1], q[2]);
            var (p, r) = s01 >= s12 ? (q[0], q[1]) : (q[1], q[2]);
            if (Math.Max(s01, s12) < 3 * Math.Max(1, Math.Min(s01, s12))) continue; // too short to tell a direction
            float a = MathF.Atan2(r.Y - p.Y, r.X - p.X) * 180 / MathF.PI;
            if (a > 90) a -= 180; else if (a <= -90) a += 180;
            if (Math.Abs(a) < 45) angles.Add((a, Math.Max(s01, s12), Math.Min(s01, s12)));
        }
        if (tall <= 1.5 * across)
        {
            if (angles.Count > 0)
            {
                float Median(Func<(float Angle, float Length, float Thick), float> by)
                {
                    float half = angles.Sum(x => x.Length) / 2, run = 0;
                    foreach (var x in angles.OrderBy(by))
                        if ((run += x.Length) >= half) return by(x);
                    return by(angles[^1]);
                }
                float angle = Median(x => x.Angle), length = Median(x => x.Length) / k, thick = Median(x => x.Thick) / k;
                if (Math.Abs(angle) >= MinSkew && length * Math.Abs(MathF.Sin(angle * MathF.PI / 180)) + thick > Overlap) skew = angle;
            }
            return 0;
        }
        double up = 0, down = 0;
        foreach (var block in engine.Detect(small, Options(1024), cancel).TextBlocks)
        {
            var b = Bounds(Points(block.BoxPoints, 0, 0));
            if (b.Height <= 1.5f * b.Width) continue;
            if (block.AngleIndex == 1) down += b.Height; // measured: top-to-bottom text comes out flipped
            else up += b.Height;
        }
        return down > up ? -1 : 1;
    }

    /// <summary>
    /// Reads a page askew by <paramref name="skew"/> degrees: turned back onto a white sheet large enough to hold it,
    /// read in tiles, and the quads mapped back onto the image as it was.
    /// </summary>
    private static List<Line> RunStraightened(RapidOcr engine, SKBitmap work, float skew, CancellationToken cancel)
    {
        int w = work.Width, h = work.Height;
        double a = skew * Math.PI / 180;
        int sw = (int)Math.Ceiling(w * Math.Abs(Math.Cos(a)) + h * Math.Abs(Math.Sin(a)));
        int sh = (int)Math.Ceiling(w * Math.Abs(Math.Sin(a)) + h * Math.Abs(Math.Cos(a)));
        var toStraight = SKMatrix.CreateTranslation(-w / 2f, -h / 2f)
            .PostConcat(SKMatrix.CreateRotationDegrees(-skew))
            .PostConcat(SKMatrix.CreateTranslation(sw / 2f, sh / 2f));
        var back = toStraight.Invert();
        List<Line> lines;
        using (var straight = new SKBitmap(new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Opaque)))
        {
            using (var canvas = new SKCanvas(straight))
            {
                canvas.Clear(SKColors.White);
                canvas.SetMatrix(toStraight);
                using var image = SKImage.FromBitmap(work);
                canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), null);
            }
            lines = RunTiled(engine, straight, cancel);
        }
        PointF[] Back(PointF[] q) => q.Select(p => { var o = back.MapPoint(p.X, p.Y); return new PointF(o.X, o.Y); }).ToArray();
        // The corners of the sheet that the image does not cover are white: what is read there lies outside the image.
        return lines.Select(l => new Line(l.Text, Back(l.Quad), l.Words.Select(x => x with { Quad = Back(x.Quad) }).ToList()))
            .Where(l => Center(l.Quad) is var c && c.X >= 0 && c.Y >= 0 && c.X <= w && c.Y <= h).ToList();
    }

    private static List<Line> RunTiled(RapidOcr engine, SKBitmap work, CancellationToken cancel)
    {
        int w = work.Width, h = work.Height;
        var whole = new List<Line>();
        var cut = new List<Line>();
        foreach (var t in Tiles(w, h))
        {
            cancel.ThrowIfCancellationRequested();
            using var tile = Crop(work, t);
            foreach (var line in Detect(engine, tile, t.Left, t.Top, cancel))
            {
                var b = Bounds(line.Quad);
                bool touches = (t.Left > 0 && b.Left <= t.Left + EdgeMargin) || (t.Right < w && b.Right >= t.Right - EdgeMargin)
                            || (t.Top > 0 && b.Top <= t.Top + EdgeMargin) || (t.Bottom < h && b.Bottom >= t.Bottom - EdgeMargin);
                (touches ? cut : whole).Add(line);
            }
        }

        // A line that crosses a tile edge comes back as pieces, each read on its own. The pieces of one line overlap in
        // the shared strip, so they are joined, and the line is read again from a crop that holds all of it.
        foreach (var group in Groups(cut))
        {
            cancel.ThrowIfCancellationRequested();
            var area = group.Aggregate(RectangleF.Union);
            float thick = Math.Min(area.Width, area.Height);
            var keep = RectangleF.Inflate(area, thick / 4, thick / 4);
            var r = Rectangle.Intersect(Rectangle.Round(RectangleF.Inflate(area, Math.Max(8, thick / 2), Math.Max(8, thick / 2))), new Rectangle(0, 0, w, h));
            if (r.Width < 1 || r.Height < 1) continue;
            using var crop = Crop(work, r);
            int side = Math.Max(r.Width, r.Height);
            if ((long)r.Width * r.Height > MaxCropArea) // very large lettering: plenty big enough to read smaller
                side = (int)(side * Math.Sqrt((double)MaxCropArea / ((long)r.Width * r.Height)));
            foreach (var line in Detect(engine, crop, r.Left, r.Top, cancel, side, CropPadding))
            {
                var b = Bounds(line.Quad);
                if (keep.Contains(b.Left + b.Width / 2, b.Top + b.Height / 2)) whole.Add(line);
            }
        }
        return Distinct(whole);
    }

    /// <summary>Overlapping tiles of about <see cref="TileArea"/>, wide rather than tall so that fewer lines are cut.</summary>
    private static IEnumerable<Rectangle> Tiles(int w, int h)
    {
        int cols = w <= MaxTileSide ? 1 : (int)Math.Ceiling((w - Overlap) / (double)(MaxTileSide - Overlap));
        int tw = cols == 1 ? w : (int)Math.Ceiling((w + (cols - 1) * (double)Overlap) / cols);
        int th = Math.Min(h, Math.Clamp(TileArea / tw, MinTileSide, MaxTileSide));
        int rows = th >= h ? 1 : (int)Math.Ceiling((h - Overlap) / (double)(th - Overlap));
        if (rows > 1) th = (int)Math.Ceiling((h + (rows - 1) * (double)Overlap) / rows); // as short as covering needs: no work read twice for nothing
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int x = cols == 1 ? 0 : (int)Math.Round(c * (w - tw) / (double)(cols - 1));
                int y = rows == 1 ? 0 : (int)Math.Round(r * (h - th) / (double)(rows - 1));
                yield return new Rectangle(x, y, tw, th);
            }
    }

    /// <summary>Bounding boxes of cut line pieces, joined where pieces of the same line overlap.</summary>
    private static List<List<RectangleF>> Groups(List<Line> pieces)
    {
        var parent = Enumerable.Range(0, pieces.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (int i = 0; i < pieces.Count; i++)
            for (int j = i + 1; j < pieces.Count; j++)
                if (SameLine(pieces[i].Quad, pieces[j].Quad)) parent[Find(i)] = Find(j);
        return Enumerable.Range(0, pieces.Count).GroupBy(Find).Select(g => g.Select(i => Bounds(pieces[i].Quad)).ToList()).ToList();
    }

    /// <summary>
    /// Two pieces of one line: measured along and across the line they run on (the more elongated of the two quads), they
    /// touch, and across the line they share most of the thinner one's height. Two lines stacked one above the other
    /// touch too, but barely overlap across. Taken from the quads, not their upright bounding boxes: on a page a degree or
    /// two askew, the box of a long line takes in the short lines above and below it.
    /// </summary>
    private static bool SameLine(PointF[] a, PointF[] b)
    {
        var (u, v) = Frame(a, b);
        var (au, bu, av, bv) = (Span(a, u), Span(b, u), Span(a, v), Span(b, v));
        if (Shared(au, bu) < -2 || Shared(av, bv) < -2) return false; // touching, within 2 px
        return Shared(av, bv) >= 0.5f * Math.Min(av.Hi - av.Lo, bv.Hi - bv.Lo);
    }

    /// <summary>
    /// The same line read twice (from two overlapping tiles, or from a tile and a crop): the longer reading stays. Same
    /// line means, along and across the line (see <see cref="SameLine"/>): most of the thinner one's height shared, most
    /// of the shorter one's run shared, and most of the shorter text in the longer one. Different lines never count as
    /// one, however askew the page: each may hold a value.
    /// </summary>
    private static List<Line> Distinct(List<Line> lines)
    {
        var kept = new List<Line>();
        foreach (var l in lines.OrderByDescending(l => l.Text.Length))
        {
            bool dup = kept.Any(k =>
            {
                var (u, v) = Frame(k.Quad, l.Quad);
                var (ku, lu, kv, lv) = (Span(k.Quad, u), Span(l.Quad, u), Span(k.Quad, v), Span(l.Quad, v));
                return Shared(kv, lv) >= 0.5f * Math.Min(kv.Hi - kv.Lo, lv.Hi - lv.Lo)
                    && Shared(ku, lu) >= 0.6f * Math.Min(ku.Hi - ku.Lo, lu.Hi - lu.Lo)
                    && Similar(k.Text, l.Text);
            });
            if (!dup) kept.Add(l);
        }
        return kept;
    }

    /// <summary>
    /// The shorter text is mostly in the longer one (longest common subsequence, spaces left out, at least half of it):
    /// two readings of one line differ in a slip or a cut-off end, two different lines in most of their characters.
    /// </summary>
    private static bool Similar(string a, string b)
    {
        string s = new(a.Where(c => !char.IsWhiteSpace(c)).ToArray()), t = new(b.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (s.Length > t.Length) (s, t) = (t, s);
        if (s.Length == 0) return true;
        var row = new int[t.Length + 1];
        for (int i = 1; i <= s.Length; i++)
        {
            int diag = 0;
            for (int j = 1; j <= t.Length; j++)
            {
                int up = row[j];
                row[j] = char.ToUpperInvariant(s[i - 1]) == char.ToUpperInvariant(t[j - 1]) ? diag + 1 : Math.Max(row[j], row[j - 1]);
                diag = up;
            }
        }
        return row[t.Length] >= 0.5 * s.Length;
    }

    /// <summary>
    /// Unit vectors along (u) and across (v) the line that the more elongated of two quads lies on; v is u turned a
    /// quarter clockwise. Quads that are not four corners fall back to the image axes.
    /// </summary>
    private static (PointF U, PointF V) Frame(PointF[] a, PointF[] b)
    {
        static (PointF Dir, float Ratio) Axis(PointF[] q)
        {
            if (q.Length != 4) return (new PointF(1, 0), 1);
            float s01 = (Dist(q[0], q[1]) + Dist(q[2], q[3])) / 2, s12 = (Dist(q[1], q[2]) + Dist(q[3], q[0])) / 2;
            var (p, r) = s01 >= s12 ? (q[0], q[1]) : (q[1], q[2]);
            float dx = r.X - p.X, dy = r.Y - p.Y, n = MathF.Sqrt(dx * dx + dy * dy);
            if (n < 1e-3f) return (new PointF(1, 0), 1);
            return (new PointF(dx / n, dy / n), Math.Max(s01, s12) / Math.Max(1e-3f, Math.Min(s01, s12)));
        }
        var (da, ra) = Axis(a);
        var (db, rb) = Axis(b);
        PointF u = ra >= rb ? da : db;
        return (u, new PointF(-u.Y, u.X));
    }

    private static (float Lo, float Hi) Span(PointF[] q, PointF axis)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var p in q)
        {
            float d = p.X * axis.X + p.Y * axis.Y;
            lo = Math.Min(lo, d); hi = Math.Max(hi, d);
        }
        return (lo, hi);
    }

    /// <summary>Length two intervals share; negative is the gap between them.</summary>
    private static float Shared((float Lo, float Hi) a, (float Lo, float Hi) b) => Math.Min(a.Hi, b.Hi) - Math.Max(a.Lo, b.Lo);

    /// <summary>One RapidOcrNet pass; lines come back moved by (dx, dy). Words are matched to their place in the line text.</summary>
    private static List<Line> Detect(RapidOcr engine, SKBitmap bmp, float dx, float dy, CancellationToken cancel, int side = 0, int padding = -1)
    {
        var result = engine.Detect(bmp, Options(side > 0 ? side : Math.Max(bmp.Width, bmp.Height), padding), cancel);
        var lines = new List<Line>();
        foreach (var block in result.TextBlocks)
        {
            if (string.IsNullOrWhiteSpace(block.Text)) continue;
            var words = new List<Word>();
            int at = 0;
            foreach (var wb in block.WordResults ?? [])
            {
                if (string.IsNullOrEmpty(wb.Text)) continue;
                int i = block.Text.IndexOf(wb.Text, at, StringComparison.Ordinal);
                if (i < 0) continue; // left out: FindSensitive then paints the whole line where this word would be
                words.Add(new Word(wb.Text, i, Points(wb.BoxPoints, dx, dy)));
                at = i + wb.Text.Length;
            }
            lines.Add(new Line(block.Text, Points(block.BoxPoints, dx, dy), words));
        }
        return lines;
    }

    // ---------- reading order ----------

    /// <summary>
    /// Lines in reading order, also for a turned image: positions are taken along the text direction (the mean direction
    /// from each line's first word to its last). Lines that follow each other below, overlapping sideways, form a block
    /// (a column, a paragraph), so a value wrapped onto the next line stays next to its start in the text; blocks are
    /// read top to bottom, side by side ones left to right.
    /// </summary>
    private static List<Line> ReadingOrder(List<Line> lines)
    {
        if (lines.Count < 2) return lines;
        double ux = 0, uy = 0;
        foreach (var l in lines.Where(l => l.Words.Count >= 2))
        {
            var a = Center(l.Words[0].Quad);
            var b = Center(l.Words[^1].Quad);
            double len = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            if (len > 0) { ux += (b.X - a.X) / len; uy += (b.Y - a.Y) / len; }
        }
        double n = Math.Sqrt(ux * ux + uy * uy);
        if (n < 1e-6) (ux, uy) = (1, 0);
        else (ux, uy) = (ux / n, uy / n);
        // u runs along the text, v down the page (u turned a quarter clockwise, y pointing down)
        var spans = lines.Select(l =>
        {
            double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
            foreach (var p in l.Quad)
            {
                double u = p.X * ux + p.Y * uy, v = -p.X * uy + p.Y * ux;
                u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
            }
            return (Line: l, U0: u0, U1: u1, V0: v0, V1: v1, H: Math.Max(1, v1 - v0));
        }).OrderBy(s => s.V0).ToList();

        var blocks = new List<List<int>>();
        for (int i = 0; i < spans.Count; i++)
        {
            var s = spans[i];
            int best = -1;
            double bestGap = double.MaxValue;
            for (int k = 0; k < blocks.Count; k++)
            {
                var last = spans[blocks[k][^1]];
                double side = Math.Min(s.U1, last.U1) - Math.Max(s.U0, last.U0);
                double gap = s.V0 - last.V1;
                if (side < 0.3 * Math.Min(s.U1 - s.U0, last.U1 - last.U0)) continue;
                if (gap < -0.5 * Math.Min(s.H, last.H) || gap > 1.5 * Math.Max(s.H, last.H)) continue;
                if (gap < bestGap) { bestGap = gap; best = k; }
            }
            if (best >= 0) blocks[best].Add(i);
            else blocks.Add([i]);
        }
        // blocks start in order of V0 (lines were taken top down); starts within half a line of each other share a row
        var ordered = new List<Line>();
        for (int b = 0; b < blocks.Count;)
        {
            var first = spans[blocks[b][0]];
            int e = b + 1;
            while (e < blocks.Count && spans[blocks[e][0]].V0 - first.V0 < 0.5 * first.H) e++;
            foreach (var block in blocks.Skip(b).Take(e - b).OrderBy(k => spans[k[0]].U0))
                ordered.AddRange(block.Select(i => spans[i].Line));
            b = e;
        }
        return ordered;
    }

    // ---------- sensitive data ----------

    /// <summary>
    /// Radar's patterns (TC checksum, IBAN mod-97, e-mail, phone) over the page text, raw and with digit look-alikes
    /// fixed (<see cref="FixDigits"/>); a place found in both counts once. Never throws.
    /// </summary>
    public static List<Finding> FindSensitive(Page page)
    {
        var findings = new List<Finding>();
        try
        {
            var text = new StringBuilder();
            var starts = new int[page.Lines.Count];
            for (int i = 0; i < page.Lines.Count; i++)
            {
                if (i > 0) text.Append('\n');
                starts[i] = text.Length;
                text.Append(page.Lines[i].Text);
            }
            string raw = text.ToString(), fixedText = FixDigits(raw);
            var hits = Radar.FindAll(raw).Select(h => (Hit: h, Text: raw)).ToList();
            if (!ReferenceEquals(fixedText, raw) && fixedText != raw)
                foreach (var h in Radar.FindAll(fixedText))
                    if (!hits.Any(k => k.Hit.Kind == h.Kind && k.Hit.Index < h.Index + h.Length && h.Index < k.Hit.Index + k.Hit.Length))
                        hits.Add((h, fixedText));

            foreach (var (hit, source) in hits)
            {
                var region = new List<PointF[]>();
                for (int i = 0; i < page.Lines.Count; i++)
                {
                    var line = page.Lines[i];
                    int p = Math.Max(hit.Index, starts[i]) - starts[i], q = Math.Min(hit.Index + hit.Length, starts[i] + line.Text.Length) - starts[i];
                    if (q <= p || string.IsNullOrWhiteSpace(line.Text[p..q])) continue;
                    var points = new List<PointF>();
                    float thick = Thickness(line.Quad);
                    foreach (var w in line.Words)
                        if (w.Start < q && w.Start + w.Text.Length > p) points.AddRange(Lengthen(w.Quad, line.Quad, 0.6f * thick));
                    // a character no word box accounts for (a word that could not be placed): the whole line, to be safe
                    for (int c = p; c < q; c++)
                        if (!char.IsWhiteSpace(line.Text[c]) && !line.Words.Any(w => c >= w.Start && c < w.Start + w.Text.Length))
                        {
                            points.AddRange(line.Quad);
                            break;
                        }
                    float grow = Math.Max(3f, 0.2f * thick);
                    var poly = Clip(Grow(Hull(points), grow), page.Width, page.Height);
                    if (poly.Length >= 3) region.Add(poly);
                }
                if (region.Count > 0) findings.Add(new Finding(hit.Kind, source.Substring(hit.Index, hit.Length), region));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log.Write($"ocr search failed: {ex.GetType().Name}: {ex.Message}");
        }
        return findings;
    }

    // OCR reads a digit as the letter that looks like it: "TRO2", "O532", "1234S678950".
    private static readonly Dictionary<char, char> LookAlike = new()
    {
        ['O'] = '0', ['o'] = '0', ['D'] = '0',
        ['I'] = '1', ['l'] = '1', ['|'] = '1', ['İ'] = '1', ['ı'] = '1',
        ['Z'] = '2', ['z'] = '2',
        ['S'] = '5', ['s'] = '5',
        ['G'] = '6',
        ['B'] = '8',
        ['q'] = '9',
    };

    /// <summary>
    /// The text with OCR's letter-for-digit slips turned back into digits, one character for one, so every position still
    /// points at the same place on the image. A look-alike changes when a digit is next to it and no real letter is on its
    /// other side (look-alikes do not count as letters, so "1OO5" becomes "1005" while "TO 12" stays); and the two check
    /// digits right after an IBAN's "TR" when digits follow. Only for searching: the raw text is searched too.
    /// </summary>
    public static string FixDigits(string text)
    {
        var c = text.ToCharArray();
        bool changed = false;
        for (int i = 0; i + 4 < c.Length; i++)
        {
            if (char.ToUpperInvariant(c[i]) != 'T' || char.ToUpperInvariant(c[i + 1]) != 'R' || (i > 0 && char.IsLetterOrDigit(c[i - 1]))) continue;
            if (!IsDigitish(c[i + 2]) || !IsDigitish(c[i + 3])) continue;
            if (!(char.IsAsciiDigit(c[i + 4]) || (c[i + 4] == ' ' && i + 5 < c.Length && char.IsAsciiDigit(c[i + 5])))) continue;
            for (int k = i + 2; k <= i + 3; k++)
                if (LookAlike.TryGetValue(c[k], out char d)) { c[k] = d; changed = true; }
        }
        // repeated so that a run of look-alikes ("OO6") turns from the digit side inwards, whichever side that is
        for (int pass = 0; pass < 8; pass++)
        {
            bool any = false;
            for (int i = 0; i < c.Length; i++)
            {
                if (!LookAlike.TryGetValue(c[i], out char d)) continue;
                bool digitBefore = i > 0 && char.IsAsciiDigit(c[i - 1]), digitAfter = i + 1 < c.Length && char.IsAsciiDigit(c[i + 1]);
                if (!digitBefore && !digitAfter) continue;
                if ((i > 0 && IsRealLetter(c[i - 1])) || (i + 1 < c.Length && IsRealLetter(c[i + 1]))) continue;
                c[i] = d;
                any = changed = true;
            }
            if (!any) break;
        }
        // A group of look-alikes only ("0532 100 II 22"): no digit touches it, but on its line it stands between two
        // groups of digits, a space on each side. At most two such groups in a row, of up to four characters each.
        var groups = new List<(int Start, int End)>();
        for (int i = 0; i < c.Length;)
        {
            if (c[i] is ' ' or '\n') { i++; continue; }
            int s = i;
            while (i < c.Length && c[i] is not (' ' or '\n')) i++;
            groups.Add((s, i));
        }
        bool Digits((int Start, int End) g) => g.End - g.Start >= 2 && c.AsSpan(g.Start, g.End - g.Start).IndexOfAnyExceptInRange('0', '9') < 0;
        bool Alike((int Start, int End) g) => g.End - g.Start <= 4 && c.AsSpan(g.Start, g.End - g.Start).ToArray().All(LookAlike.ContainsKey);
        bool Spaced(int a, int b) => c.AsSpan(groups[a].End, groups[b].Start - groups[a].End).IndexOfAnyExcept(' ') < 0; // same line
        for (int g = 1; g + 1 < groups.Count; g++)
        {
            if (!Alike(groups[g]) || !Digits(groups[g - 1]) || !Spaced(g - 1, g)) continue;
            int e = g;
            if (e + 2 < groups.Count && Alike(groups[e + 1]) && Spaced(e, e + 1)) e++;
            if (!Digits(groups[e + 1]) || !Spaced(e, e + 1)) continue;
            for (int k = g; k <= e; k++)
                for (int i = groups[k].Start; i < groups[k].End; i++) c[i] = LookAlike[c[i]];
            changed = true;
            g = e;
        }
        return changed ? new string(c) : text;
    }

    private static bool IsDigitish(char ch) => char.IsAsciiDigit(ch) || LookAlike.ContainsKey(ch);
    private static bool IsRealLetter(char ch) => char.IsLetter(ch) && !LookAlike.ContainsKey(ch);

    // ---------- geometry ----------

    private static PointF[] Points(SKPointI[] p, float dx, float dy) => p.Select(q => new PointF(q.X + dx, q.Y + dy)).ToArray();

    private static Line Map(Line l, float dx, float dy, float k) =>
        new(l.Text, l.Quad.Select(p => new PointF((p.X + dx) * k, (p.Y + dy) * k)).ToArray(),
            l.Words.Select(w => w with { Quad = w.Quad.Select(p => new PointF((p.X + dx) * k, (p.Y + dy) * k)).ToArray() }).ToList());

    private static RectangleF Bounds(PointF[] q)
    {
        float x0 = q.Min(p => p.X), y0 = q.Min(p => p.Y);
        return new RectangleF(x0, y0, q.Max(p => p.X) - x0, q.Max(p => p.Y) - y0);
    }

    private static PointF Center(PointF[] q) => new(q.Average(p => p.X), q.Average(p => p.Y));

    private static float Dist(PointF a, PointF b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Line height of a 4-corner quad: the shorter of its two mean side lengths, whichever way the text runs.</summary>
    private static float Thickness(PointF[] q) => q.Length != 4 ? Bounds(q).Height
        : Math.Min((Dist(q[0], q[1]) + Dist(q[2], q[3])) / 2, (Dist(q[1], q[2]) + Dist(q[3], q[0])) / 2);

    /// <summary>
    /// A word quad stretched by <paramref name="by"/> at both ends along its line. Word boxes come from where the
    /// recogniser placed each character, which is its start: measured, the box of a line's last word stops up to most of a
    /// character short of the ink.
    /// </summary>
    private static IEnumerable<PointF> Lengthen(PointF[] word, PointF[] line, float by)
    {
        if (line.Length != 4) return word;
        // the line's long side gives the text direction; which end is which does not matter, both ends move out
        PointF a = Dist(line[0], line[1]) >= Dist(line[1], line[2]) ? new(line[1].X - line[0].X, line[1].Y - line[0].Y) : new(line[2].X - line[1].X, line[2].Y - line[1].Y);
        float n = MathF.Sqrt(a.X * a.X + a.Y * a.Y);
        if (n < 1e-3f) return word;
        float ux = a.X / n, uy = a.Y / n;
        var c = Center(word);
        float mid = c.X * ux + c.Y * uy;
        return word.Select(p => p.X * ux + p.Y * uy >= mid ? new PointF(p.X + ux * by, p.Y + uy * by) : new PointF(p.X - ux * by, p.Y - uy * by));
    }

    /// <summary>Convex hull (monotone chain), counter-clockwise in y-down coordinates.</summary>
    private static PointF[] Hull(IEnumerable<PointF> points)
    {
        var p = points.Distinct().OrderBy(q => q.X).ThenBy(q => q.Y).ToList();
        if (p.Count < 3) return p.ToArray();
        static float Cross(PointF o, PointF a, PointF b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new List<PointF>();
        foreach (var q in p) { while (h.Count >= 2 && Cross(h[^2], h[^1], q) <= 0) h.RemoveAt(h.Count - 1); h.Add(q); }
        int lower = h.Count + 1;
        for (int i = p.Count - 2; i >= 0; i--) { while (h.Count >= lower && Cross(h[^2], h[^1], p[i]) <= 0) h.RemoveAt(h.Count - 1); h.Add(p[i]); }
        h.RemoveAt(h.Count - 1);
        return h.ToArray();
    }

    /// <summary>The polygon grown outward by at least d everywhere: hull of an octagon around every corner.</summary>
    private static PointF[] Grow(PointF[] poly, float d)
    {
        float r = d / MathF.Cos(MathF.PI / 8); // the octagon's sides, not only its corners, lie d away
        var pts = new List<PointF>(poly.Length * 8);
        foreach (var p in poly)
            for (int k = 0; k < 8; k++)
                pts.Add(new PointF(p.X + r * MathF.Cos(k * MathF.PI / 4 + MathF.PI / 8), p.Y + r * MathF.Sin(k * MathF.PI / 4 + MathF.PI / 8)));
        return Hull(pts);
    }

    /// <summary>A convex polygon clipped to the image rectangle (Sutherland-Hodgman).</summary>
    private static PointF[] Clip(PointF[] poly, float w, float h)
    {
        var p = poly.ToList();
        foreach (var (inside, cross) in new (Func<PointF, bool>, Func<PointF, PointF, PointF>)[]
        {
            (q => q.X >= 0, (a, b) => At(a, b, (0 - a.X) / (b.X - a.X))),
            (q => q.X <= w, (a, b) => At(a, b, (w - a.X) / (b.X - a.X))),
            (q => q.Y >= 0, (a, b) => At(a, b, (0 - a.Y) / (b.Y - a.Y))),
            (q => q.Y <= h, (a, b) => At(a, b, (h - a.Y) / (b.Y - a.Y))),
        })
        {
            var o = new List<PointF>();
            for (int i = 0; i < p.Count; i++)
            {
                PointF a = p[i], b = p[(i + 1) % p.Count];
                bool ia = inside(a), ib = inside(b);
                if (ia) o.Add(a);
                if (ia != ib) o.Add(cross(a, b));
            }
            p = o;
            if (p.Count == 0) break;
        }
        return p.ToArray();
    }

    private static PointF At(PointF a, PointF b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    // ---------- pixels ----------

    /// <summary>
    /// An opaque BGRA copy, scaled by <paramref name="scale"/>. Transparent areas become white, as a viewer shows them on a
    /// page; left transparent they read as black, and black text on them would disappear.
    /// </summary>
    private static SKBitmap Opaque(SKPixmap src, float scale)
    {
        int w = Math.Max(1, (int)Math.Round(src.Width * scale)), h = Math.Max(1, (int)Math.Round(src.Height * scale));
        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        using var image = SKImage.FromPixels(src) ?? throw new InvalidOperationException("unsupported pixel format");
        var sampling = scale == 1 ? new SKSamplingOptions(SKFilterMode.Nearest) : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        canvas.DrawImage(image, new SKRect(0, 0, w, h), sampling, null);
        canvas.Flush();
        return dst;
    }

    private static SKBitmap Crop(SKBitmap src, Rectangle r)
    {
        using var subset = new SKBitmap();
        if (!src.ExtractSubset(subset, new SKRectI(r.Left, r.Top, r.Right, r.Bottom))) throw new InvalidOperationException("tile copy failed");
        return subset.Copy() ?? throw new InvalidOperationException("tile copy failed"); // own pixels, packed rows
    }
}

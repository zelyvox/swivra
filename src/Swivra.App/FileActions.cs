using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Swivra.App;

internal static class FileKinds
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".avif", ".heic", ".heif" };
    private static readonly HashSet<string> Docs = new(StringComparer.OrdinalIgnoreCase)
        { ".docx", ".doc", ".txt", ".md", ".rtf", ".odt" };

    public static bool IsImage(string ext) => Images.Contains(ext);

    public static string KindOf(string ext) =>
        ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "pdf"
        : Images.Contains(ext) ? "img"
        : Docs.Contains(ext) ? "doc"
        : "other";

    public static string HumanSize(long bytes) =>
        // Invariant first: the Windows culture would put its own decimal mark in the English UI too.
        bytes >= 1024 * 1024 ? (bytes / 1024d / 1024d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture).Replace('.', Lang.IsTurkish ? ',' : '.') + " MB"
        : bytes >= 1024 ? $"{bytes / 1024d:0} KB"
        : $"{bytes} B";
}

internal sealed record ActionResult(bool Ok, string Title, string Detail, string? OutputPath = null, string? OutputExt = null, string? ClipboardText = null, Func<bool>? OnUi = null, bool Cleaned = false, int? Hidden = null, bool Unread = false);
// OnUi: work that needs the STA UI thread (clipboard file list, launching an app); false turns the result into a failure.
// Cleaned: links or embedded sections were cut from a copy before an office program opened it; Detail says what.
// Hidden: Hide ran and covered this many values (null: Hide did not run, or the step does not say how many).
// Unread: Hide could not read part of the file (pages or images); Detail says which.

/// <summary>
/// Input that some steps need beyond the step id, filled in by a tool panel.
/// Crop is in pixels of the image as the user sees it (EXIF orientation applied, before any rotate step).
/// </summary>
internal sealed record StepOptions(Rectangle? Crop = null, string? Password = null)
{
    public static readonly StepOptions None = new();
}

/// <summary>An error whose message is already worded for the user (both languages) and is shown as the result detail.</summary>
internal sealed class UserFacingException(string message) : Exception(message);

/// <summary>
/// Runs a chain of wheel steps on one or more files. Originals are never modified;
/// output goes next to them under a new, non-colliding name.
/// Image steps use GDI+, PDF steps live in PdfActions; everything else reports "not yet".
/// </summary>
internal static class FileActions
{
    private static Dictionary<string, string> Labels => new()
    {
        ["png"] = "PNG", ["jpg"] = "JPG", ["bmp"] = "BMP", ["webp"] = "WebP", ["pdf"] = "PDF", ["docx"] = "Word", ["txt"] = Lang.T("Metin", "Text"),
        ["rot"] = Lang.T("Döndür", "Rotate"), ["loc"] = Lang.T("Konumu sil", "Remove location"), ["rs"] = Lang.T("Boyutlandır", "Resize"),
        ["cmp"] = Lang.T("Sıkıştır", "Compress"), ["crop"] = Lang.T("Kırp", "Crop"), ["bg"] = Lang.T("Arka planı sil", "Cut out"),
        ["hide"] = Lang.T("Gizle", "Hide"), ["lock"] = Lang.T("Şifrele", "Lock"), ["split"] = Lang.T("Böl", "Split"), ["merge"] = Lang.T("Birleştir", "Merge"),
        ["path"] = Lang.T("Yolu kopyala", "Copy path"), ["wa"] = "WhatsApp", ["mail"] = "Mail", ["tel"] = Lang.T("Telefona", "To phone"),
    };

    private static readonly HashSet<string> ImageSteps = new() { "png", "jpg", "bmp", "webp", "pdf", "bg", "rot", "loc", "rs", "cmp", "crop", "hide" };

    // Hide is asked for, not a scan during a drag: time for a large photo or a 300 DPI scan read at full size (a 12 MP
    // photo takes about 6 s, a 48 MP one about 22 s), but an end, after which nothing is hidden and the result says so.
    private static readonly TimeSpan HideBudget = TimeSpan.FromSeconds(90);

    // Said with every Hide result on an image, found or not: OCR never gets to say "clean". Short enough that the result
    // still fits inside the wheel's centre (measured: one note more, or "blurry" too, runs over its edge).
    private static string HideCaveat => Lang.T("el yazısı ya da küçük yazı kaçabilir, gözle bak", "handwriting or small text may be missed, check by eye");

    public static ActionResult Run(IReadOnlyList<string> files, IReadOnlyList<string> steps, StepOptions? options = null)
    {
        options ??= StepOptions.None;
        if (steps.Any(SendActions.IsSendStep)) return SendActions.Run(files, steps);
        var fileSteps = steps.Where(s => s != "path").ToList();
        bool copyPath = steps.Contains("path");

        if (fileSteps.Contains("merge"))
        {
            if (fileSteps.Count > 1) return new ActionResult(false, Lang.T("Birleştir", "Merge"), Lang.T("Birleştir zincirde tek başına olmalı", "Merge must be alone in the chain"));
            var pdfs = files.Where(f => FileKinds.KindOf(Path.GetExtension(f)) == "pdf").ToList();
            if (pdfs.Count < 2) return new ActionResult(false, Lang.T("Birleştir", "Merge"), Lang.T("Birden fazla PDF'i birlikte sürükle", "Drag several PDFs together"));
            var merged = PdfActions.Merge(pdfs);
            // Files that are not PDFs are left out; say so rather than drop them unseen.
            int left = files.Count - pdfs.Count;
            if (merged.Ok && left > 0) merged = merged with { Detail = $"{merged.Detail} · {Lang.T($"{left} PDF olmayan dosya eklenmedi", left == 1 ? "1 non-PDF file left out" : $"{left} non-PDF files left out")}" };
            if (!merged.Ok || !copyPath) return merged;
            return merged with { Detail = $"{merged.Detail} · {Lang.T("yolu panoda", "path copied")}", ClipboardText = merged.OutputPath };
        }

        // Compress and lock on documents/other files make one zip of all of them (PDFs and images keep their own steps).
        var results = new List<ActionResult>();
        if (fileSteps is ["cmp"] or ["lock"])
        {
            bool lockStep = fileSteps[0] == "lock";
            bool allPdf = files.All(f => FileKinds.KindOf(Path.GetExtension(f)) == "pdf");
            var zipFiles = lockStep
                ? (allPdf ? [] : files.ToList())
                : files.Where(f => FileKinds.KindOf(Path.GetExtension(f)) is not ("pdf" or "img")).ToList();
            if (zipFiles.Count > 0)
            {
                var zr = lockStep ? DocActions.LockZip(zipFiles, options.Password ?? "") : DocActions.Compress(zipFiles);
                results.Add(copyPath && zr.Ok ? zr with { ClipboardText = zr.OutputPath } : zr);
            }
            results.AddRange(files.Except(zipFiles).Select(f => RunOne(f, fileSteps, copyPath, options)));
        }
        else results.AddRange(files.Select(f => RunOne(f, fileSteps, copyPath, options)));
        if (results.Count == 1) return results[0];
        return Summary(results, fileSteps, copyPath);
    }

    /// <summary>
    /// One result for several files. Files that could not be processed are counted on their own, whatever their place
    /// in the list: a failure is never hidden behind another file's result. With Hide in the chain it also says how many
    /// values were hidden in all, how many files had nothing to hide or were not read whole, and asks for a look by eye.
    /// Each file's own note does not fit inside the wheel; the counts say what happened.
    /// </summary>
    private static ActionResult Summary(List<ActionResult> results, List<string> fileSteps, bool copyPath)
    {
        bool hide = fileSteps.Contains("hide");
        // A step that succeeded but made no file (e.g. Hide with nothing to hide) counts as skipped.
        var ok = results.Where(r => r.Ok && (r.OutputPath is not null || fileSteps.Count == 0)).ToList();
        var skipped = results.Where(r => !ok.Contains(r) && (r.Ok || NothingInDocument(r))).ToList();
        int failed = results.Count - ok.Count - skipped.Count;
        if (ok.Count == 0 && failed == 0 && !hide) return results[0];

        string? clip = copyPath ? string.Join(Environment.NewLine, ok.Select(r => r.ClipboardText)) : null;
        string? ext = ok.Count > 0 && ok.Select(r => r.OutputExt).Distinct().Count() == 1 ? ok[0].OutputExt : null;
        if (fileSteps.Count == 0 && ok.Count == results.Count)
            return new ActionResult(true, Lang.T("Kopyalandı", "Copied"), Lang.T($"{ok.Count} yol panoda", $"{ok.Count} paths copied"), ClipboardText: clip);

        var parts = new List<string>();
        if (ok.Count > 0)
            parts.Add(ok.Count == results.Count ? Lang.T($"{ok.Count} dosya hazır", $"{ok.Count} files ready") : Lang.T($"{ok.Count}/{results.Count} dosya hazır", $"{ok.Count}/{results.Count} files ready"));
        // The total only when every file that Hide wrote says how many it covered (Word does not): no half count.
        if (hide && ok.Count > 0 && ok.All(r => r.Hidden is not null))
        {
            int items = ok.Sum(r => r.Hidden!.Value);
            parts.Add(Lang.T($"{items} öğe gizlendi", items == 1 ? "1 item hidden" : $"{items} items hidden"));
        }
        if (failed > 0) parts.Add(Lang.T($"{failed} dosya işlenemedi", failed == 1 ? "1 file could not be processed" : $"{failed} files could not be processed"));
        if (skipped.Count > 0)
            parts.Add(hide
                ? Lang.T($"{skipped.Count} dosyada gizlenecek bir şey bulunamadı", skipped.Count == 1 ? "nothing to hide found in 1 file" : $"nothing to hide found in {skipped.Count} files")
                : Lang.T($"{skipped.Count} atlandı", $"{skipped.Count} skipped"));
        if (results.Count(r => r.Unread) is int unread and > 0) parts.Add(Lang.T($"{unread} dosya tam okunamadı", unread == 1 ? "1 file not read whole" : $"{unread} files not read whole"));
        if (ok.Count(r => r.Cleaned) is int cleaned and > 0) parts.Add(Lang.T($"{cleaned} dosya temizlendi", $"{cleaned} cleaned"));
        if (hide) parts.Add(Lang.T("gözle bak", "check by eye"));
        string detail = string.Join(" · ", parts);

        if (ok.Count > 0) return new ActionResult(true, Lang.T("Hazır", "Ready"), detail, ok[0].OutputPath, ext, clip);
        if (failed == 0) return new ActionResult(true, Lang.T("Gizle", "Hide"), detail);
        // Nothing made: one reason, when every file failed for the same one, still fits.
        var reasons = results.Where(r => !r.Ok && !skipped.Contains(r)).Select(r => r.Detail).Distinct().ToList();
        if (skipped.Count == 0 && reasons.Count == 1) detail = $"{detail} · {reasons[0]}";
        return new ActionResult(false, Lang.T("Olmadı", "Failed"), detail);
    }

    /// <summary>Word's and text files' Hide reports "nothing found" as a failure; in a summary it is a file with nothing to hide.</summary>
    private static bool NothingInDocument(ActionResult r) =>
        !r.Ok && r.OutputPath is null && r.Title == Lang.T("Gizle", "Hide") && r.Detail.StartsWith(Lang.T("Kapatılacak hassas veri bulunamadı", "No sensitive data found to cover"), StringComparison.Ordinal);

    private static ActionResult RunOne(string path, List<string> fileSteps, bool copyPath, StepOptions options)
    {
        string kind = FileKinds.KindOf(Path.GetExtension(path));
        var supported = kind == "pdf" ? PdfActions.Steps : kind == "doc" ? DocActions.Steps : ImageSteps;

        string? missing = fileSteps.FirstOrDefault(s => kind == "other" || !supported.Contains(s));
        if (missing is not null)
            return new ActionResult(false, Lang.T("Bu dosyada olmaz", "Not for this file"), Lang.T($"{Label(missing)} bu dosya türünde çalışmaz", $"{Label(missing)} does not work on this file type"));

        string finalPath = path;
        string? finalExt = null;
        string? finalName = null;
        bool cleaned = false, unread = false;
        int? hiddenCount = null;
        if (fileSteps.Count > 0)
        {
            try
            {
                if (kind == "pdf")
                {
                    var r = PdfActions.Run(path, fileSteps, Label, options);
                    if (!r.Ok || r.OutputPath is null) return r;
                    finalPath = r.OutputPath!;
                    finalExt = r.OutputExt;
                    finalName = r.Detail;
                    hiddenCount = r.Hidden;
                    unread = r.Unread;
                }
                else if (kind == "doc")
                {
                    var r = DocActions.Run(path, fileSteps, Label);
                    if (!r.Ok || r.OutputPath is null) return r;
                    finalPath = r.OutputPath;
                    finalExt = r.OutputExt;
                    finalName = r.Detail;
                    cleaned = r.Cleaned;
                }
                else
                {
                    bool hide = fileSteps.Contains("hide");
                    string? written = RunImage(path, fileSteps, options, out int hidden);
                    string? frames = ExtraFrames(path);
                    if (written is null)
                    {
                        // Hide alone found nothing: no file, and no claim that the image is clean.
                        var none = new List<string> { Lang.T("Görselde gizlenecek bir şey bulunamadı", "Nothing to hide found in the image") };
                        if (frames is not null) none.Add(frames);
                        none.Add(HideCaveat);
                        return new ActionResult(true, Lang.T("Gizle", "Hide"), string.Join(" · ", none), Hidden: 0);
                    }
                    finalPath = written;
                    finalExt = Path.GetExtension(finalPath).TrimStart('.').ToLowerInvariant();
                    // Say what the file name does not: a format kept for transparency, frames or pages left out.
                    var notes = new List<string>();
                    if (hide)
                    {
                        hiddenCount = hidden;
                        // Found nothing, the other steps ran on the image as it was (as for a PDF): no "-hidden" in the name, and said.
                        if (hidden == 0) notes.Add(Lang.T("gizlenecek bir şey bulunamadı", "nothing to hide was found"));
                        else
                        {
                            notes.Add(Lang.T($"{hidden} öğe gizlendi", hidden == 1 ? "1 item hidden" : $"{hidden} items hidden"));
                            notes.Add(Lang.T("meta veri silindi", "metadata removed"));
                        }
                    }
                    if (fileSteps.Contains("bg") && finalExt == "png" && fileSteps.Any(s => s is "jpg" or "bmp" or "cmp"))
                        notes.Add(Lang.T("saydamlık için PNG", "PNG, to keep transparency"));
                    if (frames is not null) notes.Add(frames);
                    if (hide) notes.Add(HideCaveat);
                    if (notes.Count > 0) finalName = $"{Path.GetFileName(finalPath)} · {string.Join(" · ", notes)}";
                }
            }
            catch (MissingCodecException ex)
            {
                Log.Write($"action failed {path}: {ex.InnerException?.Message}");
                return new ActionResult(false, Lang.T("Açılamadı", "Could not open"), ex.Message);
            }
            catch (UserFacingException ex)
            {
                Log.Write($"action refused {path}: {ex.Message}");
                return new ActionResult(false, Lang.T("Olmadı", "Failed"), ex.Message);
            }
            catch (Exception ex)
            {
                Log.Write($"action failed {path}: {ex}");
                return new ActionResult(false, Lang.T("Olmadı", "Failed"), Lang.T("Dosya işlenemedi", "Could not process the file"));
            }
        }

        string? clip = copyPath ? finalPath : null;
        string name = finalName ?? Path.GetFileName(finalPath);
        if (fileSteps.Count == 0) return new ActionResult(true, Lang.T("Kopyalandı", "Copied"), Lang.T("Yol panoda", "Path copied"), ClipboardText: clip);
        string detail = copyPath ? $"{name} · {Lang.T("yolu panoda", "path copied")}" : name;
        return new ActionResult(true, Lang.T("Hazır", "Ready"), detail, finalPath, finalExt, clip, Cleaned: cleaned, Hidden: hiddenCount, Unread: unread);
    }

    private static string Label(string step) => Labels.GetValueOrDefault(step, step);

    /// <summary>An animated GIF/WebP/APNG or a multi-page TIFF: only the first frame or page is converted, and the note says so.</summary>
    private static string? ExtraFrames(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".gif" or ".tif" or ".tiff" or ".webp" or ".png")) return null;
        try
        {
            if (ext == ".webp")
            {
                using var ws = SafeFile.OpenRead(path);
                using var codec = SkiaSharp.SKCodec.Create(ws);
                return codec is { FrameCount: > 1 } ? Lang.T("yalnız ilk kare", "first frame only") : null;
            }
            if (ext == ".png")
                return IsApng(path) ? Lang.T("yalnız ilk kare", "first frame only") : null;

            using var s = SafeFile.OpenRead(path);
            using var img = Image.FromStream(s, false, false);
            var dim = ext == ".gif" ? FrameDimension.Time : FrameDimension.Page;
            if (!img.FrameDimensionsList.Contains(dim.Guid) || img.GetFrameCount(dim) < 2) return null;
            return ext == ".gif" ? Lang.T("yalnız ilk kare", "first frame only") : Lang.T("yalnız ilk sayfa", "first page only");
        }
        catch { return null; }
    }

    /// <summary>True when an acTL chunk comes before the first IDAT (that is what makes a PNG animated). Walks the chunk headers only.</summary>
    private static bool IsApng(string path)
    {
        using var s = SafeFile.OpenRead(path);
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        byte[] buf = new byte[8];
        if (s.Read(buf, 0, 8) != 8 || !buf.AsSpan().SequenceEqual(sig)) return false;
        long pos = 8;
        while (pos + 12 <= s.Length)
        {
            s.Position = pos;
            if (s.Read(buf, 0, 8) != 8) return false;
            long len = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(buf);
            string type = System.Text.Encoding.ASCII.GetString(buf, 4, 4);
            if (type == "acTL") return true;
            if (type is "IDAT" or "IEND") return false;
            pos += 8 + len + 4; // header, data, CRC
        }
        return false;
    }

    /// <summary>
    /// Runs the image steps and writes the result; returns its path. Null only when Hide is the only step and found
    /// nothing: then nothing is written. <paramref name="hidden"/> is the number of values Hide painted over.
    /// </summary>
    private static string? RunImage(string path, List<string> steps, StepOptions options, out int hidden)
    {
        hidden = 0;
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext == "jpeg") ext = "jpg";
        string suffix = "", hideSuffix = Lang.T("-gizli", "-hidden");
        bool strip = false, compress = false, cutout = false, hide = false;
        int rotations = 0;
        int? maxWidth = null;

        foreach (string s in steps)
        {
            switch (s)
            {
                case "crop": suffix += Lang.T("-kirpik", "-cropped"); break;
                case "png" or "jpg" or "bmp" or "webp" or "pdf": ext = s; break;
                case "bg": cutout = true; suffix += Lang.T("-arkaplansiz", "-nobg"); break;
                case "rot": rotations++; suffix += Lang.T("-dondu", "-rotated"); break;
                case "loc": strip = true; suffix += Lang.T("-temiz", "-clean"); break;
                case "rs": maxWidth = 1080; suffix += "-1080"; break;
                case "cmp": compress = true; ext = "jpg"; suffix += Lang.T("-kucuk", "-small"); break;
                // A blacked-out copy must not hand the original back: the EXIF thumbnail still shows it whole, GPS says where.
                case "hide": hide = strip = true; suffix += hideSuffix; break;
            }
        }

        // Swivra writes only these; other inputs keep a format it can write (HEIC/AVIF are photos, GIF/TIFF may have alpha).
        if (ext is "heic" or "heif" or "avif") ext = "jpg";
        else if (ext is not ("png" or "jpg" or "bmp" or "webp" or "pdf")) ext = "png";

        // A cut-out needs alpha: PNG, unless the chain ends in WebP or PDF (both keep transparency).
        if (cutout && ext is not ("webp" or "pdf")) ext = "png";

        if (steps.Contains("crop") && options.Crop is null) throw new InvalidOperationException("crop step without a crop rectangle");

        Bitmap work = LoadOriented(path, out PropertyItem[] props);
        try
        {
            // Crop first: the panel picked the rectangle on the upright original, before any rotate step.
            if (options.Crop is Rectangle crop && steps.Contains("crop"))
            {
                crop.Intersect(new Rectangle(0, 0, work.Width, work.Height));
                if (crop.Width < 1 || crop.Height < 1) throw new InvalidOperationException($"crop {options.Crop} outside {work.Size}");
                var cropped = work.Clone(crop, work.PixelFormat);
                work.Dispose();
                work = cropped;
            }

            for (int i = 0; i < rotations; i++) work.RotateFlip(RotateFlipType.Rotate90FlipNone);

            // Hide reads the image as it now stands (cropped, turned): its polygons need no mapping back. It reads before
            // Resize, at full size, since 1080 px can shrink small print below what OCR reads, and paints there, so the
            // smaller copy is made from pixels that are already black. Before the cut-out, too: text the cut-out makes
            // transparent is still in the file's colour channels, and OCR only sees what a viewer shows.
            List<PointF[]>? cover = null;
            if (hide)
            {
                (cover, hidden) = FindToHide(work, path);
                if (hidden == 0)
                {
                    // Hide alone writes nothing then. With other steps those still run, and the name does not say hidden.
                    if (steps.All(s => s == "hide")) return null;
                    cover = null;
                    int at = suffix.IndexOf(hideSuffix, StringComparison.Ordinal);
                    if (at >= 0) suffix = suffix.Remove(at, hideSuffix.Length);
                }
                else ImageActions.Cover(work, cover);
            }

            if (maxWidth is int mw && work.Width > mw)
            {
                int h = Math.Max(1, (int)Math.Round(work.Height * (mw / (double)work.Width))); // a very wide, flat image would round to 0
                float kx = mw / (float)work.Width, ky = h / (float)work.Height;
                cover = cover?.Select(p => p.Select(q => new PointF(q.X * kx, q.Y * ky)).ToArray()).ToList();
                var resized = new Bitmap(mw, h);
                using (var g = Graphics.FromImage(resized))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using var wrap = new ImageAttributes();
                    wrap.SetWrapMode(WrapMode.TileFlipXY); // without it the bicubic kernel blends the edge with transparent black
                    g.DrawImage(work, new Rectangle(0, 0, mw, h), 0, 0, work.Width, work.Height, GraphicsUnit.Pixel, wrap);
                }
                work.Dispose();
                work = resized;
            }

            if (cutout)
            {
                var cut = ImageActions.RemoveBackground(work);
                work.Dispose();
                work = cut;
            }

            if (cover is not null)
            {
                // Again on the final pixels: the cut-out may have made the black see-through, and resizing blends its edge.
                ImageActions.Cover(work, cover);
                ImageActions.ClearUnderTransparent(work);
            }

            if (!strip)
            {
                foreach (var p in props)
                {
                    if (!KeepTag(p.Id)) continue;
                    if (p.Id is 0xA002 or 0xA003) SetLong(p, (uint)(p.Id == 0xA002 ? work.Width : work.Height));
                    try { work.SetPropertyItem(p); } catch { /* some tags are not writable */ }
                }
            }

            string baseName = Path.GetFileNameWithoutExtension(path) + suffix;
            string target = UniquePath(Path.GetDirectoryName(path)!, baseName, ext);
            if (ext == "webp") ImageActions.SaveWebp(work, target);
            else if (ext == "pdf") ImageActions.SavePdf(work, target);
            else Save(work, target, ext, compress ? 70L : 92L);
            return target;
        }
        finally
        {
            work.Dispose();
        }
    }

    /// <summary>
    /// The polygons to paint over and how many values they cover, read by OCR at full size. Text that could not be read
    /// (no models, out of time, an error) is a failure: nothing may be called hidden then.
    /// </summary>
    private static (List<PointF[]> Polygons, int Count) FindToHide(Bitmap work, string path)
    {
        var page = Ocr.Read(work, HideBudget);
        if (page is null)
        {
            Log.Write($"hide: text not read in {path} (see the ocr line above)");
            throw new UserFacingException(Lang.T("Görseldeki yazılar okunamadı, gizlenmedi", "Could not read the text in the image; nothing was hidden"));
        }
        var found = Ocr.FindSensitive(page);
        Log.Write($"hide: {found.Count} value(s) in {path}");
        return (found.SelectMany(f => f.Region).ToList(), found.Count);
    }

    /// <summary>
    /// EXIF items that still describe the new pixels. Dropped: orientation (we rotated ourselves), the stored
    /// width/height of IFD0, and the embedded thumbnail (0x0201/0x0202 and GDI+'s 0x50xx tags): after a crop it
    /// would still show the whole original photo.
    /// </summary>
    private static bool KeepTag(int id) => id is not (0x0112 or 0x0100 or 0x0101 or 0x0201 or 0x0202) && id is not (>= 0x5000 and <= 0x50FF);

    private static void SetLong(PropertyItem p, uint value)
    {
        p.Type = 4; // LONG
        p.Len = 4;
        p.Value = BitConverter.GetBytes(value);
    }

    /// <summary>
    /// Loads an image the way photo viewers show it: orientation applied to the pixels.
    /// Phones store portrait shots sideways plus a tag; ignoring it turns them on their side.
    /// </summary>
    internal static Bitmap LoadOriented(string path, out PropertyItem[] props) => ImageIO.Load(path, out props);

    private static void Save(Bitmap bmp, string target, string ext, long jpegQuality)
    {
        // Through a stream: GDI+ cannot write to a path longer than 260 characters. CreateNew: never over a file.
        using var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
        try { SaveTo(bmp, fs, ext, jpegQuality); }
        catch
        {
            fs.Dispose();
            SafeFile.TryDelete(target);
            throw;
        }
    }

    private static void SaveTo(Bitmap bmp, Stream target, string ext, long jpegQuality)
    {
        if (ext == "jpg")
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ps = new EncoderParameters(1);
            ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, jpegQuality);
            // The JPEG encoder drops alpha and stores transparent pixels as black: flatten onto white first.
            using Bitmap? flat = ImageActions.HasTransparency(bmp) ? ImageActions.FlattenOnWhite(bmp) : null;
            (flat ?? bmp).Save(target, codec, ps);
        }
        else
        {
            bmp.Save(target, ext == "bmp" ? ImageFormat.Bmp : ImageFormat.Png);
        }
    }

    internal static string UniquePath(string dir, string baseName, string ext)
    {
        string candidate = Path.Combine(dir, $"{baseName}.{ext}");
        for (int i = 2; File.Exists(candidate); i++) candidate = Path.Combine(dir, $"{baseName} ({i}).{ext}");
        return candidate;
    }

    internal static string UniqueDir(string parent, string name)
    {
        string candidate = Path.Combine(parent, name);
        for (int i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++) candidate = Path.Combine(parent, $"{name} ({i})");
        return candidate;
    }
}

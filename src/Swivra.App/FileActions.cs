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
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.#} MB".Replace('.', Lang.IsTurkish ? ',' : '.')
        : bytes >= 1024 ? $"{bytes / 1024d:0} KB"
        : $"{bytes} B";
}

internal sealed record ActionResult(bool Ok, string Title, string Detail, string? OutputPath = null, string? OutputExt = null, string? ClipboardText = null, Func<bool>? OnUi = null);
// OnUi: work that needs the STA UI thread (clipboard file list, launching an app); false turns the result into a failure.

/// <summary>
/// Input that some steps need beyond the step id, filled in by a tool panel.
/// Crop is in pixels of the image as the user sees it (EXIF orientation applied, before any rotate step).
/// </summary>
internal sealed record StepOptions(Rectangle? Crop = null, string? Password = null)
{
    public static readonly StepOptions None = new();
}

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

    private static readonly HashSet<string> ImageSteps = new() { "png", "jpg", "bmp", "webp", "pdf", "bg", "rot", "loc", "rs", "cmp", "crop" };

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

        // A step that succeeded but made no file (e.g. Hide with nothing to hide) counts as skipped.
        var ok = results.Where(r => r.Ok && (r.OutputPath is not null || fileSteps.Count == 0)).ToList();
        if (ok.Count == 0) return results[0];
        string? clip = copyPath ? string.Join(Environment.NewLine, ok.Select(r => r.ClipboardText)) : null;
        string? ext = ok.Select(r => r.OutputExt).Distinct().Count() == 1 ? ok[0].OutputExt : null;
        if (fileSteps.Count == 0 && ok.Count == results.Count)
            return new ActionResult(true, Lang.T("Kopyalandı", "Copied"), Lang.T($"{ok.Count} yol panoda", $"{ok.Count} paths copied"), ClipboardText: clip);
        string detail = ok.Count == results.Count
            ? Lang.T($"{ok.Count} dosya hazır", $"{ok.Count} files ready")
            : Lang.T($"{ok.Count}/{results.Count} dosya hazır, {results.Count - ok.Count} atlandı", $"{ok.Count}/{results.Count} files ready, {results.Count - ok.Count} skipped");
        return new ActionResult(true, Lang.T("Hazır", "Ready"), detail, ok[0].OutputPath, ext, clip);
    }

    private static ActionResult RunOne(string path, List<string> fileSteps, bool copyPath, StepOptions options)
    {
        string kind = FileKinds.KindOf(Path.GetExtension(path));
        var supported = kind == "pdf" ? PdfActions.Steps : kind == "doc" ? DocActions.Steps : ImageSteps;

        string? missing = fileSteps.FirstOrDefault(s => kind == "other" || !supported.Contains(s));
        if (missing is not null)
            return new ActionResult(false, Lang.T("Yakında", "Coming soon"), Lang.T($"{Label(missing)} henüz bu sürümde yok", $"{Label(missing)} is not in this version yet"));

        string finalPath = path;
        string? finalExt = null;
        string? finalName = null;
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
                }
                else if (kind == "doc")
                {
                    var r = DocActions.Run(path, fileSteps, Label);
                    if (!r.Ok || r.OutputPath is null) return r;
                    finalPath = r.OutputPath;
                    finalExt = r.OutputExt;
                    finalName = r.Detail;
                }
                else
                {
                    finalPath = RunImage(path, fileSteps, options);
                    finalExt = Path.GetExtension(finalPath).TrimStart('.').ToLowerInvariant();
                }
            }
            catch (MissingCodecException ex)
            {
                Log.Write($"action failed {path}: {ex.InnerException?.Message}");
                return new ActionResult(false, Lang.T("Açılamadı", "Could not open"), ex.Message);
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
        return new ActionResult(true, Lang.T("Hazır", "Ready"), detail, finalPath, finalExt, clip);
    }

    private static string Label(string step) => Labels.GetValueOrDefault(step, step);

    private static string RunImage(string path, List<string> steps, StepOptions options)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext == "jpeg") ext = "jpg";
        string suffix = "";
        bool strip = false, compress = false, cutout = false;
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

            if (maxWidth is int mw && work.Width > mw)
            {
                int h = (int)Math.Round(work.Height * (mw / (double)work.Width));
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

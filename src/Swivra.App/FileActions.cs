using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Swivra.App;

internal static class FileKinds
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".avif", ".heic" };
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

internal sealed record ActionResult(bool Ok, string Title, string Detail, string? OutputPath = null, string? OutputExt = null, string? ClipboardText = null);

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
        ["cmp"] = Lang.T("Sıkıştır", "Compress"), ["crop"] = Lang.T("Kırp", "Crop"), ["bg"] = Lang.T("Arka planı sil", "Remove bg"),
        ["hide"] = Lang.T("Gizle", "Hide"), ["lock"] = Lang.T("Şifrele", "Lock"), ["split"] = Lang.T("Böl", "Split"), ["merge"] = Lang.T("Birleştir", "Merge"),
        ["path"] = Lang.T("Yolu kopyala", "Copy path"), ["wa"] = "WhatsApp", ["mail"] = "Mail", ["tel"] = Lang.T("Telefona", "To phone"),
    };

    private static readonly HashSet<string> ImageSteps = new() { "png", "jpg", "bmp", "rot", "loc", "rs", "cmp", "crop" };

    public static ActionResult Run(IReadOnlyList<string> files, IReadOnlyList<string> steps, StepOptions? options = null)
    {
        options ??= StepOptions.None;
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

        var results = files.Select(f => RunOne(f, fileSteps, copyPath, options)).ToList();
        if (results.Count == 1) return results[0];

        var ok = results.Where(r => r.Ok).ToList();
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
        var supported = kind == "pdf" ? PdfActions.Steps : ImageSteps;

        string? missing = fileSteps.FirstOrDefault(s => (kind != "img" && kind != "pdf") || !supported.Contains(s));
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
                else
                {
                    finalPath = RunImage(path, fileSteps, options);
                    finalExt = Path.GetExtension(finalPath).TrimStart('.').ToLowerInvariant();
                }
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
        bool strip = false, compress = false;
        int rotations = 0;
        int? maxWidth = null;

        foreach (string s in steps)
        {
            switch (s)
            {
                case "crop": suffix += Lang.T("-kirpik", "-cropped"); break;
                case "png" or "jpg" or "bmp": ext = s; break;
                case "rot": rotations++; suffix += Lang.T("-dondu", "-rotated"); break;
                case "loc": strip = true; suffix += Lang.T("-temiz", "-clean"); break;
                case "rs": maxWidth = 1080; suffix += "-1080"; break;
                case "cmp": compress = true; ext = "jpg"; suffix += Lang.T("-kucuk", "-small"); break;
            }
        }

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
                    g.DrawImage(work, 0, 0, mw, h);
                }
                work.Dispose();
                work = resized;
            }

            if (!strip)
            {
                foreach (var p in props)
                {
                    if (p.Id == 0x0112) continue; // orientation would be wrong after our own rotation
                    try { work.SetPropertyItem(p); } catch { /* some tags are not writable */ }
                }
            }

            string baseName = Path.GetFileNameWithoutExtension(path) + suffix;
            string target = UniquePath(Path.GetDirectoryName(path)!, baseName, ext);
            Save(work, target, ext, compress ? 70L : 92L);
            return target;
        }
        finally
        {
            work.Dispose();
        }
    }

    /// <summary>
    /// Loads an image the way photo viewers show it: EXIF orientation applied to the pixels.
    /// Phones store portrait shots sideways plus a tag; ignoring it turns them on their side.
    /// The file is read into memory so it is not locked while we work.
    /// </summary>
    internal static Bitmap LoadOriented(string path, out PropertyItem[] props)
    {
        using var ms = new MemoryStream(File.ReadAllBytes(path));
        using var img = Image.FromStream(ms);
        props = img.PropertyItems;
        var bmp = new Bitmap(img);
        var orientation = props.FirstOrDefault(p => p.Id == 0x0112);
        if (orientation?.Value is { Length: >= 2 } v)
        {
            RotateFlipType? flip = BitConverter.ToUInt16(v, 0) switch
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
        return bmp;
    }

    private static void Save(Bitmap bmp, string target, string ext, long jpegQuality)
    {
        if (ext == "jpg")
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ps = new EncoderParameters(1);
            ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, jpegQuality);
            bmp.Save(target, codec, ps);
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

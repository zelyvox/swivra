using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Cark.App;

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
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.#} MB".Replace('.', ',')
        : bytes >= 1024 ? $"{bytes / 1024d:0} KB"
        : $"{bytes} B";
}

internal sealed record ActionResult(bool Ok, string Title, string Detail, string? OutputPath = null, string? OutputExt = null, string? ClipboardText = null);

/// <summary>
/// Runs a chain of wheel steps on one file. The original is never modified;
/// output goes next to it under a new, non-colliding name.
/// v0 implements image steps with GDI+; everything else reports "not yet".
/// </summary>
internal static class FileActions
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        ["png"] = "PNG", ["jpg"] = "JPG", ["bmp"] = "BMP", ["webp"] = "WebP", ["pdf"] = "PDF", ["docx"] = "Word", ["txt"] = "Metin",
        ["rot"] = "Döndür", ["loc"] = "Konumu sil", ["rs"] = "Boyutlandır", ["cmp"] = "Sıkıştır", ["crop"] = "Kırp", ["bg"] = "Arka planı sil",
        ["hide"] = "Gizle", ["lock"] = "Şifrele", ["split"] = "Böl", ["merge"] = "Birleştir",
        ["path"] = "Yolu kopyala", ["wa"] = "WhatsApp", ["mail"] = "Mail", ["tel"] = "Telefona",
    };

    private static readonly HashSet<string> ImageSteps = new() { "png", "jpg", "bmp", "rot", "loc", "rs", "cmp" };

    public static ActionResult Run(string path, IReadOnlyList<string> steps)
    {
        string kind = FileKinds.KindOf(Path.GetExtension(path));
        var fileSteps = steps.Where(s => s != "path").ToList();
        bool copyPath = steps.Contains("path");

        string? missing = fileSteps.FirstOrDefault(s => kind != "img" || !ImageSteps.Contains(s));
        if (missing is not null)
        {
            string label = Labels.GetValueOrDefault(missing, missing);
            return new ActionResult(false, "Yakında", $"{label} henüz bu sürümde yok");
        }

        string finalPath = path;
        if (fileSteps.Count > 0)
        {
            try
            {
                finalPath = RunImage(path, fileSteps);
            }
            catch (Exception ex)
            {
                Log.Write($"action failed {path}: {ex}");
                return new ActionResult(false, "Olmadı", "Dosya işlenemedi");
            }
        }

        string? clip = copyPath ? finalPath : null;
        string name = Path.GetFileName(finalPath);
        if (fileSteps.Count == 0) return new ActionResult(true, "Kopyalandı", "Yol panoda", ClipboardText: clip);
        string detail = copyPath ? $"{name} · yolu panoda" : name;
        return new ActionResult(true, "Hazır", detail, finalPath, Path.GetExtension(finalPath).TrimStart('.').ToLowerInvariant(), clip);
    }

    private static string RunImage(string path, List<string> steps)
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
                case "png" or "jpg" or "bmp": ext = s; break;
                case "rot": rotations++; suffix += "-dondu"; break;
                case "loc": strip = true; suffix += "-temiz"; break;
                case "rs": maxWidth = 1080; suffix += "-1080"; break;
                case "cmp": compress = true; ext = "jpg"; suffix += "-kucuk"; break;
            }
        }

        using var source = LoadDetached(path, out PropertyItem[] props);
        Bitmap work = new(source);
        try
        {
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

    private static Image LoadDetached(string path, out PropertyItem[] props)
    {
        // Read into memory so the source file is not locked while we work.
        var bytes = File.ReadAllBytes(path);
        var ms = new MemoryStream(bytes);
        var img = Image.FromStream(ms);
        props = img.PropertyItems;
        return img;
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

    private static string UniquePath(string dir, string baseName, string ext)
    {
        string candidate = Path.Combine(dir, $"{baseName}.{ext}");
        for (int i = 2; File.Exists(candidate); i++) candidate = Path.Combine(dir, $"{baseName} ({i}).{ext}");
        return candidate;
    }
}

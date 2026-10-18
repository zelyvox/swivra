using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;
using System.Text;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Cark.App;

/// <summary>
/// PDF steps: render to images (PDFium), extract text (PdfPig), rotate, split and merge (PDFsharp 6.2.x, MIT).
/// The original is never touched; every output goes next to it under a non-colliding name.
/// </summary>
internal static class PdfActions
{
    public static readonly HashSet<string> Steps = new() { "png", "jpg", "txt", "rot", "split", "merge" };

    private const int RenderDpi = 200;
    private const int JpgQuality = 90;

    /// <summary>Steps whose output is not a single PDF, so nothing can follow them.</summary>
    private static readonly HashSet<string> Terminal = new() { "png", "jpg", "txt", "split" };

    private sealed class PdfFailure(ActionResult result) : Exception
    {
        public ActionResult Result { get; } = result;
    }

    /// <summary>Runs a chain of PDF steps; the returned result carries the final output path and no clipboard text.</summary>
    public static ActionResult Run(string path, IReadOnlyList<string> steps, Func<string, string> label)
    {
        for (int i = 0; i < steps.Count - 1; i++)
        {
            if (Terminal.Contains(steps[i]))
                return new ActionResult(false, "Olmadı", $"{label(steps[i])} zincirin son adımı olmalı");
        }

        string origDir = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string? temp = null;
        var created = new List<string>();
        try
        {
            string input = path;
            string suffix = "";
            string output = path;
            bool isFolder = false;

            for (int i = 0; i < steps.Count; i++)
            {
                bool last = i == steps.Count - 1;
                string baseName = name + suffix;
                switch (steps[i])
                {
                    case "rot":
                        suffix += "-dondu";
                        string rotTarget;
                        if (last) rotTarget = FileActions.UniquePath(origDir, name + suffix, "pdf");
                        else
                        {
                            temp ??= Directory.CreateTempSubdirectory("cark-").FullName;
                            rotTarget = Path.Combine(temp, $"{i}.pdf");
                        }
                        Rotate(input, rotTarget);
                        if (last) created.Add(rotTarget);
                        input = output = rotTarget;
                        break;
                    case "split":
                        output = Split(input, origDir, baseName, created);
                        isFolder = true;
                        break;
                    case "png" or "jpg":
                        (output, isFolder) = Render(input, origDir, baseName, steps[i], created);
                        break;
                    case "txt":
                        output = ExtractTxt(input, origDir, baseName, created);
                        break;
                    default:
                        return new ActionResult(false, "Yakında", $"{label(steps[i])} henüz bu sürümde yok");
                }
            }

            string? ext = isFolder ? null : Path.GetExtension(output).TrimStart('.').ToLowerInvariant();
            return new ActionResult(true, "Hazır", Path.GetFileName(output), output, ext);
        }
        catch (PdfFailure f)
        {
            Cleanup(created);
            return f.Result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Cleanup(created);
            Log.Write($"pdf io failed {path}: {ex}");
            return new ActionResult(false, "Olmadı", "Dosya işlenemedi");
        }
        catch (Exception ex)
        {
            Cleanup(created);
            Log.Write($"pdf open/process failed {path}: {ex}");
            return new ActionResult(false, "Açılamadı", "PDF parolalı ya da bozuk");
        }
        finally
        {
            if (temp is not null)
            {
                try { Directory.Delete(temp, true); } catch { /* temp leftovers are harmless */ }
            }
        }
    }

    /// <summary>Merges the PDFs in the given order into one file next to the first.</summary>
    public static ActionResult Merge(IReadOnlyList<string> files)
    {
        string target = FileActions.UniquePath(Path.GetDirectoryName(files[0])!, Path.GetFileNameWithoutExtension(files[0]) + "-birlesik", "pdf");
        try
        {
            using var output = new PdfDocument();
            foreach (string f in files)
            {
                using var doc = PdfReader.Open(f, PdfDocumentOpenMode.Import);
                foreach (PdfPage page in doc.Pages) output.AddPage(page);
            }
            output.Save(target);
            return new ActionResult(true, "Hazır", Path.GetFileName(target), target, "pdf");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Cleanup([target]);
            Log.Write($"pdf merge io failed: {ex}");
            return new ActionResult(false, "Olmadı", "Dosya işlenemedi");
        }
        catch (Exception ex)
        {
            Cleanup([target]);
            Log.Write($"pdf merge failed: {ex}");
            return new ActionResult(false, "Açılamadı", "PDF parolalı ya da bozuk");
        }
    }

    /// <summary>Text of the first <paramref name="maxPages"/> pages, one entry per page.</summary>
    public static List<string> ExtractPages(string path, int maxPages = int.MaxValue)
    {
        var pages = new List<string>();
        using var doc = UglyToad.PdfPig.PdfDocument.Open(path);
        foreach (var page in doc.GetPages().Take(maxPages))
            pages.Add(ContentOrderTextExtractor.GetText(page));
        return pages;
    }

    private static void Rotate(string input, string target)
    {
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        foreach (PdfPage page in doc.Pages) page.Rotate = (page.Rotate + 90) % 360;
        doc.Save(target);
    }

    private static string Split(string input, string dir, string baseName, List<string> created)
    {
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        int count = doc.PageCount;
        int digits = Math.Max(2, count.ToString().Length);
        string folder = FileActions.UniqueDir(dir, baseName + "-bolum");
        Directory.CreateDirectory(folder);
        created.Add(folder);
        for (int i = 0; i < count; i++)
        {
            using var part = new PdfDocument();
            part.AddPage(doc.Pages[i]);
            part.Save(Path.Combine(folder, $"{baseName}-{(i + 1).ToString().PadLeft(digits, '0')}.pdf"));
        }
        return folder;
    }

    private static (string Path, bool IsFolder) Render(string input, string dir, string baseName, string ext, List<string> created)
    {
        byte[] bytes = File.ReadAllBytes(input);
        int count;
        using (var ms = new MemoryStream(bytes)) count = PDFtoImage.Conversion.GetPageCount(ms);
        if (count < 1) throw new InvalidDataException("pdf has no pages");

        string folder = "";
        string single = "";
        int digits = Math.Max(2, count.ToString().Length);
        if (count > 1)
        {
            folder = FileActions.UniqueDir(dir, baseName + "-sayfalar");
            Directory.CreateDirectory(folder);
            created.Add(folder);
        }
        else
        {
            single = FileActions.UniquePath(dir, baseName, ext);
            created.Add(single);
        }

        var options = new PDFtoImage.RenderOptions(Dpi: RenderDpi);
        for (int i = 0; i < count; i++)
        {
            using var ms = new MemoryStream(bytes);
            using SKBitmap bmp = PDFtoImage.Conversion.ToImage(ms, i, options: options);
            using var image = SKImage.FromBitmap(bmp);
            using var data = ext == "jpg" ? image.Encode(SKEncodedImageFormat.Jpeg, JpgQuality) : image.Encode(SKEncodedImageFormat.Png, 100);
            string target = count > 1
                ? Path.Combine(folder, $"{baseName}-{(i + 1).ToString().PadLeft(digits, '0')}.{ext}")
                : single;
            using var fs = File.Create(target);
            data.SaveTo(fs);
        }
        return count > 1 ? (folder, true) : (single, false);
    }

    private static string ExtractTxt(string input, string dir, string baseName, List<string> created)
    {
        var pages = ExtractPages(input);
        if (pages.All(string.IsNullOrWhiteSpace))
            throw new PdfFailure(new ActionResult(false, "Metin yok", "Taranmış PDF olabilir"));
        string target = FileActions.UniquePath(dir, baseName, "txt");
        created.Add(target);
        string nl = Environment.NewLine;
        File.WriteAllText(target, string.Join(nl + nl, pages.Select(p => p.Trim())), new UTF8Encoding(false));
        return target;
    }

    private static void Cleanup(IEnumerable<string> paths)
    {
        foreach (string p in paths)
        {
            try
            {
                if (Directory.Exists(p)) Directory.Delete(p, true);
                else if (File.Exists(p)) File.Delete(p);
            }
            catch { /* best effort */ }
        }
    }
}

using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Security;
using SkiaSharp;
using System.Text;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Cark.App;

/// <summary>
/// PDF steps: render to images (PDFium), extract text (PdfPig), rotate, split, merge, compress and encrypt (PDFsharp 6.2.x, MIT).
/// The original is never touched; every output goes next to it under a non-colliding name.
/// </summary>
internal static class PdfActions
{
    public static readonly HashSet<string> Steps = new() { "png", "jpg", "txt", "rot", "split", "merge", "cmp", "lock" };

    private const int RenderDpi = 200;
    private const int JpgQuality = 90;
    private const int CompressMaxSide = 1600;
    private const int CompressQuality = 70;
    private const long CompressMinStream = 10 * 1024;   // smaller images are not worth a lossy pass
    private const double CompressMinGain = 0.05;

    /// <summary>Steps nothing can follow: the output is not a single PDF, or (lock) later steps would need the password.</summary>
    private static readonly HashSet<string> Terminal = new() { "png", "jpg", "txt", "split", "lock" };

    private sealed class PdfFailure(ActionResult result) : Exception
    {
        public ActionResult Result { get; } = result;
    }

    /// <summary>Runs a chain of PDF steps; the returned result carries the final output path and no clipboard text.</summary>
    public static ActionResult Run(string path, IReadOnlyList<string> steps, Func<string, string> label, StepOptions options)
    {
        for (int i = 0; i < steps.Count - 1; i++)
        {
            if (Terminal.Contains(steps[i]))
                return new ActionResult(false, "Olmadı", $"{label(steps[i])} zincirin son adımı olmalı");
        }

        if (steps.Contains("lock") && string.IsNullOrEmpty(options.Password))
            return new ActionResult(false, "Şifrele", "Parola gerekli");
        if (IsEncrypted(path))
            return new ActionResult(false, "Olmadı", "Şifreli PDF açılamadı");

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

            // The last step writes next to the original; earlier ones go to a temp folder.
            string StepTarget(int index, bool isLast, string stepSuffix)
            {
                if (!isLast)
                {
                    temp ??= Directory.CreateTempSubdirectory("cark-").FullName;
                    return Path.Combine(temp, $"{index}.pdf");
                }
                string target = FileActions.UniquePath(origDir, name + stepSuffix, "pdf");
                created.Add(target);
                return target;
            }

            for (int i = 0; i < steps.Count; i++)
            {
                bool last = i == steps.Count - 1;
                string baseName = name + suffix;
                switch (steps[i])
                {
                    case "rot":
                        suffix += "-dondu";
                        string rotTarget = StepTarget(i, last, suffix);
                        Rotate(input, rotTarget);
                        input = output = rotTarget;
                        break;
                    case "cmp":
                        string cmpTarget = StepTarget(i, last, suffix + "-kucuk");
                        long before = new FileInfo(input).Length;
                        Compress(input, cmpTarget);
                        long after = new FileInfo(cmpTarget).Length;
                        if (after > before * (1 - CompressMinGain))
                        {
                            // Not worth it: drop the attempt and carry on with the unchanged input.
                            Cleanup([cmpTarget]);
                            created.Remove(cmpTarget);
                            if (!last) break;
                            if (input == path) return new ActionResult(true, "Zaten küçük", "Daha fazla küçülmüyor");
                            // Earlier steps already produced a file (in temp); it becomes the final output.
                            string keep = FileActions.UniquePath(origDir, name + suffix, "pdf");
                            File.Copy(input, keep);
                            created.Add(keep);
                            input = output = keep;
                            break;
                        }
                        suffix += "-kucuk";
                        input = output = cmpTarget;
                        if (last) return new ActionResult(true, "Hazır", $"{Path.GetFileName(cmpTarget)} · {FileKinds.HumanSize(before)} → {FileKinds.HumanSize(after)}", cmpTarget, "pdf");
                        break;
                    case "lock":
                        suffix += "-sifreli";
                        string lockTarget = StepTarget(i, last, suffix);
                        Lock(input, lockTarget, options.Password!);
                        input = output = lockTarget;
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

    /// <summary>Re-encodes safe raster images as downscaled JPEG and compresses content streams.</summary>
    private static void Compress(string input, string target)
    {
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        foreach (PdfObject obj in doc.Internals.GetAllObjects())
        {
            if (obj is PdfDictionary { Stream: not null } dict && dict.Elements.GetName("/Subtype") == "/Image")
            {
                try { RecompressImage(dict); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { Log.Write($"pdf image skipped: {ex.Message}"); }
            }
        }
        doc.Options.CompressContentStreams = true;
        doc.Save(target);
    }

    private static void RecompressImage(PdfDictionary img)
    {
        var el = img.Elements;
        if (el.ContainsKey("/SMask") || el.ContainsKey("/Mask") || el.ContainsKey("/Decode") || el.GetBoolean("/ImageMask")) return;
        if (el.GetInteger("/BitsPerComponent") != 8) return;
        int width = el.GetInteger("/Width"), height = el.GetInteger("/Height");
        if (width < 1 || height < 1 || img.Stream.Length < CompressMinStream) return;

        int comps = ColorComponents(el["/ColorSpace"]);
        if (comps == 0) return;

        using SKBitmap? source = SingleFilter(el["/Filter"]) switch
        {
            "/DCTDecode" => SKBitmap.Decode(img.Stream.Value),
            "/FlateDecode" => FromRaw(img.Stream.UnfilteredValue, width, height, comps),
            _ => null,
        };
        if (source is null || source.Width != width || source.Height != height) return;

        double scale = Math.Min(1.0, CompressMaxSide / (double)Math.Max(width, height));
        int nw = Math.Max(1, (int)Math.Round(width * scale)), nh = Math.Max(1, (int)Math.Round(height * scale));
        // Always written as RGB: whether Skia emits a one-channel JPEG for gray input is not
        // guaranteed, and a JPEG whose channel count disagrees with /ColorSpace breaks the page.
        using SKBitmap rgba = Resized(source, nw, nh);
        using var image = SKImage.FromBitmap(rgba);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, CompressQuality);
        if (data is null || data.Size >= img.Stream.Length) return;

        img.Stream.Value = data.ToArray();
        el.SetInteger("/Width", nw);
        el.SetInteger("/Height", nh);
        el.SetName("/Filter", "/DCTDecode");
        if (comps == 1) el.SetName("/ColorSpace", "/DeviceRGB");
        el.Remove("/DecodeParms");
    }

    /// <summary>1 for gray, 3 for RGB, 0 for anything we leave alone (Indexed, CMYK, Lab, ICC with other counts...).</summary>
    private static int ColorComponents(PdfItem? cs)
    {
        if (cs is PdfReference r) cs = r.Value;
        switch (cs)
        {
            case PdfName n:
                return n.Value == "/DeviceRGB" ? 3 : n.Value == "/DeviceGray" ? 1 : 0;
            case PdfArray { Elements.Count: 2 } a when a.Elements[0] is PdfName { Value: "/ICCBased" }:
                var profile = (a.Elements[1] as PdfReference)?.Value as PdfDictionary;
                int n2 = profile?.Elements.GetInteger("/N") ?? 0;
                return n2 is 1 or 3 ? n2 : 0;
            default:
                return 0;
        }
    }

    private static string? SingleFilter(PdfItem? f)
    {
        if (f is PdfReference r) f = r.Value;
        if (f is PdfArray { Elements.Count: 1 } a) f = a.Elements[0];
        return (f as PdfName)?.Value;
    }

    /// <summary>Raw 8-bit gray or RGB samples to an opaque RGBA bitmap; null when the length does not fit.</summary>
    private static SKBitmap? FromRaw(byte[] raw, int width, int height, int comps)
    {
        if (raw.Length != (long)width * height * comps) return null;
        var px = new byte[width * height * 4];
        for (int i = 0, o = 0; i < width * height; i++, o += 4)
        {
            px[o] = raw[i * comps];
            px[o + 1] = raw[i * comps + (comps == 3 ? 1 : 0)];
            px[o + 2] = raw[i * comps + (comps == 3 ? 2 : 0)];
            px[o + 3] = 255;
        }
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        return bmp;
    }

    private static SKBitmap Resized(SKBitmap src, int w, int h)
    {
        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var image = SKImage.FromBitmap(src);
        using var canvas = new SKCanvas(dst);
        canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return dst;
    }

    /// <summary>AES-256 (V5), same password for opening and for owner rights.</summary>
    private static void Lock(string input, string target, string password)
    {
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        doc.SecuritySettings.UserPassword = password;
        doc.SecuritySettings.OwnerPassword = password;
        doc.SecurityHandler.SetEncryption(PdfDefaultEncryption.V5);
        doc.Save(target);
    }

    /// <summary>True when the PDF needs a password to open (PDFsharp refuses it without one).</summary>
    private static bool IsEncrypted(string path)
    {
        try
        {
            using var doc = PdfReader.Open(path, PdfDocumentOpenMode.InformationOnly);
            return false;
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        catch
        {
            return false; // other failures surface in the actual step
        }
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

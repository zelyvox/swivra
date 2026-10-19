using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Win32;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace Swivra.App;

/// <summary>
/// Document and "other file" steps: txt/md/docx conversion (OpenXml, PDFsharp, optional LibreOffice),
/// zip compress, AES-256 zip lock and masking of sensitive text (Radar validators).
/// Originals are never modified; outputs go next to them under a non-colliding name.
/// </summary>
internal static class DocActions
{
    // cmp and lock are listed so a chain like cmp>lock gets a clear message instead of "not in this version".
    public static readonly HashSet<string> Steps = new() { "pdf", "txt", "docx", "hide", "cmp", "lock" };

    private const string Mask = "████";
    private static readonly HashSet<string> PlainExts = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".md" };

    private sealed class DocFailure(string title, string detail) : Exception(detail)
    {
        public string Title { get; } = title;
    }

    // ---------- dispatch ----------

    /// <summary>Runs one document step on one file. Documents take a single step; chains are refused honestly.</summary>
    public static ActionResult Run(string path, IReadOnlyList<string> steps, Func<string, string> label)
    {
        if (steps.Count != 1)
            return new ActionResult(false, Lang.T("Olmadı", "Failed"), Lang.T("Belgelerde tek adım uygulanır", "Documents take one step at a time"));
        string step = steps[0];
        if (step is "cmp" or "lock")
            return new ActionResult(false, label(step), Lang.T($"{label(step)} zincirde tek başına olmalı", $"{label(step)} must be alone in the chain"));
        try
        {
            return step switch
            {
                "pdf" => ToPdf(path),
                "txt" => ToText(path),
                "docx" => ToDocx(path),
                "hide" => Hide(path),
                _ => new ActionResult(false, Lang.T("Yakında", "Coming soon"), Lang.T($"{label(step)} henüz bu sürümde yok", $"{label(step)} is not in this version yet")),
            };
        }
        catch (DocFailure f)
        {
            return new ActionResult(false, f.Title, f.Message);
        }
    }

    private static string Ext(string path) => Path.GetExtension(path).ToLowerInvariant();

    private static ActionResult Done(string output, string? note = null)
    {
        string name = Path.GetFileName(output);
        return new ActionResult(true, Lang.T("Hazır", "Ready"), note is null ? name : $"{name} · {note}", output, Path.GetExtension(output).TrimStart('.').ToLowerInvariant());
    }

    private static string OutPath(string source, string suffix, string ext) =>
        FileActions.UniquePath(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source) + suffix, ext);

    // ---------- conversions ----------

    private static ActionResult ToPdf(string path)
    {
        string ext = Ext(path);
        if (ViaOffice(path, "pdf") is ActionResult office) return office;

        if (!PlainExts.Contains(ext) && ext != ".docx") throw NeedOffice(ext, "PDF");

        string text = ext == ".docx" ? DocxText(path) : ReadText(path, out _);
        string output = OutPath(path, "", "pdf");
        try
        {
            WritePdf(text, output);
        }
        catch
        {
            TryDelete(output);
            throw;
        }
        return Done(output, ext == ".docx"
            ? Lang.T("yalnız metin, düzen ve görseller yok", "text only, no layout or images")
            : Lang.T("düz metin", "plain text"));
    }

    private static ActionResult ToText(string path)
    {
        string ext = Ext(path);
        if (ext == ".docx")
        {
            string output = OutPath(path, "", "txt");
            File.WriteAllText(output, DocxText(path), new UTF8Encoding(false));
            return Done(output, Lang.T("yalnız metin", "text only"));
        }
        return ViaOffice(path, "txt") ?? throw NeedOffice(ext, "txt");
    }

    private static ActionResult ToDocx(string path)
    {
        string ext = Ext(path);
        if (PlainExts.Contains(ext))
        {
            string text = ReadText(path, out _);
            string output = OutPath(path, "", "docx");
            try
            {
                WriteDocx(text, output);
            }
            catch
            {
                TryDelete(output);
                throw;
            }
            return Done(output, Lang.T("düz metin", "plain text"));
        }
        return ViaOffice(path, "docx") ?? throw NeedOffice(ext, "docx");
    }

    /// <summary>Word first, then LibreOffice (OfficeConvert); null when neither is there or both failed.</summary>
    private static ActionResult? ViaOffice(string path, string ext)
    {
        string target = OutPath(path, "", ext);
        var r = OfficeConvert.Convert(path, target);
        return r is null ? null : Done(r.Path, r.Engine);
    }

    private static DocFailure NeedOffice(string ext, string target) =>
        new(Lang.T("Word ya da LibreOffice gerekli", "Word or LibreOffice required"),
            Lang.T($"{ext} dosyasını {target} yapmak için Word ya da LibreOffice kurulu olmalı", $"Converting {ext} to {target} needs Word or LibreOffice installed"));

    // ---------- text helpers ----------

    /// <summary>Reads a text file: BOM first, then strict UTF-8, then Windows-1254 (older Turkish files).</summary>
    internal static string ReadText(string path, out Encoding encoding)
    {
        byte[] b = File.ReadAllBytes(path);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) { encoding = new UTF8Encoding(true); return Encoding.UTF8.GetString(b, 3, b.Length - 3); }
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) { encoding = new UnicodeEncoding(false, true); return Encoding.Unicode.GetString(b, 2, b.Length - 2); }
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) { encoding = new UnicodeEncoding(true, true); return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2); }
        try
        {
            encoding = new UTF8Encoding(false);
            return new UTF8Encoding(false, true).GetString(b);
        }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            encoding = Encoding.GetEncoding(1254);
            return encoding.GetString(b);
        }
    }

    private static IEnumerable<string> Lines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static void WriteDocx(string text, string output)
    {
        using var doc = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        foreach (string line in Lines(text))
        {
            var run = new Run(new Text(line) { Space = SpaceProcessingModeValues.Preserve });
            body.Append(new Paragraph(run));
        }
        main.Document = new Document(body);
        main.Document.Save();
    }

    /// <summary>All paragraphs of the main body (tables included) as plain text, tabs and line breaks kept.</summary>
    internal static string DocxText(string path)
    {
        using var doc = OpenDocxRead(path);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return "";
        var sb = new StringBuilder();
        foreach (var p in body.Descendants<Paragraph>())
        {
            foreach (var el in p.Descendants())
            {
                switch (el)
                {
                    case Text t: sb.Append(t.Text); break;
                    case TabChar: sb.Append('\t'); break;
                    case Break: sb.Append('\n'); break;
                }
            }
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    private static WordprocessingDocument OpenDocxRead(string path)
    {
        // Read through a share-friendly stream so a document open in Word can still be converted.
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            return WordprocessingDocument.Open(fs, false);
        }
        catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or FileFormatException)
        {
            fs.Dispose();
            throw new DocFailure(Lang.T("Olmadı", "Failed"), Lang.T("Word dosyası açılamadı", "Could not open the Word file"));
        }
    }

    // ---------- native PDF writer (text only) ----------

    private sealed class WindowsFontResolver : IFontResolver
    {
        private static readonly string Dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        private static readonly string[][] Families = [["segoeui.ttf", "segoeuib.ttf"], ["arial.ttf", "arialbd.ttf"]];

        public static string? Regular => Families.Select(f => f[0]).FirstOrDefault(f => File.Exists(Path.Combine(Dir, f)));

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            var fam = Families.FirstOrDefault(f => File.Exists(Path.Combine(Dir, f[0])));
            if (fam is null) return null;
            string file = isBold && File.Exists(Path.Combine(Dir, fam[1])) ? fam[1] : fam[0];
            return new FontResolverInfo(file);
        }

        public byte[]? GetFont(string faceName) => File.ReadAllBytes(Path.Combine(Dir, faceName));
    }

    private static void EnsureFontResolver()
    {
        if (WindowsFontResolver.Regular is null)
            throw new DocFailure(Lang.T("Olmadı", "Failed"), Lang.T("PDF için uygun yazı tipi bulunamadı", "No suitable font found for the PDF"));
        if (GlobalFontSettings.FontResolver is null)
        {
            try { GlobalFontSettings.FontResolver = new WindowsFontResolver(); }
            catch (InvalidOperationException) { /* another component already set a resolver */ }
        }
    }

    /// <summary>Plain text on A4 pages with word wrap and pagination. Segoe UI / Arial from the system covers Turkish.</summary>
    private static void WritePdf(string text, string output)
    {
        EnsureFontResolver();
        const double margin = 56, size = 11, lineH = size * 1.4;
        var doc = new PdfDocument();
        var font = new XFont("Segoe UI", size, XFontStyleEx.Regular);
        PdfPage? page = null;
        XGraphics? gfx = null;
        double y = 0, maxW = 0, maxY = 0;

        void NewPage()
        {
            gfx?.Dispose();
            page = doc.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            gfx = XGraphics.FromPdfPage(page);
            maxW = page.Width.Point - 2 * margin;
            maxY = page.Height.Point - margin;
            y = margin;
        }

        void Emit(string line)
        {
            if (page is null || y + lineH > maxY) NewPage();
            if (line.Length > 0) gfx!.DrawString(line, font, XBrushes.Black, new XPoint(margin, y + size), XStringFormats.Default);
            y += lineH;
        }

        NewPage();
        foreach (string raw in Lines(text))
        {
            string line = raw.Replace("\t", "    ");
            if (line.Length == 0) { Emit(""); continue; }
            var cur = new StringBuilder();
            foreach (string word in SplitWords(line))
            {
                string tryLine = cur.ToString() + word;
                if (gfx!.MeasureString(tryLine, font).Width <= maxW) { cur.Append(word); continue; }
                if (cur.Length > 0) { Emit(cur.ToString().TrimEnd()); cur.Clear(); }
                string rest = word.TrimStart();
                // A single word wider than the page is cut by character.
                while (rest.Length > 0 && gfx.MeasureString(rest, font).Width > maxW)
                {
                    int n = rest.Length;
                    while (n > 1 && gfx.MeasureString(rest[..n], font).Width > maxW) n--;
                    Emit(rest[..n]);
                    rest = rest[n..];
                }
                cur.Append(rest);
            }
            Emit(cur.ToString().TrimEnd());
        }
        gfx?.Dispose();
        doc.Info.Title = Path.GetFileNameWithoutExtension(output);
        doc.Save(output);
    }

    /// <summary>Words with their trailing spaces, so measuring keeps the spacing.</summary>
    private static IEnumerable<string> SplitWords(string line)
    {
        int i = 0;
        while (i < line.Length)
        {
            int j = i;
            while (j < line.Length && line[j] != ' ') j++;
            while (j < line.Length && line[j] == ' ') j++;
            yield return line[i..j];
            i = j;
        }
    }

    // ---------- hide ----------

    private static ActionResult Hide(string path)
    {
        string ext = Ext(path);
        if (!PlainExts.Contains(ext) && ext != ".docx")
            throw new DocFailure(Lang.T("Gizle", "Hide"), Lang.T("Gizle yalnız .txt, .md ve .docx için", "Hide works on .txt, .md and .docx only"));

        string suffix = Lang.T("-gizli", "-hidden");
        string output = OutPath(path, suffix, ext.TrimStart('.'));
        int count;
        try
        {
            if (ext == ".docx")
            {
                File.Copy(path, output);
                count = MaskDocx(output);
            }
            else
            {
                string text = ReadText(path, out Encoding enc);
                string masked = MaskText(text, out count);
                if (count > 0) File.WriteAllBytes(output, enc.GetPreamble().Concat(enc.GetBytes(masked)).ToArray());
            }
        }
        catch
        {
            TryDelete(output);
            throw;
        }
        if (count == 0)
        {
            TryDelete(output);
            return new ActionResult(false, Lang.T("Gizle", "Hide"), Lang.T("Kapatılacak hassas veri bulunamadı", "No sensitive data found to cover"));
        }
        return Done(output, Lang.T($"{count} alan kapatıldı", count == 1 ? "1 item covered" : $"{count} items covered"));
    }

    /// <summary>Merges overlapping spans and replaces each with the mask.</summary>
    internal static string MaskText(string text, out int count)
    {
        var spans = MergeSpans(Radar.FindSensitiveSpans(text));
        count = spans.Count;
        if (count == 0) return text;
        var sb = new StringBuilder();
        int pos = 0;
        foreach (var (s, l) in spans)
        {
            sb.Append(text, pos, s - pos).Append(Mask);
            pos = s + l;
        }
        return sb.Append(text, pos, text.Length - pos).ToString();
    }

    private static List<(int Start, int Length)> MergeSpans(IEnumerable<(int Start, int Length)> spans)
    {
        var merged = new List<(int Start, int Length)>();
        foreach (var sp in spans.OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && sp.Start <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.Start + last.Length, sp.Start + sp.Length) - last.Start);
            }
            else merged.Add(sp);
        }
        return merged;
    }

    /// <summary>Masks paragraph by paragraph so a value split across runs is still caught. Returns the number of covered items.</summary>
    private static int MaskDocx(string copy)
    {
        int total = 0;
        using var doc = WordprocessingDocument.Open(copy, true);
        var main = doc.MainDocumentPart ?? throw new DocFailure(Lang.T("Olmadı", "Failed"), Lang.T("Word dosyası açılamadı", "Could not open the Word file"));
        var roots = new List<OpenXmlPartRootElement>();
        if (main.Document is not null) roots.Add(main.Document);
        roots.AddRange(main.HeaderParts.Select(h => (OpenXmlPartRootElement)h.Header));
        roots.AddRange(main.FooterParts.Select(f => (OpenXmlPartRootElement)f.Footer));
        foreach (var root in roots)
        {
            foreach (var p in root.Descendants<Paragraph>())
                total += MaskParagraph(p);
            root.Save();
        }
        return total;
    }

    private static int MaskParagraph(Paragraph p)
    {
        var texts = p.Descendants<Text>().ToList();
        if (texts.Count == 0) return 0;
        string full = string.Concat(texts.Select(t => t.Text));
        var spans = MergeSpans(Radar.FindSensitiveSpans(full));
        if (spans.Count == 0) return 0;

        int pos = 0, si = 0;
        foreach (var t in texts)
        {
            var sb = new StringBuilder();
            foreach (char c in t.Text)
            {
                while (si < spans.Count && pos >= spans[si].Start + spans[si].Length) si++;
                if (si < spans.Count && pos >= spans[si].Start)
                {
                    if (pos == spans[si].Start) sb.Append(Mask); // the whole mask sits in the first run the value touches
                }
                else sb.Append(c);
                pos++;
            }
            t.Text = sb.ToString();
            t.Space = SpaceProcessingModeValues.Preserve;
        }
        return spans.Count;
    }

    // ---------- zip: compress and lock ----------

    /// <summary>Files (folders are skipped) with unique entry names for a flat zip.</summary>
    private static List<(string Path, string Entry)> ZipEntries(IReadOnlyList<string> files)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<(string, string)>();
        foreach (string f in files.Where(File.Exists))
        {
            string name = Path.GetFileName(f), entry = name;
            for (int i = 2; !used.Add(entry); i++)
                entry = $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}";
            list.Add((f, entry));
        }
        return list;
    }

    private static string ZipName(IReadOnlyList<string> files, string suffix) =>
        FileActions.UniquePath(Path.GetDirectoryName(files[0])!, Path.GetFileNameWithoutExtension(files[0]) + suffix, "zip");

    /// <summary>Puts all files into one .zip next to the first file. Reports the size before and after.</summary>
    public static ActionResult Compress(IReadOnlyList<string> files)
    {
        var entries = ZipEntries(files);
        if (entries.Count == 0) return new ActionResult(false, Lang.T("Sıkıştır", "Compress"), Lang.T("Sıkıştırılacak dosya yok", "No files to compress"));
        string target = ZipName(files, "");
        try
        {
            using (var zip = System.IO.Compression.ZipFile.Open(target, ZipArchiveMode.Create))
            {
                foreach (var (p, entry) in entries)
                    zip.CreateEntryFromFile(p, entry, CompressionLevel.Optimal);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"zip failed {target}: {ex}");
            TryDelete(target);
            return new ActionResult(false, Lang.T("Olmadı", "Failed"), Lang.T("Zip oluşturulamadı", "Could not create the zip"));
        }
        long before = entries.Sum(e => new FileInfo(e.Path).Length), after = new FileInfo(target).Length;
        string sizes = $"{FileKinds.HumanSize(before)} → {FileKinds.HumanSize(after)}";
        string count = entries.Count > 1 ? Lang.T($"{entries.Count} dosya · ", $"{entries.Count} files · ") : "";
        return Done(target, count + sizes);
    }

    /// <summary>AES-256 encrypted .zip of all files (SharpZipLib). Names inside the zip are visible; contents are not.</summary>
    public static ActionResult LockZip(IReadOnlyList<string> files, string password)
    {
        if (string.IsNullOrEmpty(password)) return new ActionResult(false, Lang.T("Şifrele", "Lock"), Lang.T("Parola gerekli", "Password required"));
        var entries = ZipEntries(files);
        if (entries.Count == 0) return new ActionResult(false, Lang.T("Şifrele", "Lock"), Lang.T("Şifrelenecek dosya yok", "No files to lock"));
        string target = ZipName(files, Lang.T("-sifreli", "-locked"));
        try
        {
            using (var fs = File.Create(target))
            using (var zs = new ZipOutputStream(fs) { Password = password })
            {
                zs.SetLevel(9);
                foreach (var (p, entry) in entries)
                {
                    var ze = new ZipEntry(ZipEntry.CleanName(entry))
                    {
                        AESKeySize = 256,
                        DateTime = File.GetLastWriteTime(p),
                        CompressionMethod = ICSharpCode.SharpZipLib.Zip.CompressionMethod.Deflated,
                    };
                    zs.PutNextEntry(ze);
                    using var src = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    src.CopyTo(zs);
                    zs.CloseEntry();
                }
                zs.Finish();
            }
        }
        catch (Exception ex)
        {
            Log.Write($"zip lock failed {target}: {ex}");
            TryDelete(target);
            return new ActionResult(false, Lang.T("Olmadı", "Failed"), Lang.T("Şifreli zip oluşturulamadı", "Could not create the locked zip"));
        }
        string count = entries.Count > 1 ? Lang.T($"{entries.Count} dosya · ", $"{entries.Count} files · ") : "";
        return Done(target, count + "AES-256");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* leftover is harmless */ }
    }
}

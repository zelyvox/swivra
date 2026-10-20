using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Cell = DocumentFormat.OpenXml.Spreadsheet.Cell;
using CellValues = DocumentFormat.OpenXml.Spreadsheet.CellValues;
using CommentText = DocumentFormat.OpenXml.Spreadsheet.CommentText;
using InlineString = DocumentFormat.OpenXml.Spreadsheet.InlineString;
using SharedStringItem = DocumentFormat.OpenXml.Spreadsheet.SharedStringItem;
using SText = DocumentFormat.OpenXml.Spreadsheet.Text;
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
        int count, skipped = 0;
        try
        {
            if (ext == ".docx")
            {
                File.Copy(path, output);
                count = MaskDocx(output, out skipped);
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
        // Binary embedded objects cannot be looked into; say so rather than suggest the file is clean.
        string skippedNote = skipped == 0 ? "" : Lang.T($" · {skipped} gömülü nesne kontrol edilemedi", skipped == 1 ? " · 1 embedded object not checked" : $" · {skipped} embedded objects not checked");
        if (count == 0)
        {
            TryDelete(output);
            return new ActionResult(false, Lang.T("Gizle", "Hide"), Lang.T("Kapatılacak hassas veri bulunamadı", "No sensitive data found to cover") + skippedNote);
        }
        return Done(output, Lang.T($"{count} alan kapatıldı", count == 1 ? "1 item covered" : $"{count} items covered") + skippedNote);
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

    /// <summary>
    /// Masks every place a .docx keeps text, not only what Word shows: all story parts (body, headers, footers,
    /// footnotes, endnotes, comments, building blocks), tracked deletions, field codes and equations, legacy
    /// form-field texts, alt text, link targets, document variables and mail-merge settings, document
    /// properties, the custom XML that content controls are bound to (Word refills a bound control from it on
    /// open), SmartArt and chart text, and embedded Word, Excel and PowerPoint files. Photos lose their EXIF/XMP
    /// metadata. The thumbnail is a picture of the unmasked first page, so it always goes.
    /// Paragraph by paragraph, so a value split across runs is still caught.
    /// Returns the number of distinct values covered; <paramref name="skipped"/> counts embedded objects
    /// that cannot be opened (binary OLE, ActiveX) and were left as they are.
    /// </summary>
    private static int MaskDocx(string copy, out int skipped)
    {
        var covered = new Covered();
        using (var doc = WordprocessingDocument.Open(copy, true))
            MaskWord(doc, covered);
        skipped = covered.Unchecked;
        return covered.Count;
    }

    /// <summary>
    /// What was covered across one document and everything embedded in it. Values are counted once however
    /// often they occur (link text and target, field code and result, the two copies of a text box).
    /// </summary>
    private sealed class Covered
    {
        private readonly HashSet<string> _values = new(StringComparer.OrdinalIgnoreCase);
        public int Unchecked;
        public int Count => _values.Count;

        /// <summary>The value with sensitive items masked; remembers what it covered.</summary>
        public string? Mask(string? value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            var spans = MergeSpans(Radar.FindSensitiveSpans(value));
            Note(value, spans);
            return spans.Count == 0 ? value : MaskText(value, out _);
        }

        public void Note(string text, List<(int Start, int Length)> spans)
        {
            foreach (var (s, l) in spans) _values.Add(string.Concat(text.AsSpan(s, l).ToString().Where(c => !char.IsWhiteSpace(c))));
        }

        /// <summary>A photo that carried a location counts as one covered item of its own.</summary>
        public void NoteLocation(Uri part) => _values.Add("location:" + part);
    }

    private static void MaskWord(WordprocessingDocument doc, Covered c)
    {
        var main = doc.MainDocumentPart ?? throw new DocFailure(Lang.T("Olmadı", "Failed"), Lang.T("Word dosyası açılamadı", "Could not open the Word file"));

        var stories = new List<OpenXmlPart> { main };
        stories.AddRange(main.HeaderParts);
        stories.AddRange(main.FooterParts);
        if (main.FootnotesPart is { } fn) stories.Add(fn);
        if (main.EndnotesPart is { } en) stories.Add(en);
        if (main.WordprocessingCommentsPart is { } cm) stories.Add(cm);
        if (main.GlossaryDocumentPart is { } gl) stories.Add(gl);
        foreach (var part in stories)
        {
            if (part.RootElement is not OpenXmlPartRootElement root) continue;
            foreach (var p in root.Descendants<Paragraph>())
            {
                MaskRuns<Text>(p, c, preserveSpace: true);
                MaskRuns<DeletedText>(p, c, preserveSpace: true);
                MaskRuns<FieldCode>(p, c, preserveSpace: true);
                MaskRuns<DeletedFieldCode>(p, c, preserveSpace: true);
                MaskRuns<DocumentFormat.OpenXml.Math.Text>(p, c, preserveSpace: true);
            }
            foreach (var f in root.Descendants<SimpleField>())
                f.Instruction = c.Mask(f.Instruction?.Value);
            // Legacy form fields keep their default, help and status text in w:val attributes.
            foreach (var ff in root.Descendants<FormFieldData>())
                MaskAttributes(ff, name => name == "val", c);
            // Alt text sits in wp:docPr, pic:cNvPr, wps:cNvPr and, in the VML fallback, v:shape@alt / o:title.
            MaskAttributes(root, name => name is "descr" or "title" or "alt", c);
            MaskHyperlinks(part, c);
            root.Save();
        }

        // Document variables and the mail-merge connection (query, data source) live in settings.xml.
        if (main.DocumentSettingsPart?.Settings is { } settings)
        {
            foreach (var e in settings.Elements().Where(e => e is DocumentVariables or MailMerge))
                MaskAttributes(e, name => name == "val", c);
            settings.Save();
        }
        // Co-authors signed in with a work account are stored as "S::name@company.com::guid".
        if (main.WordprocessingPeoplePart?.RootElement is OpenXmlPartRootElement people)
        {
            MaskAttributes(people, _ => true, c);
            people.Save();
        }
        foreach (var part in main.CustomXmlParts)
            MaskXmlPart(part, c);

        MaskProperties(doc, c);
        MaskInnerParts(doc, c);
        if (doc.ThumbnailPart is { } thumb) doc.DeletePart(thumb);
    }

    /// <summary>Excel embedded in a document (or behind a chart): shared strings, cells, comments, properties.</summary>
    private static void MaskExcel(SpreadsheetDocument doc, Covered c)
    {
        if (doc.WorkbookPart is not { } wb) return;
        if (wb.SharedStringTablePart?.SharedStringTable is { } sst)
        {
            foreach (var item in sst.Elements<SharedStringItem>()) MaskRuns<SText>(item, c, preserveSpace: true);
            sst.Save();
        }
        foreach (var ws in wb.WorksheetParts)
        {
            foreach (var cell in ws.Worksheet.Descendants<Cell>())
            {
                if (cell.InlineString is { } inline) { MaskRuns<SText>(inline, c, preserveSpace: true); continue; }
                if (cell.CellFormula is not null || cell.CellValue is not { } v) continue;
                var type = cell.DataType?.Value;
                if (type == CellValues.String) { v.Text = c.Mask(v.Text)!; continue; }
                if (type is not null && type != CellValues.Number) continue; // shared strings are done above; booleans and errors hold no text
                string masked = c.Mask(v.Text)!;
                if (masked == v.Text) continue;
                // A masked number is no number any more: the cell becomes an inline string.
                cell.CellValue = null;
                cell.DataType = CellValues.InlineString;
                cell.InlineString = new InlineString(new SText(masked));
            }
            ws.Worksheet.Save();
            if (ws.WorksheetCommentsPart?.Comments is { } comments)
            {
                foreach (var ct in comments.Descendants<CommentText>()) MaskRuns<SText>(ct, c, preserveSpace: true);
                comments.Save();
            }
        }
        MaskProperties(doc, c);
        MaskInnerParts(doc, c);
    }

    /// <summary>PowerPoint embedded in a document: slides, notes, alt text, links, properties.</summary>
    private static void MaskPowerPoint(PresentationDocument doc, Covered c)
    {
        if (doc.PresentationPart is not { } pres) return;
        var parts = new List<OpenXmlPart>();
        foreach (var slide in pres.SlideParts)
        {
            parts.Add(slide);
            if (slide.NotesSlidePart is { } notes) parts.Add(notes);
        }
        foreach (var part in parts)
        {
            if (part.RootElement is not OpenXmlPartRootElement root) continue;
            foreach (var p in root.Descendants<DocumentFormat.OpenXml.Drawing.Paragraph>()) MaskRuns<DocumentFormat.OpenXml.Drawing.Text>(p, c, preserveSpace: false);
            MaskAttributes(root, name => name is "descr" or "title", c);
            MaskHyperlinks(part, c);
            root.Save();
        }
        MaskProperties(doc, c);
        MaskInnerParts(doc, c);
    }

    /// <summary>
    /// Parts below a story: embedded Office files (masked in place), binary objects (counted as skipped),
    /// photos (metadata removed), and the text of SmartArt and charts.
    /// </summary>
    private static void MaskInnerParts(OpenXmlPackage package, Covered c)
    {
        foreach (var part in package.GetAllParts().ToList())
        {
            switch (part)
            {
                case EmbeddedPackagePart embedded: MaskEmbedded(embedded, c); break;
                case EmbeddedObjectPart or EmbeddedControlPersistencePart or EmbeddedControlPersistenceBinaryDataPart: c.Unchecked++; break;
                case ImagePart image: StripImageMetadata(image, c); break;
                case ChartPart or ChartDrawingPart or DiagramDataPart or DiagramPersistLayoutPart: MaskDrawingText(part, c); break;
            }
        }
    }

    private static void MaskEmbedded(EmbeddedPackagePart part, Covered c)
    {
        using var ms = new MemoryStream();
        using (var s = part.GetStream(FileMode.Open, FileAccess.Read)) s.CopyTo(ms);
        ms.Position = 0;
        try
        {
            string type = part.ContentType;
            if (type.Contains("wordprocessingml", StringComparison.Ordinal)) { using var d = WordprocessingDocument.Open(ms, true); MaskWord(d, c); }
            else if (type.Contains("spreadsheetml", StringComparison.Ordinal)) { using var d = SpreadsheetDocument.Open(ms, true); MaskExcel(d, c); }
            else if (type.Contains("presentationml", StringComparison.Ordinal)) { using var d = PresentationDocument.Open(ms, true); MaskPowerPoint(d, c); }
            else { c.Unchecked++; return; }
        }
        catch (Exception ex) when (ex is not DocFailure)
        {
            Log.Write($"embedded {part.ContentType} not masked: {ex.Message}");
            c.Unchecked++;
            return;
        }
        ms.Position = 0;
        part.FeedData(ms);
    }

    /// <summary>
    /// Chart, SmartArt and chart-shape text. Chart values (c:v) also back the drawn chart; a number that
    /// holds a value becomes 0 so the chart stays readable, text becomes the mask.
    /// </summary>
    private static void MaskDrawingText(OpenXmlPart part, Covered c)
    {
        if (part.RootElement is not OpenXmlPartRootElement root) return;
        foreach (var p in root.Descendants<DocumentFormat.OpenXml.Drawing.Paragraph>()) MaskRuns<DocumentFormat.OpenXml.Drawing.Text>(p, c, preserveSpace: false);
        foreach (var v in root.Descendants<DocumentFormat.OpenXml.Drawing.Charts.NumericValue>())
        {
            string masked = c.Mask(v.Text)!;
            if (masked == v.Text) continue;
            v.Text = v.Ancestors<DocumentFormat.OpenXml.Drawing.Charts.NumberingCache>().Any() ? "0" : masked;
        }
        root.Save();
    }

    /// <summary>
    /// A masked mailto:/tel: target is no valid URI. The relationship keeps its id (w:hyperlink and
    /// DrawingML a:hlinkClick may point at it; a dangling id makes Word refuse the file) and goes nowhere.
    /// </summary>
    private static void MaskHyperlinks(OpenXmlPart part, Covered c)
    {
        foreach (var rel in part.HyperlinkRelationships.ToList())
        {
            string target = rel.Uri.OriginalString;
            if (c.Mask(target) == target) continue;
            string id = rel.Id;
            part.DeleteReferenceRelationship(rel);
            part.AddHyperlinkRelationship(new Uri("about:blank"), true, id);
        }
    }

    /// <summary>Core, extended and custom document properties of any Office package.</summary>
    private static void MaskProperties(OpenXmlPackage package, Covered c)
    {
        var pp = package.PackageProperties;
        pp.Title = c.Mask(pp.Title);
        pp.Subject = c.Mask(pp.Subject);
        pp.Creator = c.Mask(pp.Creator);
        pp.Keywords = c.Mask(pp.Keywords);
        pp.Description = c.Mask(pp.Description);
        pp.LastModifiedBy = c.Mask(pp.LastModifiedBy);
        pp.Category = c.Mask(pp.Category);
        // Only string-typed values can take the mask; a number or date holding a value (an ID stored as
        // vt:r8, say) would make the file invalid, so that custom property is removed instead.
        if (package.GetPartsOfType<ExtendedFilePropertiesPart>().FirstOrDefault()?.Properties is { } app)
        {
            foreach (var t in app.Descendants<OpenXmlLeafTextElement>().Where(IsStringProperty))
                t.Text = c.Mask(t.Text)!;
            app.Save();
        }
        if (package.GetPartsOfType<CustomFilePropertiesPart>().FirstOrDefault()?.Properties is { } custom)
        {
            foreach (var prop in custom.Elements<DocumentFormat.OpenXml.CustomProperties.CustomDocumentProperty>().ToList())
            {
                if (prop.FirstChild is not OpenXmlLeafTextElement value) continue;
                string masked = c.Mask(value.Text)!;
                if (masked == value.Text) continue;
                if (IsStringProperty(value)) value.Text = masked;
                else prop.Remove();
            }
            custom.Save();
        }
    }

    /// <summary>
    /// Removes EXIF, XMP, IPTC and text metadata from an embedded JPEG or PNG without touching the pixels or
    /// the colour profile. A photo whose metadata held a location counts as a covered item.
    /// </summary>
    private static void StripImageMetadata(ImagePart part, Covered c)
    {
        byte[] data;
        using (var s = part.GetStream(FileMode.Open, FileAccess.Read))
        using (var ms = new MemoryStream())
        {
            s.CopyTo(ms);
            data = ms.ToArray();
        }
        bool gps = false;
        byte[]? clean = part.ContentType switch
        {
            "image/jpeg" => StripJpeg(data, ref gps),
            "image/png" => StripPng(data, ref gps),
            _ => null,
        };
        if (clean is null) return;
        if (gps) c.NoteLocation(part.Uri);
        using var output = new MemoryStream(clean);
        part.FeedData(output);
    }

    /// <summary>JPEG without APP1 (EXIF, XMP), APP13 (IPTC) and comments; null when there was nothing to remove or the file is odd.</summary>
    private static byte[]? StripJpeg(byte[] d, ref bool gps)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;
        using var o = new MemoryStream(d.Length);
        o.Write(d, 0, 2);
        int i = 2;
        bool removed = false;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0xFF) return null;
            byte marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; } // fill byte
            if (marker == 0xDA) break; // start of scan: the rest is image data
            int len = d[i + 2] << 8 | d[i + 3];
            if (len < 2 || i + 2 + len > d.Length) return null;
            if (marker is 0xE1 or 0xED or 0xFE)
            {
                if (marker == 0xE1 && Radar.ExifHasGps(d[(i + 4)..(i + 2 + len)])) gps = true;
                if (marker == 0xE1 && System.Text.Encoding.Latin1.GetString(d, i + 4, len - 2).Contains("GPSLatitude", StringComparison.Ordinal)) gps = true; // XMP
                removed = true;
            }
            else o.Write(d, i, 2 + len);
            i += 2 + len;
        }
        if (!removed) return null;
        o.Write(d, i, d.Length - i);
        return o.ToArray();
    }

    /// <summary>PNG without eXIf, text chunks (tEXt, zTXt, iTXt: XMP lives there) and tIME; null when there was nothing to remove.</summary>
    private static byte[]? StripPng(byte[] d, ref bool gps)
    {
        if (d.Length < 8 || d[1] != 'P' || d[2] != 'N' || d[3] != 'G') return null;
        using var o = new MemoryStream(d.Length);
        o.Write(d, 0, 8);
        int i = 8;
        bool removed = false;
        while (i + 12 <= d.Length)
        {
            int len = d[i] << 24 | d[i + 1] << 16 | d[i + 2] << 8 | d[i + 3];
            if (len < 0 || i + 12 + len > d.Length) return null;
            string type = System.Text.Encoding.ASCII.GetString(d, i + 4, 4);
            if (type is "eXIf" or "tEXt" or "zTXt" or "iTXt" or "tIME")
            {
                if (type == "eXIf" && Radar.ExifHasGps(d[(i + 8)..(i + 8 + len)])) gps = true;
                // Text chunks may be compressed; any location there comes from XMP, which also names GPSLatitude.
                if (type != "eXIf" && System.Text.Encoding.Latin1.GetString(d, i + 8, len).Contains("GPSLatitude", StringComparison.Ordinal)) gps = true;
                removed = true;
            }
            else o.Write(d, i, 12 + len);
            i += 12 + len;
            if (type == "IEND") break;
        }
        return removed ? o.ToArray() : null;
    }

    // xml:space="preserve" on w:t, w:delText and w:instrText alike (only Text exposes it as a typed property).
    private static readonly OpenXmlAttribute PreserveSpace = new("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");

    private static bool IsStringProperty(OpenXmlLeafTextElement e) => e is
        DocumentFormat.OpenXml.VariantTypes.VTLPWSTR or DocumentFormat.OpenXml.VariantTypes.VTLPSTR or DocumentFormat.OpenXml.VariantTypes.VTBString or
        DocumentFormat.OpenXml.ExtendedProperties.Company or DocumentFormat.OpenXml.ExtendedProperties.Manager or
        DocumentFormat.OpenXml.ExtendedProperties.HyperlinkBase or DocumentFormat.OpenXml.ExtendedProperties.Template;

    /// <summary>Masks the attributes whose local name passes <paramref name="names"/> on every element under <paramref name="root"/>.</summary>
    private static void MaskAttributes(OpenXmlElement root, Func<string, bool> names, Covered c)
    {
        foreach (var e in root.Descendants().ToList())
            foreach (var a in e.GetAttributes())
            {
                if (!names(a.LocalName) || string.IsNullOrEmpty(a.Value)) continue;
                string masked = c.Mask(a.Value)!;
                if (masked != a.Value) e.SetAttribute(new OpenXmlAttribute(a.Prefix, a.LocalName, a.NamespaceUri, masked));
            }
    }

    /// <summary>Masks text nodes and attribute values of a custom XML part; rewrites it only when something changed.</summary>
    private static void MaskXmlPart(OpenXmlPart part, Covered c)
    {
        System.Xml.Linq.XDocument xml;
        try
        {
            using var s = part.GetStream(FileMode.Open, FileAccess.Read);
            xml = System.Xml.Linq.XDocument.Load(s, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException) { return; } // not XML after all; nothing Word would bind to
        bool changed = false;
        foreach (var t in xml.DescendantNodes().OfType<System.Xml.Linq.XText>())
        {
            string masked = c.Mask(t.Value)!;
            if (masked != t.Value) { t.Value = masked; changed = true; }
        }
        foreach (var a in xml.Descendants().SelectMany(e => e.Attributes()))
        {
            string masked = c.Mask(a.Value)!;
            if (masked != a.Value) { a.Value = masked; changed = true; }
        }
        if (!changed) return;
        using var ms = new MemoryStream();
        xml.Save(ms, System.Xml.Linq.SaveOptions.DisableFormatting);
        ms.Position = 0;
        part.FeedData(ms);
    }

    /// <summary>
    /// Masks the <typeparamref name="T"/> runs of one paragraph-like element (w:p, a:p, a shared string) as a
    /// whole, so a value split across runs is still caught. Runs that belong to a paragraph nested inside it
    /// (a text box) are left to that paragraph, so the two texts are never joined into a false match.
    /// </summary>
    private static void MaskRuns<T>(OpenXmlElement p, Covered c, bool preserveSpace) where T : OpenXmlLeafTextElement
    {
        Type paragraph = p.GetType();
        var texts = p.Descendants<T>().Where(t => t.Ancestors().First(a => a.GetType() == paragraph) == p).ToList();
        if (texts.Count == 0) return;
        string full = string.Concat(texts.Select(t => t.Text));
        var spans = MergeSpans(Radar.FindSensitiveSpans(full));
        if (spans.Count == 0) return;
        c.Note(full, spans);

        int pos = 0, si = 0;
        foreach (var t in texts)
        {
            var sb = new StringBuilder();
            foreach (char ch in t.Text)
            {
                while (si < spans.Count && pos >= spans[si].Start + spans[si].Length) si++;
                if (si < spans.Count && pos >= spans[si].Start)
                {
                    if (pos == spans[si].Start) sb.Append(Mask); // the whole mask sits in the first run the value touches
                }
                else sb.Append(ch);
                pos++;
            }
            t.Text = sb.ToString();
            if (preserveSpace) t.SetAttribute(PreserveSpace);
        }
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

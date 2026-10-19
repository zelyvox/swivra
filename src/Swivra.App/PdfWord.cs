using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Win32;
using System.Diagnostics;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Swivra.App;

/// <summary>
/// PDF to Word. With LibreOffice installed it converts headless (layout kept as far as LibreOffice manages);
/// without it, a text-only .docx is built from the PDF text (PdfPig + Open XML SDK, MIT).
/// </summary>
internal static class PdfWord
{
    private const int LibreOfficeTimeoutMs = 120_000;

    /// <summary>Path of soffice.exe, or null when LibreOffice is not installed.</summary>
    public static string? FindLibreOffice()
    {
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\soffice.exe");
                if (key?.GetValue(null) is string p && File.Exists(p)) return p;
            }
            catch { /* registry unreadable: fall through to the folders */ }
        }
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            string p = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Converts to .docx next to the original; the detail says honestly which route was taken.</summary>
    public static ActionResult Convert(string input, string dir, string baseName, List<string> created)
    {
        string? soffice = FindLibreOffice();
        if (soffice is not null)
        {
            string? target = TryLibreOffice(soffice, input, dir, baseName);
            if (target is not null)
            {
                created.Add(target);
                return new ActionResult(true, Lang.T("Hazır", "Ready"), $"{Path.GetFileName(target)} · {Lang.T("LibreOffice ile", "via LibreOffice")}", target, "docx");
            }
            Log.Write("libreoffice conversion failed, falling back to text-only docx");
        }

        var paragraphs = Paragraphs(input);
        if (paragraphs.All(p => p.Count == 0))
            return new ActionResult(false, Lang.T("Metin yok", "No text"), Lang.T("Taranmış PDF olabilir", "It may be a scanned PDF"));
        string textTarget = FileActions.UniquePath(dir, baseName, "docx");
        created.Add(textTarget);
        WriteDocx(textTarget, paragraphs);
        string note = soffice is null
            ? Lang.T("Yalnız metin · düzen için LibreOffice kur", "Text only · install LibreOffice to keep the layout")
            : Lang.T("Yalnız metin · LibreOffice çalışmadı", "Text only · LibreOffice failed");
        return new ActionResult(true, Lang.T("Hazır", "Ready"), $"{Path.GetFileName(textTarget)} · {note}", textTarget, "docx");
    }

    private static string? TryLibreOffice(string soffice, string input, string dir, string baseName)
    {
        string work = Directory.CreateTempSubdirectory("swivra-lo-").FullName;
        try
        {
            string outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(outDir);
            // Private profile so a running LibreOffice window or a locked default profile cannot block us.
            string profile = new Uri(Path.Combine(work, "profile")).AbsoluteUri;
            var psi = new ProcessStartInfo(soffice)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string a in new[] { $"-env:UserInstallation={profile}", "--headless", "--infilter=writer_pdf_import", "--convert-to", "docx", "--outdir", outDir, input })
                psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            proc.StandardOutput.ReadToEndAsync();
            proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(LibreOfficeTimeoutMs))
            {
                try { proc.Kill(true); } catch { /* already gone */ }
                return null;
            }
            string result = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + ".docx");
            if (proc.ExitCode != 0 || !File.Exists(result)) return null;
            // Move out of the temp folder before it is deleted.
            string kept = FileActions.UniquePath(dir, baseName, "docx");
            File.Move(result, kept);
            return kept;
        }
        catch (Exception ex)
        {
            Log.Write($"libreoffice failed: {ex.Message}");
            return null;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* harmless leftovers */ }
        }
    }

    /// <summary>Per page, the text blocks in reading order, each as one paragraph.</summary>
    private static List<List<string>> Paragraphs(string input)
    {
        var pages = new List<List<string>>();
        using var doc = UglyToad.PdfPig.PdfDocument.Open(input);
        foreach (var page in doc.GetPages())
        {
            var paras = new List<string>();
            try
            {
                var words = page.GetWords().ToList();
                if (page.Rotation.Value != 0)
                {
                    // Rotated pages make PdfPig split words into single letters: use the letter-based text, one paragraph.
                    string rotText = PdfRedact.PageText(page).Trim();
                    if (rotText.Length > 0) paras.Add(rotText);
                }
                else if (words.Count > 0)
                {
                    var blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                    foreach (var b in UnsupervisedReadingOrderDetector.Instance.Get(blocks))
                    {
                        string text = string.Join(" ", b.TextLines.Select(l => l.Text.Trim())).Trim();
                        if (text.Length > 0) paras.Add(text);
                    }
                }
            }
            catch (Exception ex)
            {
                // Layout analysis can choke on odd pages: fall back to the plain content-order text.
                Log.Write($"docx layout fallback: {ex.Message}");
                paras = ContentOrderTextExtractor.GetText(page)
                    .Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Replace("\r", "").Replace('\n', ' ').Trim()).Where(p => p.Length > 0).ToList();
            }
            pages.Add(paras);
        }
        return pages;
    }

    private static void WriteDocx(string target, List<List<string>> pages)
    {
        using var word = WordprocessingDocument.Create(target, WordprocessingDocumentType.Document);
        var main = word.AddMainDocumentPart();
        main.Document = new WordDocument();
        var body = main.Document.AppendChild(new Body());
        for (int i = 0; i < pages.Count; i++)
        {
            foreach (string p in pages[i])
            {
                var run = new Run(new Text(p) { Space = SpaceProcessingModeValues.Preserve });
                body.AppendChild(new Paragraph(run));
            }
            if (i < pages.Count - 1)
                body.AppendChild(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
        }
        main.Document.Save();
    }
}

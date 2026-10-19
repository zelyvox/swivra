using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;

namespace Swivra.App;

/// <summary>
/// PDF to Word. With Microsoft Word or LibreOffice installed the layout is kept (see OfficeConvert);
/// without either, a text-only .docx is built from the PDF text (PdfPig + Open XML SDK, MIT).
/// </summary>
internal static class PdfWord
{
    /// <summary>Converts to .docx next to the original; the detail says honestly which route was taken.</summary>
    public static ActionResult Convert(string input, string dir, string baseName, List<string> created)
    {
        string target = FileActions.UniquePath(dir, baseName, "docx");
        var office = OfficeConvert.Convert(input, target);
        if (office is not null)
        {
            created.Add(target);
            return new ActionResult(true, Lang.T("Hazır", "Ready"), $"{Path.GetFileName(target)} · {Lang.T($"{office.Engine} ile", $"via {office.Engine}")}", target, "docx");
        }

        var paragraphs = Paragraphs(input);
        if (paragraphs.All(p => p.Count == 0))
            return new ActionResult(false, Lang.T("Metin yok", "No text"), Lang.T("Taranmış PDF olabilir", "It may be a scanned PDF"));
        created.Add(target);
        WriteDocx(target, paragraphs);
        string note = OfficeConvert.Available
            ? Lang.T("Yalnız metin · Word/LibreOffice dönüştüremedi", "Text only · Word/LibreOffice could not convert it")
            : Lang.T("Yalnız metin · düzen için Word ya da LibreOffice gerekli", "Text only · Word or LibreOffice keeps the layout");
        return new ActionResult(true, Lang.T("Hazır", "Ready"), $"{Path.GetFileName(target)} · {note}", target, "docx");
    }

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

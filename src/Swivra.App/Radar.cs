using System.Drawing.Imaging;
using System.Text;
using System.Text.RegularExpressions;

namespace Swivra.App;

/// <summary>
/// Looks for sensitive data inside a file before it is shared. Runs fully offline.
/// v0: image EXIF (GPS, camera model), plain-text files and the text of the first PDF pages (TC kimlik no, IBAN, e-mail, phone).
/// </summary>
internal static partial class Radar
{
    private static readonly HashSet<string> TextExts = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".csv", ".md", ".json", ".xml", ".log" };
    private const long MaxTextBytes = 5 * 1024 * 1024;
    private const int MaxPdfPages = 20;

    public static List<string> Scan(string path)
    {
        var found = new List<string>();
        try
        {
            string ext = Path.GetExtension(path);
            if (FileKinds.IsImage(ext)) ScanImage(path, found);
            else if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) ScanPdf(path, found);
            else if (TextExts.Contains(ext) && new FileInfo(path).Length <= MaxTextBytes) ScanText(File.ReadAllText(path, Encoding.UTF8), found);
        }
        catch (Exception ex)
        {
            Log.Write($"radar failed {path}: {ex.Message}");
        }
        return found;
    }

    private static void ScanImage(string path, List<string> found)
    {
        using var stream = File.OpenRead(path);
        using var img = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
        var ids = img.PropertyIdList;
        if (ids.Contains(0x0002) || ids.Contains(0x0004)) found.Add(Lang.T("Konum bilgisi", "Location"));
        if (ids.Contains(0x0110))
        {
            string model = Encoding.ASCII.GetString(img.GetPropertyItem(0x0110)!.Value ?? []).Trim('\0', ' ');
            found.Add(model.Length > 0 ? Lang.T($"Cihaz modeli: {model}", $"Device model: {model}") : Lang.T("Cihaz modeli", "Device model"));
        }
    }

    private static void ScanPdf(string path, List<string> found)
    {
        try
        {
            ScanText(string.Join("\n", PdfActions.ExtractPages(path, MaxPdfPages)), found);
        }
        catch (Exception ex)
        {
            Log.Write($"radar pdf skipped {path}: {ex.Message}"); // password or corrupt: no findings
        }
    }

    public static void ScanText(string text, List<string> found)
    {
        if (TcCandidate().Matches(text).Any(m => IsValidTc(m.Value))) found.Add(Lang.T("TC kimlik no", "Turkish ID number"));
        if (IbanCandidate().Matches(text).Any(m => IsValidIban(m.Value))) found.Add("IBAN");
        if (Email().IsMatch(text)) found.Add(Lang.T("E-posta", "Email"));
        if (Phone().IsMatch(text)) found.Add(Lang.T("Telefon", "Phone"));
    }

    /// <summary>
    /// TC kimlik no: 11 digits, first non-zero.
    /// d10 = ((d1+d3+d5+d7+d9)*7 - (d2+d4+d6+d8)) mod 10, d11 = (d1+...+d10) mod 10.
    /// </summary>
    public static bool IsValidTc(string s)
    {
        if (s.Length != 11 || s[0] == '0' || !s.All(char.IsAsciiDigit)) return false;
        int[] d = s.Select(c => c - '0').ToArray();
        int odd = d[0] + d[2] + d[4] + d[6] + d[8];
        int even = d[1] + d[3] + d[5] + d[7];
        int d10 = ((odd * 7 - even) % 10 + 10) % 10;
        int d11 = d.Take(10).Sum() % 10;
        return d[9] == d10 && d[10] == d11;
    }

    public static bool IsValidIban(string raw)
    {
        string s = raw.Replace(" ", "").ToUpperInvariant();
        if (s.Length != 26 || !s.StartsWith("TR")) return false;
        string rearranged = s[4..] + s[..4];
        int rem = 0;
        foreach (char c in rearranged)
        {
            string chunk = char.IsLetter(c) ? (c - 'A' + 10).ToString() : c.ToString();
            foreach (char k in chunk) rem = (rem * 10 + (k - '0')) % 97;
        }
        return rem == 1;
    }

    [GeneratedRegex(@"(?<!\d)[1-9]\d{10}(?!\d)")]
    private static partial Regex TcCandidate();

    [GeneratedRegex(@"TR\d{2}(?:\s?\d{4}){5}\s?\d{2}", RegexOptions.IgnoreCase)]
    private static partial Regex IbanCandidate();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"(?<!\d)(?:\+90\s?|0)5\d{2}\s?\d{3}\s?\d{2}\s?\d{2}(?!\d)")]
    private static partial Regex Phone();
}

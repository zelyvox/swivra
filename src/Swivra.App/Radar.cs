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
    private const long MaxDocxBytes = 25 * 1024 * 1024;

    public static List<string> Scan(string path)
    {
        var found = new List<string>();
        try
        {
            string ext = Path.GetExtension(path);
            if (FileKinds.IsImage(ext)) ScanImage(path, found);
            else if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) ScanPdf(path, found);
            else if (ext.Equals(".docx", StringComparison.OrdinalIgnoreCase) && new FileInfo(path).Length <= MaxDocxBytes) ScanText(DocActions.DocxText(path), found);
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
        bool gps;
        string? model;
        try
        {
            using var stream = File.OpenRead(path);
            using var img = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: false);
            var ids = img.PropertyIdList;
            gps = ids.Contains(0x0002) || ids.Contains(0x0004);
            model = ids.Contains(0x0110) ? Encoding.ASCII.GetString(img.GetPropertyItem(0x0110)!.Value ?? []).Trim('\0', ' ') : null;
        }
        catch (Exception e) when (ImageIO.IsGdiRefusal(e))
        {
            (gps, model) = ImageIO.ReadPrivacy(path); // HEIC, AVIF, WebP: GDI+ cannot read them, WIC can
        }
        if (!gps && path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) gps = PngHasGps(path); // GDI+ ignores eXIf and XMP in PNG
        if (!gps && (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)))
            gps = JpegXmpHasGps(path); // nor does it show GPS kept only in XMP
        if (gps) found.Add(Lang.T("Konum bilgisi", "Location"));
        if (model is not null)
            found.Add(model.Length > 0 ? Lang.T($"Cihaz modeli: {model}", $"Device model: {model}") : Lang.T("Cihaz modeli", "Device model"));
    }

    private const int MaxMetaChunkBytes = 8 * 1024 * 1024; // one metadata chunk or segment read into memory, at most
    private const int MaxXmpBytes = 4 * 1024 * 1024;
    private const int MaxChunks = 200_000;                 // a 2 GB PNG in 8 KB IDAT chunks stays under this; a crafted file cannot loop forever

    /// <summary>
    /// GPS in a PNG: an eXIf chunk with a GPS IFD, or XMP (XML:com.adobe.xmp) with exif:GPSLatitude/Longitude. Never throws.
    /// eXIf and XMP may also come after the pixels (the spec allows it), so pixel chunks are skipped by seeking, not read.
    /// </summary>
    private static bool PngHasGps(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[8];
            if (fs.ReadAtLeast(head, 8, throwOnEndOfStream: false) < 8 || head[0] != 0x89 || head[1] != 'P' || head[2] != 'N' || head[3] != 'G') return false;
            for (int n = 0; n < MaxChunks && fs.Position + 12 <= fs.Length; n++)
            {
                if (fs.ReadAtLeast(head, 8, throwOnEndOfStream: false) < 8) return false;
                uint len = (uint)(head[0] << 24 | head[1] << 16 | head[2] << 8 | head[3]);
                string type = Encoding.ASCII.GetString(head[4..8]);
                if (len > fs.Length - fs.Position - 4) return false; // truncated: nothing more to trust
                if (type == "IEND") return false;
                bool wanted = type is "eXIf" or "iTXt" or "tEXt" or "zTXt" && len <= MaxMetaChunkBytes;
                if (!wanted)
                {
                    fs.Seek(len + 4, SeekOrigin.Current); // data and CRC
                    continue;
                }
                var data = new byte[len];
                fs.ReadExactly(data);
                fs.Seek(4, SeekOrigin.Current);
                if (type == "eXIf" && ExifHasGps(data)) return true;
                if (type != "eXIf" && XmpHasGps(type, data)) return true;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"radar png metadata skipped {path}: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// GPS that a JPEG keeps only in XMP (APP1 "http://ns.adobe.com/xap/1.0/" or its extension segments), which GDI+ does not
    /// show as EXIF properties. Reads segment headers up to the image data; never throws.
    /// </summary>
    private static bool JpegXmpHasGps(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.ReadByte() != 0xFF || fs.ReadByte() != 0xD8) return false;
            for (int n = 0; n < MaxChunks; n++)
            {
                int ff = fs.ReadByte();
                if (ff < 0) return false;
                if (ff != 0xFF) return false; // lost sync: stop rather than guess
                int marker = fs.ReadByte();
                while (marker == 0xFF) marker = fs.ReadByte(); // fill bytes
                if (marker < 0 || marker == 0xD9 || marker == 0xDA) return false; // end, or image data: metadata is over
                if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) continue; // no length
                int hi = fs.ReadByte(), lo = fs.ReadByte();
                if (hi < 0 || lo < 0) return false;
                int len = hi << 8 | lo;
                if (len < 2 || len - 2 > fs.Length - fs.Position) return false;
                if (marker != 0xE1)
                {
                    fs.Seek(len - 2, SeekOrigin.Current);
                    continue;
                }
                var data = new byte[len - 2];
                fs.ReadExactly(data);
                string text = Encoding.UTF8.GetString(data);
                if ((text.StartsWith("http://ns.adobe.com/xap/1.0/", StringComparison.Ordinal) || text.StartsWith("http://ns.adobe.com/xmp/extension/", StringComparison.Ordinal))
                    && (text.Contains("GPSLatitude", StringComparison.Ordinal) || text.Contains("GPSLongitude", StringComparison.Ordinal)))
                    return true;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"radar jpeg xmp skipped {path}: {ex.Message}");
        }
        return false;
    }

    /// <summary>TIFF-structured EXIF block: IFD0 has tag 0x8825 (GPS IFD pointer) and that IFD has at least one entry.</summary>
    internal static bool ExifHasGps(byte[] t)
    {
        if (t.Length >= 6 && t[0] == 'E' && t[1] == 'x' && t[2] == 'i' && t[3] == 'f') t = t[6..]; // optional "Exif\0\0" prefix
        if (t.Length < 8) return false;
        bool le = t[0] == 'I' && t[1] == 'I';
        if (!le && !(t[0] == 'M' && t[1] == 'M')) return false;
        ushort U16(int o) => (ushort)(le ? t[o] | t[o + 1] << 8 : t[o] << 8 | t[o + 1]);
        uint U32(int o) => le ? (uint)(t[o] | t[o + 1] << 8 | t[o + 2] << 16 | t[o + 3] << 24) : (uint)(t[o] << 24 | t[o + 1] << 16 | t[o + 2] << 8 | t[o + 3]);
        long ifd0 = U32(4);
        if (ifd0 + 2 > t.Length) return false;
        int n = U16((int)ifd0);
        for (int i = 0; i < n; i++)
        {
            long e = ifd0 + 2 + i * 12L;
            if (e + 12 > t.Length) return false;
            if (U16((int)e) != 0x8825) continue;
            long gps = U32((int)e + 8);
            return gps + 2 <= t.Length && U16((int)gps) > 0;
        }
        return false;
    }

    /// <summary>iTXt / tEXt / zTXt with keyword XML:com.adobe.xmp whose XML names exif:GPSLatitude or GPSLongitude.</summary>
    private static bool XmpHasGps(string type, ReadOnlySpan<byte> d)
    {
        int nul = d.IndexOf((byte)0);
        if (nul < 0 || Encoding.Latin1.GetString(d[..nul]) != "XML:com.adobe.xmp") return false;
        byte[] body;
        if (type == "tEXt") body = d[(nul + 1)..].ToArray();
        else if (type == "zTXt")
        {
            if (nul + 2 > d.Length) return false;
            body = Inflate(d[(nul + 2)..]);
        }
        else
        {
            if (nul + 3 > d.Length) return false;
            bool compressed = d[nul + 1] == 1;
            var rest = d[(nul + 3)..]; // after flag and method: language tag, translated keyword, text
            int a = rest.IndexOf((byte)0);
            if (a < 0) return false;
            rest = rest[(a + 1)..];
            int b = rest.IndexOf((byte)0);
            if (b < 0) return false;
            rest = rest[(b + 1)..];
            body = compressed ? Inflate(rest) : rest.ToArray();
        }
        string xml = Encoding.UTF8.GetString(body);
        return xml.Contains("GPSLatitude", StringComparison.Ordinal) || xml.Contains("GPSLongitude", StringComparison.Ordinal);
    }

    private static byte[] Inflate(ReadOnlySpan<byte> zlib)
    {
        using var input = new MemoryStream(zlib.ToArray());
        using var z = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while (output.Length < MaxXmpBytes && (read = z.Read(chunk, 0, chunk.Length)) > 0) output.Write(chunk, 0, read);
        return output.ToArray();
    }

    private static void ScanPdf(string path, List<string> found)
    {
        try
        {
            // Symbol-font characters folded back, as Hide searches them: digits in such a font are found too.
            ScanText(PdfRedact.Fold(string.Join("\n", PdfActions.ExtractPages(path, MaxPdfPages))), found);
        }
        catch (PdfTree.TreeException ex)
        {
            // Not "nothing found": the text was never read, and such a file is built to break readers.
            Log.Write($"radar pdf refused {path}: {ex.Message}");
            found.Add(Lang.T("PDF okunamadı, gözle kontrol et", "PDF could not be read, check it by eye"));
        }
        catch (Exception ex)
        {
            Log.Write($"radar pdf skipped {path}: {ex.Message}"); // password or corrupt: no findings
        }
    }

    public enum HitKind { Tc, Iban, Email, Phone }

    /// <summary>One sensitive match inside a text: where it starts and how long it is.</summary>
    public readonly record struct Hit(HitKind Kind, int Index, int Length);

    /// <summary>All validated matches (TC checksum, IBAN mod-97, e-mail, phone) with positions; shared by the radar and PDF redaction.</summary>
    public static List<Hit> FindAll(string text)
    {
        var hits = new List<Hit>();
        foreach (Match m in DigitRun().Matches(text)) AddTcHits(m, hits);
        foreach (Match m in IbanCandidate().Matches(text)) if (IsValidIban(m.Value)) hits.Add(new Hit(HitKind.Iban, m.Index, m.Length));
        foreach (Match m in Email().Matches(text)) hits.Add(new Hit(HitKind.Email, m.Index, m.Length));
        foreach (Match m in Phone().Matches(text)) hits.Add(new Hit(HitKind.Phone, m.Index, m.Length));
        return hits;
    }

    /// <summary>
    /// TC numbers inside one run of digit groups ("12345678950", "123 456 789 50", "1 2 3 ... 0" from boxed form cells).
    /// A candidate is whole groups adding up to exactly 11 digits, so it never starts or ends inside a group and
    /// a longer number never yields one; the checksum decides. The hit spans from its first to its last digit.
    /// </summary>
    private static void AddTcHits(Match run, List<Hit> hits)
    {
        string s = run.Value;
        var groups = new List<(int Start, int End)>();
        for (int i = 0; i < s.Length;)
        {
            if (!char.IsAsciiDigit(s[i])) { i++; continue; }
            int start = i;
            while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
            groups.Add((start, i));
        }
        for (int g = 0; g < groups.Count; g++)
        {
            var digits = new StringBuilder();
            for (int k = g; k < groups.Count && digits.Length < 11; k++)
            {
                digits.Append(s, groups[k].Start, groups[k].End - groups[k].Start);
                if (digits.Length != 11) continue;
                if (IsValidTc(digits.ToString()))
                {
                    hits.Add(new Hit(HitKind.Tc, run.Index + groups[g].Start, groups[k].End - groups[g].Start));
                    g = k;   // next candidate starts after this one
                }
                break;
            }
        }
    }

    public static void ScanText(string text, List<string> found)
    {
        var kinds = FindAll(text).Select(h => h.Kind).ToHashSet();
        if (kinds.Contains(HitKind.Tc)) found.Add(Lang.T("TC kimlik no", "Turkish ID number"));
        if (kinds.Contains(HitKind.Iban)) found.Add("IBAN");
        if (kinds.Contains(HitKind.Email)) found.Add(Lang.T("E-posta", "Email"));
        if (kinds.Contains(HitKind.Phone)) found.Add(Lang.T("Telefon", "Phone"));
    }

    /// <summary>Spans (start, length) of every sensitive item in the text, for masking.</summary>
    public static List<(int Start, int Length)> FindSensitiveSpans(string text) =>
        FindAll(text).Select(h => (h.Index, h.Length)).ToList();

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
        string s = string.Concat(raw.Where(c => !char.IsWhiteSpace(c) && c != '-')).ToUpperInvariant();
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

    // Digit groups joined by up to two spaces, dots or dashes on one line: "123 456 789 50", "123.456.789.50",
    // and form cells, which come back from a PDF as "1 2 3 4 5 6 7 8 9 5 0". Separators and digits never
    // overlap, so the match is linear; AddTcHits picks the 11-digit candidates out of it.
    [GeneratedRegex(@"(?<!\d)\d+(?:(?:[^\S\r\n]|[.\-]){1,2}\d+)*")]
    private static partial Regex DigitRun();

    // Up to two spaces or dashes between any two digits, not only between groups of four: a long IBAN that wraps
    // onto the next line in a PDF comes back as "TR33000610051978645 7841326" (the line break becomes a space,
    // next to a typed one it is two), and some forms write "TR33-0006-...". Bounded, so no backtracking blow-up; mod-97 still decides.
    [GeneratedRegex(@"TR\d{2}(?:[\s\-]{0,2}\d){22}", RegexOptions.IgnoreCase)]
    private static partial Regex IbanCandidate();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    // 05xx mobile numbers with +90 or a leading 0, the area code optionally in brackets, groups split by a space, dot or dash:
    // "0532 123 45 67", "(0532) 123 45 67", "0532-123-45-67", "+90 (532) 123 45 67", "0 532 123 45 67".
    [GeneratedRegex(@"(?<!\d)(?:\+90[\s.\-]?\(?|\(0|0[\s.\-]?\(?)5\d{2}\)?[\s.\-]?\d{3}[\s.\-]?\d{2}[\s.\-]?\d{2}(?!\d)")]
    private static partial Regex Phone();
}

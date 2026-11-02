using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Swivra.App;

/// <summary>
/// Reads the text of an embedded HTML, MHT or plain-text section (a Word altChunk) so it can go into
/// the document as plain paragraphs. Only text comes out: markup, styles, pictures and links are
/// dropped, so a mistake here can lose formatting but cannot let anything reach Word. Returns null
/// when the bytes do not look like text.
/// </summary>
internal static class ChunkText
{
    private static readonly TimeSpan RegexBudget = TimeSpan.FromSeconds(5);
    private const int MaxMimeDepth = 8;

    public static List<string>? Lines(byte[] data)
    {
        try
        {
            string? text = LooksLikeMime(data) ? FromMime(Encoding.Latin1.GetString(data), 0) : FromHtml(Decode(data, null));
            if (text is null || !IsText(text)) return null;
            return text.Split('\n')
                .Select(l => Regex.Replace(Clean(l), @"\s+", " ", RegexOptions.None, RegexBudget).Trim())
                .Where(l => l.Length > 0)
                .ToList();
        }
        catch (Exception ex)
        {
            // A mistake here must not stop the whole conversion: the section is removed instead.
            Log.Write($"embedded section not readable as text: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>
    /// True when the bytes are text that Word can only read as text: no markup, no MIME headers, no
    /// RTF anywhere. Word picks a file's format from its content, so anything else (an HTML page saved
    /// as .txt, say) is not handed to it as it is.
    /// </summary>
    public static bool IsPlainText(byte[] data)
    {
        try
        {
            if (LooksLikeMime(data)) return false;
            string text = Decode(data, null);
            // MIME headers anywhere (padding can push them past where LooksLikeMime looks), RTF anywhere.
            return IsText(text) && !HasMarkup(text) && !text.Contains(@"{\rt", StringComparison.OrdinalIgnoreCase)
                && !Regex.IsMatch(text, @"(?im)^(MIME-Version|Content-Type)\s*:", RegexOptions.None, RegexBudget);
        }
        catch (Exception) { return false; }
    }

    // A tag, comment, declaration or processing instruction: "<p", "</", "<!", "<?". "a < b" is not one.
    private static bool HasMarkup(string text) =>
        Regex.IsMatch(text, @"<[!?/a-zA-Z]", RegexOptions.None, RegexBudget) || text.Contains("&#", StringComparison.Ordinal);

    // ---------- MHT (MIME) ----------

    private static bool LooksLikeMime(byte[] data)
    {
        string head = Encoding.Latin1.GetString(data, 0, Math.Min(data.Length, 4096));
        int end = head.IndexOf("\n\n", StringComparison.Ordinal) is int a and >= 0 ? a : head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (end < 0) return false;
        return Regex.IsMatch(head[..end], @"(?im)^(MIME-Version|Content-Type)\s*:", RegexOptions.None, RegexBudget);
    }

    /// <summary>The text of a MIME entity: the first text/html part, else the first text/plain.</summary>
    private static string? FromMime(string raw, int depth)
    {
        if (depth > MaxMimeDepth) return null;
        raw = raw.Replace("\r\n", "\n");
        int split = raw.IndexOf("\n\n", StringComparison.Ordinal);
        string headerText = split < 0 ? raw : raw[..split];
        string body = split < 0 ? "" : raw[(split + 2)..];
        var headers = Headers(headerText);
        string type = headers.GetValueOrDefault("content-type", "text/plain");
        string mediaType = type.Split(';')[0].Trim().ToLowerInvariant();

        if (mediaType.StartsWith("multipart/", StringComparison.Ordinal))
        {
            string? boundary = Param(type, "boundary");
            if (string.IsNullOrEmpty(boundary)) return null;
            string? plain = null;
            foreach (string part in Parts(body, boundary))
            {
                string partHead = part.IndexOf("\n\n", StringComparison.Ordinal) is int i and >= 0 ? part[..i] : part;
                string partType = Headers(partHead).GetValueOrDefault("content-type", "text/plain").Split(';')[0].Trim().ToLowerInvariant();
                if (partType == "text/html" || partType.StartsWith("multipart/", StringComparison.Ordinal))
                {
                    if (FromMime(part, depth + 1) is string html) return html;
                }
                else if (partType == "text/plain" && plain is null) plain = FromMime(part, depth + 1);
            }
            return plain;
        }
        if (mediaType is not ("text/html" or "text/plain" or "application/xhtml+xml")) return null;
        byte[] bytes = headers.GetValueOrDefault("content-transfer-encoding", "").Trim().ToLowerInvariant() switch
        {
            "base64" => FromBase64(body),
            "quoted-printable" => FromQuotedPrintable(body),
            _ => Encoding.Latin1.GetBytes(body),
        };
        string text = Decode(bytes, Param(type, "charset"));
        return mediaType == "text/plain" ? text : FromHtml(text);
    }

    private static Dictionary<string, string> Headers(string block)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? name = null;
        foreach (string line in block.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                if (name is not null) result[name] += " " + line.Trim(); // folded header
                continue;
            }
            int colon = line.IndexOf(':');
            if (colon <= 0) { name = null; continue; }
            name = line[..colon].Trim();
            result.TryAdd(name, line[(colon + 1)..].Trim());
            if (result[name] != line[(colon + 1)..].Trim()) name = null; // repeated header: first one counts
        }
        return result;
    }

    private static string? Param(string header, string key)
    {
        var m = Regex.Match(header, $@"(?i);\s*{key}\s*=\s*(?:""([^""]*)""|([^;\s]+))", RegexOptions.None, RegexBudget);
        return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
    }

    private static IEnumerable<string> Parts(string body, string boundary)
    {
        string delimiter = "--" + boundary;
        var lines = body.Split('\n');
        StringBuilder? current = null;
        foreach (string line in lines)
        {
            string l = line.TrimEnd();
            if (l == delimiter + "--") { if (current is not null) yield return current.ToString(); yield break; }
            if (l == delimiter)
            {
                if (current is not null) yield return current.ToString();
                current = new StringBuilder();
                continue;
            }
            current?.Append(line).Append('\n');
        }
        if (current is not null) yield return current.ToString();
    }

    private static byte[] FromBase64(string body)
    {
        var sb = new StringBuilder(body.Length);
        foreach (char c in body) if (char.IsAsciiLetterOrDigit(c) || c is '+' or '/') sb.Append(c);
        while (sb.Length % 4 != 0) sb.Append('=');
        try { return Convert.FromBase64String(sb.ToString()); }
        catch (FormatException) { return []; }
    }

    private static byte[] FromQuotedPrintable(string body)
    {
        var bytes = new List<byte>(body.Length);
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (c == '=' && i + 1 < body.Length && body[i + 1] == '\n') { i++; continue; } // soft line break
            if (c == '=' && i + 2 < body.Length && Uri.IsHexDigit(body[i + 1]) && Uri.IsHexDigit(body[i + 2]))
            {
                bytes.Add(System.Convert.ToByte(body.Substring(i + 1, 2), 16));
                i += 2;
                continue;
            }
            bytes.Add((byte)c);
        }
        return bytes.ToArray();
    }

    // ---------- HTML ----------

    private static readonly string[] Dropped = ["script", "style", "head", "title", "xml", "object", "svg", "template", "noscript", "iframe"];

    /// <summary>Visible text of HTML: hidden blocks, comments and tags gone, block ends kept as line breaks.</summary>
    private static string FromHtml(string html)
    {
        if (!HasMarkup(html) && !html.Contains('&')) return html; // plain text, "a < b" included
        foreach (string tag in Dropped)
            html = Regex.Replace(html, $@"(?is)<{tag}\b.*?</{tag}\s*>", "\n", RegexOptions.None, RegexBudget);
        html = Regex.Replace(html, @"(?s)<!--.*?(-->|$)", " ", RegexOptions.None, RegexBudget);
        html = Regex.Replace(html, @"(?s)<!\[CDATA\[.*?(\]\]>|$)", " ", RegexOptions.None, RegexBudget);
        html = Regex.Replace(html, @"(?i)</?(p|div|br|li|tr|h[1-6]|table|ul|ol|dl|dd|dt|blockquote|pre|section|article|header|footer|hr|body|html)\b[^>]*>", "\n", RegexOptions.None, RegexBudget);
        html = Regex.Replace(html, @"(?i)</?(td|th)\b[^>]*>", " ", RegexOptions.None, RegexBudget);
        html = Regex.Replace(html, @"<[^>]*(>|$)", "", RegexOptions.None, RegexBudget);
        return WebUtility.HtmlDecode(html);
    }

    // ---------- text ----------

    /// <summary>Bytes to text: a BOM first, then strict UTF-8, then the declared charset, then Windows-1254.</summary>
    private static string Decode(byte[] b, string? charset)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        // UTF-16 without a BOM: in mostly-ASCII text every other byte is zero.
        if (b.Length >= 4 && b.Length % 2 == 0)
        {
            int n = Math.Min(b.Length, 4096) & ~1, evenZero = 0, oddZero = 0;
            for (int i = 0; i < n; i += 2) { if (b[i] == 0) evenZero++; if (b[i + 1] == 0) oddZero++; }
            if (oddZero > n / 4 && evenZero == 0) return Encoding.Unicode.GetString(b);
            if (evenZero > n / 4 && oddZero == 0) return Encoding.BigEndianUnicode.GetString(b);
        }
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch (DecoderFallbackException) { /* not UTF-8 */ }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        charset ??= Regex.Match(Encoding.Latin1.GetString(b, 0, Math.Min(b.Length, 4096)), @"(?i)charset\s*=\s*[""']?([\w.:-]+)", RegexOptions.None, RegexBudget) is { Success: true } m ? m.Groups[1].Value : null;
        try { if (charset is not null) return Encoding.GetEncoding(charset).GetString(b); }
        catch (ArgumentException) { /* unknown name */ }
        return Encoding.GetEncoding(1254).GetString(b);
    }

    /// <summary>False for binary data: NULs or many control characters.</summary>
    private static bool IsText(string s)
    {
        if (s.Contains('\0')) return false;
        int control = s.Count(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t' or '\f'));
        return control * 100 <= s.Length;
    }

    /// <summary>Only characters XML can carry.</summary>
    private static string Clean(string line)
    {
        var sb = new StringBuilder(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1])) { sb.Append(c).Append(line[++i]); continue; }
            if (System.Xml.XmlConvert.IsXmlChar(c) && c != '\r') sb.Append(c);
            else sb.Append(' ');
        }
        return sb.ToString();
    }
}

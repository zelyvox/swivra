using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Swivra.App;

/// <summary>
/// Layout-keeping conversions (PDF ↔ Word, documents → PDF/text) through an office suite the user
/// already has: Microsoft Word first, then LibreOffice. Neither is shipped with Swivra; callers fall
/// back to their own text-only route when this returns null.
/// </summary>
internal static partial class OfficeConvert
{
    /// <summary>
    /// Note: what the cleaning changed in the user's document, null when nothing. Uncleaned: the file went
    /// to the office program as it is (a binary .doc with Word as the only engine); the note says so.
    /// Path can differ from the requested target when another run took that name in the meantime.
    /// </summary>
    internal sealed record Result(string Path, string Engine, string? Note = null, bool Uncleaned = false);

    private const int TimeoutMs = 120_000;

    public static bool WordInstalled => Type.GetTypeFromProgID("Word.Application") is not null;

    public static bool Available => WordInstalled || FindLibreOffice() is not null;

    /// <summary>
    /// Converts <paramref name="input"/> to <paramref name="target"/> (".pdf", ".docx" or ".txt").
    /// Returns which engine did it, or null when no engine is installed or every engine failed.
    /// </summary>
    public static Result? Convert(string input, string target)
    {
        string ext = Path.GetExtension(target).ToLowerInvariant();
        string inExt = Path.GetExtension(input).ToLowerInvariant();
        string? soffice = FindLibreOffice();
        // No office program: nothing to clean for, and a refusal here would hide the caller's own
        // text-only route, which never hands the file to anything that fetches.
        if (soffice is null && !WordInstalled)
        {
            Log.Write("office conversion: no Word or LibreOffice installed");
            return null;
        }
        string? work = null, outDir = null;
        try
        {
            // The dropped file is not trusted. Word fetches a document's remote template (and linked
            // content) on open, which can leak the user's NTLM hash to a UNC path; Protected View avoids
            // that but cannot export, and leaving it triggers the fetch. So OOXML and RTF go in as a copy
            // with their external references cut. Legacy binary .doc cannot be cleaned: LibreOffice
            // (which does not load Word templates) is its only engine when installed, and Word gets it
            // only when it is the only engine there is, with a note saying it went in as it is.
            // Word reads the format from the bytes, not the name, so the bytes pick the cleaning, and
            // nothing else reaches Word as it is unless it can only be read as plain text.
            string safe = input;
            var report = new Cleaning();
            var kind = Sniff(input);
            switch (kind)
            {
                case Kind.Zip or Kind.Rtf:
                    work = Directory.CreateTempSubdirectory("swivra-word-").FullName;
                    safe = Path.Combine(work, Path.GetFileName(input));
                    SafeFile.CopyWritable(input, safe);
                    try
                    {
                        if (Sniff(safe) == Kind.Rtf) report.Links += CutRtfReferences(safe);
                        else if (IsOdfPackage(safe)) CleanOdf(safe, report); // by the bytes: Word imports ODF under any name
                        // A ZIP named .odt that is no OpenDocument package: LibreOffice gets it first and would try to repair it.
                        // A Word file renamed to .odt is still cleaned as one (both programs read it by its contents).
                        else if (inExt == ".odt" && !HasContentTypes(safe)) throw new UnsafeDocumentException("an .odt that is not an OpenDocument package");
                        else CutExternalReferences(safe, report);
                    }
                    catch (Exception ex) when (ex is not UnsafeDocumentException)
                    {
                        // The cleaner's own refusal (a duplicate or oversized part, broken XML, a bad escape):
                        // no office program has seen the file, so the user hears "could not be read safely",
                        // not "Word could not open it", and a .docx does not slip into the text-only route.
                        throw new UnsafeDocumentException($"cleaning failed ({ex.GetType().Name}: {ex.Message})");
                    }
                    break;
                case Kind.Pdf when inExt == ".pdf":
                    break; // PDF to Word: the PDF itself is read, as before
                case Kind.Ole:
                    if (inExt != ".doc")
                    {
                        Log.Write("office input: a binary Word file under another extension, not converted");
                        return null;
                    }
                    if (soffice is null) report.Uncleaned = true; // Word will get it as it is: the user is told
                    break;
                default: // text, markup or unknown bytes
                    if (OoxmlExts.Contains(inExt) || inExt is ".rtf" or ".odt" or ".pdf")
                    {
                        Log.Write("office input: contents do not match the extension, not converted");
                        return null;
                    }
                    byte[]? data = ReadAllCapped(input, MaxMarkupBytes);
                    if (inExt is ".txt" or ".md")
                    {
                        // Markup in a text file would make Word read it as a web page: the caller's own
                        // plain-text route shows it as it is instead.
                        if (data is null || !ChunkText.IsPlainText(data))
                        {
                            Log.Write("office input: text file with markup, office not used");
                            return null;
                        }
                        break;
                    }
                    // A web page saved as .doc (bank statements, exported reports): Word would load its
                    // pictures and styles from wherever they point. Its text goes in as a new document.
                    var lines = data is null ? null : ChunkText.Lines(data);
                    if (lines is null)
                    {
                        Log.Write("office input: not readable as text, not converted");
                        return null;
                    }
                    work = Directory.CreateTempSubdirectory("swivra-word-").FullName;
                    safe = Path.Combine(work, Path.GetFileNameWithoutExtension(input) + ".docx");
                    WriteTextDocx(lines, safe);
                    report.AsText++;
                    break;
            }
            if (report.Note() is string done) Log.Write($"office input cleaned before conversion: {done}");

            string? note = report.Note();
            // The engines write into a folder of this run's own and the result is moved to the target
            // without overwriting: the name was picked before the conversion and another run may have
            // taken it since. Word would save over that file, and a failed run would delete it.
            outDir = Directory.CreateTempSubdirectory("swivra-").FullName;
            string produced = Path.Combine(outDir, Path.GetFileName(target));
            // A binary .doc is never cleaned: with LibreOffice installed, Word does not get it even when LibreOffice fails.
            bool rawDoc = kind == Kind.Ole;
            string? engine = null;
            if (inExt is ".doc" or ".odt" && soffice is not null && TryLibreOffice(soffice, safe, produced, ext)) engine = "LibreOffice";
            else if (WordInstalled && !(rawDoc && soffice is not null) && TryWord(safe, produced, ext)) engine = "Word";
            else if (inExt is not (".doc" or ".odt") && soffice is not null && TryLibreOffice(soffice, safe, produced, ext)) engine = "LibreOffice";
            if (engine is null) return null;
            return new Result(Deliver(produced, target), engine, note, report.Uncleaned);
        }
        catch (UnsafeDocumentException ex)
        {
            // Not "no engine": the caller says the document could not be read safely.
            Log.Write($"office input refused: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            Log.Write($"office conversion failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (work is not null) try { Directory.Delete(work, true); } catch { /* harmless leftovers */ }
            if (outDir is not null) try { Directory.Delete(outDir, true); } catch { /* same */ }
        }
    }

    /// <summary>Moves the finished file to the target, or to the next free name if the target appeared meanwhile; never overwrites.</summary>
    private static string Deliver(string produced, string target)
    {
        try
        {
            File.Move(produced, target);
            return target;
        }
        catch (IOException) when (File.Exists(target))
        {
            string other = FileActions.UniquePath(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target), Path.GetExtension(target).TrimStart('.'));
            File.Move(produced, other);
            Log.Write("office output name taken meanwhile, saved under the next free name");
            return other;
        }
    }

    // ---------- untrusted input: cut external references ----------

    private static readonly HashSet<string> OoxmlExts = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm", ".dotx", ".dotm" };

    private enum Kind { Other, Zip, Rtf, Ole, Pdf }

    private static ReadOnlySpan<byte> OleSignature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>
    /// A ZIP package (OOXML, also ODT) or an OLE2 file (binary .doc) by its signature, RTF by <c>{\rtf</c>
    /// at the start. RTF text further in does not make a file RTF: Word would read it as something
    /// else, and it must then be handled as that.
    /// </summary>
    private static Kind Sniff(string path)
    {
        var head = new byte[1024];
        int n;
        using (var f = SafeFile.OpenRead(path)) n = f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return SniffBytes(head.AsSpan(0, n));
    }

    private static Kind SniffBytes(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 'P' && data[1] == 'K' && data[2] == 3 && data[3] == 4) return Kind.Zip;
        if (data.StartsWith(OleSignature)) return Kind.Ole;
        int i = data.StartsWith("﻿"u8) ? 3 : 0;
        while (i < data.Length && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') i++;
        // Word takes "{\rt" alone as RTF ("{\rtx1" opens too), so that is what counts.
        if (data[i..].StartsWith(@"{\rt"u8)) return Kind.Rtf;
        // A PDF header may come after up to 1 KB of anything.
        return data[..Math.Min(data.Length, 1024)].IndexOf("%PDF-"u8) >= 0 ? Kind.Pdf : Kind.Other;
    }

    private static byte[]? ReadAllCapped(string path, long cap)
    {
        if (new FileInfo(path).Length > cap) return null;
        return SafeFile.ReadAll(path);
    }

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    // Strict OOXML spells the same vocabulary in other namespaces; Word reads both.
    private static readonly XNamespace WStrict = "http://purl.oclc.org/ooxml/wordprocessingml/main";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace RelStrict = "http://purl.oclc.org/ooxml/officeDocument/relationships";

    private static bool IsW(XNamespace ns) => ns == W || ns == WStrict;

    /// <summary>
    /// The value Word reads for an attribute: the one with this local name in <paramref name="ns"/>.
    /// Null when there is none, or when another attribute carries the same local name (unqualified or in
    /// another namespace): this code and Word could then each take a different one.
    /// </summary>
    private static string? OwnAttribute(XElement e, XNamespace ns, string localName)
    {
        XAttribute? hit = null;
        foreach (var a in e.Attributes())
        {
            if (a.IsNamespaceDeclaration || a.Name.LocalName != localName) continue;
            if (a.Name.Namespace != ns || hit is not null) return null;
            hit = a;
        }
        return hit?.Value;
    }

    /// <summary>A plain paragraph per line; an empty list still gives one paragraph, so a table cell stays valid.</summary>
    private static IEnumerable<XElement> Paragraphs(XNamespace w, IReadOnlyList<string> lines) =>
        (lines.Count == 0 ? [""] : lines).Select(line => new XElement(w + "p",
            new XElement(w + "r", new XElement(w + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), line))));

    /// <summary>A new, minimal .docx holding only the given lines as plain paragraphs.</summary>
    private static void WriteTextDocx(IReadOnlyList<string> lines, string path)
    {
        const string Types = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>";
        const string Rels = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
            + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>";
        var doc = new XDocument(new XElement(W + "document", new XAttribute(XNamespace.Xmlns + "w", W),
            new XElement(W + "body", Paragraphs(W, lines), new XElement(W + "sectPr"))));
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string name, Action<Stream> write) { using var s = zip.CreateEntry(name).Open(); write(s); }
        Put("[Content_Types].xml", s => s.Write(System.Text.Encoding.UTF8.GetBytes(Types)));
        Put("_rels/.rels", s => s.Write(System.Text.Encoding.UTF8.GetBytes(Rels)));
        Put("word/document.xml", s => doc.Save(s, SaveOptions.DisableFormatting));
    }

    // A local path that does not exist: Word looks for it on disk and moves on, nothing leaves the machine.
    private const string Blocked = "file:///C:/swivra-blocked-reference";

    // Fields that pull in a file or a live link when Word updates them. QUOTE only prints its argument.
    private static readonly System.Text.RegularExpressions.Regex FetchingField = new(
        // FILLIN and ASK fetch nothing but open a prompt when updated, which hangs the invisible Word.
        @"\b(INCLUDEPICTURE|INCLUDETEXT|INCLUDE|IMPORT|LINK|DDEAUTO|DDE|RD|DATABASE|FILLIN|ASK)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>What the cleaning changed, in the words the user sees under the result.</summary>
    internal sealed class Cleaning
    {
        public int Links;   // external relationships and fetching fields cut
        public int AsText;  // embedded HTML/MHT/text sections kept as plain paragraphs
        public int Removed; // embedded sections that could not be read safely
        public int Macros;  // macros, scripts and event handlers removed (ODF)
        public bool Uncleaned; // a binary .doc that goes to Word as it is

        public string? Note()
        {
            var parts = new List<string>();
            if (Uncleaned) parts.Add(Lang.T("eski .doc biçimi temizlenemedi, Word'de olduğu gibi açıldı", "old .doc format could not be cleaned, opened in Word as it is"));
            // Short: it is shown in the small circle in the middle of the wheel.
            if (Links > 0) parts.Add(Lang.T($"{Links} bağlantı kesildi", $"{Links} link{(Links == 1 ? "" : "s")} cut"));
            if (AsText > 0) parts.Add(Lang.T($"{AsText} bölüm düz metin oldu", $"{AsText} section{(AsText == 1 ? "" : "s")} as plain text"));
            if (Removed > 0) parts.Add(Lang.T($"{Removed} bölüm çıkarıldı", $"{Removed} section{(Removed == 1 ? "" : "s")} removed"));
            if (Macros > 0) parts.Add(Lang.T($"{Macros} makro çıkarıldı", $"{Macros} macro{(Macros == 1 ? "" : "s")} removed"));
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
    }

    /// <summary>
    /// Points every external relationship except hyperlinks (attached template, linked pictures and
    /// objects, frames, subdocuments) at <see cref="Blocked"/>. Ids stay, so the file stays valid.
    /// Hyperlinks are only followed on a click. In every XML part, fields that fetch a file
    /// (w:instrText, w:fldSimple@w:instr) become QUOTE fields, as for RTF. Embedded altChunk parts,
    /// which Word imports on open, are handled too, see <see cref="CutPackage"/>.
    /// </summary>
    private static void CutExternalReferences(string path, Cleaning report)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        CutPackage(file, 0, report, new Budget());
    }

    /// <summary>
    /// Inflated bytes left for a package and every package nested in it. Each package's parts count at
    /// their declared sizes, and a nested package also counts once more while it is held in memory, so a
    /// small file that nests one large package in another is refused before the copies add up.
    /// </summary>
    private sealed class Budget
    {
        private long _left = MaxPackageBytes;

        public void Spend(long bytes)
        {
            if ((_left -= bytes) < 0) throw new UnsafeDocumentException("nested packages inflate past the size limit");
        }
    }

    // A chunk can hold a whole package, which can hold another; deeper than this is not opened.
    private const int MaxChunkDepth = 8;
    // A nested package is cleaned in memory; a larger one is not opened.
    private const long MaxChunkBytes = 256L * 1024 * 1024;
    // HTML, MHT and text sections are read as text; a larger one is not.
    private const long MaxMarkupBytes = 32L * 1024 * 1024;

    private const string AltChunkType = "/aFChunk";

    // Part is null when the target names no part in the package (Word might still resolve it differently).
    private sealed record ChunkRef(string Rels, string Source, string Id, string? Part);

    /// <summary>
    /// Cleans one package in place. An altChunk part is read by its bytes, never its name or content
    /// type: a package is cleaned like the file (nested chunks included, up to <see cref="MaxChunkDepth"/>),
    /// RTF like an RTF file. Anything else (HTML, MHT, text, unknown) never reaches Word: its text is
    /// read here and written into the document as plain paragraphs in place of the chunk, and the part,
    /// its relationship and its content-type entry go. Word's HTML import fetches links, stylesheets and
    /// pictures, and no rewrite of the markup could be shown to catch every way of spelling one.
    /// </summary>
    private static void CutPackage(Stream stream, int depth, Cleaning report, Budget budget)
    {
        // A read-only pass first: the limits are checked before anything is inflated, and the parts that
        // need reading are picked by name, content type or first character. An archive opened for update
        // keeps every entry it opens inflated in memory until it closes, so pictures, fonts and other
        // binary parts are never opened there. Every part is still read to its end here, without being
        // kept, so a damaged one (see CheckedRead) is refused as the whole file once was; the chunk parts
        // are kept from this read, so a nested package is inflated once.
        List<ChunkRef> refs;
        var toRead = new Dictionary<string, bool>(StringComparer.Ordinal); // part -> Word reads it as XML
        HashSet<string> partNames;
        var chunkData = new Dictionary<string, MemoryStream>(StringComparer.Ordinal);
        try
        {
            using (var peek = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
            {
                budget.Spend(CheckEntries(peek));
                var xmlTypes = ContentTypes.Read(peek);
                refs = AltChunkRefs(peek);
                var chunkParts = refs.Select(r => r.Part).OfType<string>().ToHashSet(StringComparer.Ordinal);
                partNames = peek.Entries.Select(e => e.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var e in peek.Entries)
                {
                    string name = e.FullName;
                    // Word reads a part as XML by its content type (or name); those must parse.
                    bool xmlPart = name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase) || name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || xmlTypes.IsXml(name);
                    MemoryStream? keep = null;
                    if (chunkParts.Contains(name))
                    {
                        budget.Spend(e.Length); // held in memory while it is cleaned
                        chunkData[name] = keep = new MemoryStream((int)e.Length);
                    }
                    using var s = new CheckedRead(e, keep);
                    if (xmlPart) toRead[name] = true;
                    else if (LooksLikeXml(s)) toRead[name] = false;
                    s.ToEnd();
                }
            }
            stream.Position = 0;
            using var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);
            CutParts(zip, depth, report, budget, refs, toRead, partNames, chunkData);
        }
        finally
        {
            foreach (var ms in chunkData.Values) ms.Dispose();
        }
    }

    private static void CutParts(ZipArchive zip, int depth, Cleaning report, Budget budget, List<ChunkRef> refs,
        Dictionary<string, bool> toRead, HashSet<string> partNames, Dictionary<string, MemoryStream> chunkData)
    {
        // Chunk part name -> paragraphs to put in its place (null: a placeholder line instead of its text).
        var flatten = new Dictionary<string, List<string>?>(StringComparer.Ordinal);
        // Chunk parts cleaned and kept (package, RTF): their relationship is pinned to this exact part.
        var kept = new HashSet<string>(StringComparer.Ordinal);
        foreach (string part in refs.Select(r => r.Part).OfType<string>().Distinct())
        {
            var entry = zip.GetEntry(part)!;
            chunkData.Remove(part, out var held);
            using var ms = held!; // its memory goes before the next chunk is cleaned
            var data = ms.GetBuffer().AsSpan(0, (int)ms.Length);
            var kind = SniffBytes(data);
            if (kind == Kind.Zip && depth + 1 <= MaxChunkDepth)
            {
                var inner = new Cleaning();
                ms.Position = 0;
                try { CutPackage(ms, depth + 1, inner, budget); }
                catch (Exception ex) when (ex is InvalidDataException or IOException)
                {
                    Log.Write($"office input: embedded package {part} unreadable ({ex.Message})");
                    flatten[part] = null;
                    report.Removed++;
                    continue;
                }
                report.Links += inner.Links; report.AsText += inner.AsText; report.Removed += inner.Removed;
                Overwrite(entry, ms.GetBuffer().AsSpan(0, (int)ms.Length)); // also changes nobody counts (a removed updateFields)
                kept.Add(part);
            }
            else if (kind == Kind.Rtf)
            {
                var latin1 = System.Text.Encoding.Latin1;
                int cut = 0;
                string rtf = CleanRtf(latin1.GetString(data), ref cut);
                if (cut > 0) Overwrite(entry, latin1.GetBytes(rtf));
                report.Links += cut;
                kept.Add(part);
            }
            else
            {
                var lines = kind is Kind.Zip or Kind.Ole || data.Length > MaxMarkupBytes ? null : ChunkText.Lines(data.ToArray());
                flatten[part] = lines;
                if (lines is null) { report.Removed++; Log.Write($"office input: embedded part {part} removed (not readable as text)"); }
                else report.AsText++;
            }
        }
        // (source part, relationship id) of every chunk that becomes paragraphs
        var replaced = new Dictionary<(string, string), List<string>?>();
        var droppedRels = new HashSet<(string, string)>();
        var pinned = new Dictionary<(string, string), string>();
        var known = refs.Select(r => (r.Source, r.Id)).ToHashSet();
        foreach (var r in refs)
        {
            if (r.Part is not null && kept.Contains(r.Part))
            {
                pinned.TryAdd((r.Rels, r.Id), r.Part);
                continue;
            }
            List<string>? lines = null;
            if (r.Part is not null) lines = flatten[r.Part];
            else
            {
                // A target this code cannot place: if Word places it differently it would load it
                // uncleaned, so the chunk goes, whatever it points at.
                report.Removed++;
                Log.Write($"office input: embedded part reference {r.Id} in {r.Rels} removed (target not found)");
            }
            replaced.TryAdd((r.Source, r.Id), lines);
            droppedRels.Add((r.Rels, r.Id));
        }

        foreach (var entry in zip.Entries.ToList())
        {
            string name = entry.FullName;
            if (flatten.ContainsKey(name) || kept.Contains(name)) continue; // deleted below, or already cleaned
            bool rels = name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);
            bool types = name.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase);
            // Any part may hold a story, under any name: _rels/.rels and the content types decide, not
            // the extension. So every part that is XML by its bytes is checked (picked in the first pass).
            if (!toRead.TryGetValue(name, out bool xmlPart)) continue;
            byte[] bytes = ReadCapped(entry, MaxXmlBytes) ?? throw new UnsafeDocumentException($"part {name} too large to read as XML");
            XDocument xml;
            try
            {
                xml = LoadXml(bytes, name);
            }
            catch (System.Xml.XmlException ex)
            {
                if (!xmlPart) continue; // markup Word does not read as a part (an unused HTML file, say)
                // Skipping it would leave whatever it links to in place; Word's parser may read it anyway.
                throw new InvalidDataException($"part {name} is not well-formed XML ({ex.Message})");
            }
            // Writing the part back drops a DOCTYPE the parser skipped; see MayHideDoctype.
            bool changed = MayHideDoctype(bytes);
            if (rels)
            {
                foreach (var rel in xml.Descendants().Where(e => e.Name.LocalName == "Relationship").ToList())
                {
                    var key = (name, (string?)rel.Attribute("Id") ?? "");
                    if (droppedRels.Contains(key)) { rel.Remove(); changed = true; continue; }
                    // Office add-ins (task panes) are loaded from their store, which can be a network share.
                    string relType = (string?)rel.Attribute("Type") ?? "";
                    if (relType.EndsWith("/webextensiontaskpanes", StringComparison.OrdinalIgnoreCase) || relType.EndsWith("/webextension", StringComparison.OrdinalIgnoreCase))
                    {
                        rel.Remove();
                        report.Links++;
                        changed = true;
                        continue;
                    }
                    if (pinned.TryGetValue(key, out var part))
                    {
                        // Word must open the very part that was cleaned, however it would read the old target.
                        rel.SetAttributeValue("Target", "/" + string.Join('/', part.Split('/').Select(Uri.EscapeDataString)));
                        changed = true;
                        continue;
                    }
                    if (!IsExternal(rel)) continue;
                    if (((string?)rel.Attribute("Type"))?.EndsWith("/hyperlink", StringComparison.Ordinal) == true) continue;
                    rel.SetAttributeValue("Target", Blocked);
                    report.Links++;
                    changed = true;
                }
            }
            else if (types)
            {
                foreach (var o in xml.Descendants().Where(e => e.Name.LocalName == "Override").ToList())
                {
                    string? partName = ResolvePart("", (string?)o.Attribute("PartName"));
                    if (partName is not null && flatten.Keys.Any(k => k.Equals(partName, StringComparison.OrdinalIgnoreCase))) { o.Remove(); changed = true; }
                }
            }
            else
            {
                int cut = NeutralizeFields(xml);
                foreach (var a in xml.Descendants().Attributes().Where(a => a.Name.LocalName == "instr").ToList())
                {
                    string value = Neutralize(a.Value, ref cut);
                    if (value == a.Value) continue;
                    a.Value = value;
                    KeepResult(a.Parent!);
                }
                // Settings that act on open: a mail merge connects to its data source (and asks about
                // it in a prompt nobody sees); updateFields refreshes every field.
                foreach (var setting in xml.Descendants().Where(e => e.Name.LocalName is "mailMerge" or "updateFields").ToList())
                {
                    if (setting.Name.LocalName == "mailMerge") cut++;
                    setting.Remove();
                    changed = true;
                }
                report.Links += cut;
                changed |= cut > 0;
                changed |= CutXmlLinks(xml, PartDir(name), partNames, report, MayBeSvg(name, bytes, xml));
                foreach (var chunk in xml.Descendants().Where(e => e.Name.LocalName == "altChunk").ToList())
                {
                    // Word reads r:id only (the Strict one in a Strict part). Another "id" beside it, or none,
                    // leaves which relationship Word follows open, so the chunk goes.
                    XNamespace? relNs = chunk.Name.Namespace == W ? Rel : chunk.Name.Namespace == WStrict ? RelStrict : null;
                    string? id = relNs is null ? null : OwnAttribute(chunk, relNs, "id");
                    if (id is null || !replaced.TryGetValue((name, id), out var lines))
                    {
                        if (id is not null && known.Contains((name, id))) continue; // a cleaned package or RTF, pinned above
                        // Its relationship is not an altChunk one, or is missing: Word may import whatever
                        // it points at all the same, so the chunk goes.
                        lines = null;
                        report.Removed++;
                        Log.Write($"office input: embedded section {id ?? "(id unclear)"} in {name} removed (not an altChunk relationship)");
                    }
                    chunk.ReplaceWith(Paragraphs(chunk.Name.Namespace, lines ??
                        [Lang.T("[Swivra: gömülü bir bölüm güvenli okunamadığı için çıkarıldı]", "[Swivra: an embedded section was removed because it could not be read safely]")]));
                    changed = true;
                }
            }
            if (!changed) continue;
            xml.DocumentType?.Remove();
            using var wr = Recreate(entry).Open();
            xml.Save(wr, SaveOptions.DisableFormatting);
        }
        foreach (var part in flatten.Keys) zip.GetEntry(part)?.Delete();
    }

    // Real documents have a few hundred parts at most and inflate to tens of megabytes; a package past
    // either limit is refused before any part is inflated (a 1 MB file can declare gigabytes of zeros).
    private const int MaxEntries = 10_000;
    private const long MaxPackageBytes = 512L * 1024 * 1024;

    /// <summary>
    /// The OpenDocument rule for links inside XML parts, applied to an OOXML part other than relationships
    /// and content types: SVG pictures (href, xlink:href, url() in styles), VML (o:href, src) and whatever
    /// else names an address in an attribute. The same copy can go to LibreOffice, whose SVG and VML
    /// importers follow them. Every href or src that does not name a part of this package is pointed at
    /// <see cref="Blocked"/>; inline raster pictures (data:image/...) and cid: names, which reach nothing,
    /// stay. xml:base and xml-stylesheet instructions go; CSS that loads from an address is refused, in a
    /// style element or an attribute that takes CSS (style, and the presentation attributes that take
    /// url()), whatever its namespace. In a part that may reach an SVG importer (<paramref name="svg"/>,
    /// see <see cref="MayBeSvg"/>) every attribute counts: the importer may take a picture without (or
    /// under another) namespace as SVG all the same. url(#id) inside the picture still passes. Elsewhere
    /// other attributes are left alone, so alt text that mentions "url(" does not refuse the document.
    /// </summary>
    private static bool CutXmlLinks(XDocument xml, string partDir, HashSet<string> names, Cleaning report, bool svg)
    {
        bool changed = false;
        foreach (var pi in xml.DescendantNodes().OfType<XProcessingInstruction>().Where(p => p.Target.Equals("xml-stylesheet", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            pi.Remove();
            changed = true;
        }
        foreach (var el in xml.Descendants())
        {
            if (el.Name.LocalName == "style" && FetchesCss(el.Value)) throw new UnsafeDocumentException("style sheet that loads from an address");
            foreach (var attr in el.Attributes().ToList())
            {
                if (attr.IsNamespaceDeclaration) continue;
                if (attr.Name == XNamespace.Xml + "base")
                {
                    attr.Remove();
                    changed = true;
                    continue;
                }
                // style="" (VML's too), and SVG presentation attributes (fill, filter, mask, clip-path, marker-*) that take url().
                if ((svg || CssAttributes.Contains(attr.Name.LocalName)) && FetchesCss(attr.Value))
                    throw new UnsafeDocumentException($"attribute {attr.Name.LocalName} that loads from an address");
                if (attr.Name.LocalName is not ("href" or "src")) continue;
                string href = attr.Value.Trim();
                if (IsInsidePackage(href, partDir, names) || InlinePicture.IsMatch(href) || href.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)) continue;
                attr.Value = Blocked;
                report.Links++;
                changed = true;
            }
        }
        return changed;
    }

    // Attributes that carry CSS, by local name in any namespace: style, and the SVG presentation
    // attributes whose value may be a url().
    private static readonly HashSet<string> CssAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "style", "fill", "stroke", "filter", "clip-path", "mask", "marker", "marker-start", "marker-mid", "marker-end", "cursor",
    };

    /// <summary>
    /// A part that may reach an SVG importer: one named .svg, or one with "svg" anywhere in it, in any
    /// case (an svg element in any namespace or none, "&lt;svg" in a comment or processing instruction:
    /// LibreOffice picks its SVG filter by looking for that text). Searched in the bytes and in the parsed
    /// document, so neither an encoding nor a character reference hides it.
    /// </summary>
    private static bool MayBeSvg(string name, byte[] bytes, XDocument xml)
    {
        if (name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) return true;
        for (int i = 0; i + 2 < bytes.Length; i++)
            if ((bytes[i] | 0x20) == 's' && (bytes[i + 1] | 0x20) == 'v' && (bytes[i + 2] | 0x20) == 'g') return true;
        foreach (var node in xml.DescendantNodes())
        {
            bool found = node switch
            {
                XElement e => Has(e.Name.LocalName) || Has(e.Name.NamespaceName)
                    || e.Attributes().Any(a => Has(a.Name.LocalName) || Has(a.Name.NamespaceName) || Has(a.Value)),
                XText t => Has(t.Value), // CDATA too
                XComment c => Has(c.Value),
                XProcessingInstruction p => Has(p.Target) || Has(p.Data),
                XDocumentType d => Has(d.Name) || Has(d.PublicId) || Has(d.SystemId) || Has(d.InternalSubset),
                _ => false,
            };
            if (found) return true;
        }
        return false;

        static bool Has(string? s) => s is not null && s.Contains("svg", StringComparison.OrdinalIgnoreCase);
    }

    // A raster picture inline in the address itself; an inline SVG could link on, so it is not one.
    private static readonly System.Text.RegularExpressions.Regex InlinePicture = new(
        @"^data:image/(png|jpeg|jpg|gif|bmp|webp|tiff|x-icon)[;,]", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Refuses a package Word might read differently from this code: two entries with the same part
    /// name (case and escapes aside) could each be the one Word picks, and an entry that inflates past
    /// the size limit would not fit in memory. Also one with more entries, or more inflated bytes in
    /// all, than any real document. Sizes are the declared ones: the archive reader stops each entry there.
    /// Returns the declared inflated bytes in all.
    /// </summary>
    private static long CheckEntries(ZipArchive zip)
    {
        if (zip.Entries.Count > MaxEntries) throw new InvalidDataException($"{zip.Entries.Count} entries");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var e in zip.Entries)
        {
            if (e.Length > MaxChunkBytes) throw new InvalidDataException($"part {e.FullName} too large");
            if ((total += e.Length) > MaxPackageBytes) throw new InvalidDataException("package inflates past the size limit");
            if (!seen.Add(ResolvePart("", e.FullName) ?? e.FullName)) throw new InvalidDataException($"duplicate part {e.FullName}");
        }
        return total;
    }

    /// <summary>
    /// Reads an entry through to its end without keeping it (or into <c>keep</c>, for a part that is
    /// cleaned in memory): the archive reader checks neither the CRC nor whether the data ends where
    /// the header says, so both are checked here. A part that cannot be inflated, inflates to another
    /// length or fails its CRC is refused: Word and LibreOffice would repair it or read it otherwise.
    /// </summary>
    private sealed class CheckedRead : Stream
    {
        private readonly ZipArchiveEntry _entry;
        private readonly Stream _inner;
        private readonly MemoryStream? _keep;
        private uint _crc = 0xFFFFFFFF;
        private long _read;

        public CheckedRead(ZipArchiveEntry entry, MemoryStream? keep = null)
        {
            _entry = entry;
            _keep = keep;
            try { _inner = entry.Open(); }
            catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException) { throw Damaged(ex.Message); }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n;
            try { n = _inner.Read(buffer); }
            catch (Exception ex) when (ex is InvalidDataException or IOException) { throw Damaged(ex.Message); }
            var got = buffer[..n];
            uint crc = _crc;
            foreach (byte b in got) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            _crc = crc;
            _read += n;
            _keep?.Write(got);
            return n;
        }

        /// <summary>Reads what is left and compares length and CRC with the archive's own.</summary>
        public void ToEnd()
        {
            var buffer = new byte[81920];
            while (Read(buffer, 0, buffer.Length) > 0) { }
            if (_read != _entry.Length || ~_crc != _entry.Crc32) throw Damaged("length or CRC differs");
        }

        private UnsafeDocumentException Damaged(string why) => new($"damaged part {_entry.FullName} ({why})");

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // CRC-32 (IEEE 802.3, reflected 0xEDB88320), as ZIP stores it.
        private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(i =>
        {
            uint c = (uint)i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            return c;
        }).ToArray();
    }

    // A parsed part takes many times its size in memory (a 6-byte "<w:p/>" becomes an object of over a
    // hundred bytes), and the package limit does not bound that. Word's own limit for a text-only
    // document is 32 MB, and the markup around that much text stays well under 48 MB; real parts were
    // measured at 18 to 30 bytes per node (element, attribute or text), so 48 MB of real markup holds
    // about 2.5 million. A part past either limit is refused before it is parsed: at most a few hundred
    // megabytes of tree, however the bytes are spent.
    private const int MaxXmlBytes = 48 * 1024 * 1024;
    private const int MaxXmlNodes = 2_500_000;

    /// <summary>
    /// Parses a part. A DOCTYPE (some SVG pictures carry one) is skipped, not refused; nothing outside
    /// the package is ever read. Its nodes are counted in a streaming pass first (see <see cref="MaxXmlNodes"/>).
    /// </summary>
    private static XDocument LoadXml(byte[] bytes, string name)
    {
        if (bytes.Length > MaxXmlBytes) throw new UnsafeDocumentException($"part {name} too large to read as XML");
        var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null };
        long nodes = 0;
        using (var counter = System.Xml.XmlReader.Create(new MemoryStream(bytes, writable: false), settings))
            while (counter.Read())
                if (counter.NodeType != System.Xml.XmlNodeType.EndElement && (nodes += 1 + counter.AttributeCount) > MaxXmlNodes)
                    throw new UnsafeDocumentException($"part {name} has too many XML nodes");
        using var reader = System.Xml.XmlReader.Create(new MemoryStream(bytes, writable: false), settings);
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    /// <summary>An entry read whole (up to <see cref="MaxXmlBytes"/>) and parsed; see <see cref="LoadXml(byte[], string)"/>.</summary>
    private static XDocument LoadXml(ZipArchiveEntry entry) =>
        LoadXml(ReadCapped(entry, MaxXmlBytes) ?? throw new UnsafeDocumentException($"part {entry.FullName} too large to read as XML"), entry.FullName);

    /// <summary>
    /// The package's content types: which parts Word reads as XML. Every Default and Override counts,
    /// whatever its namespace and however often it repeats: a part is XML when any of them says so, as
    /// Word may take a different one of two entries than this code would.
    /// </summary>
    private sealed class ContentTypes
    {
        private readonly Dictionary<string, bool> _byExt = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _byPart = new(StringComparer.OrdinalIgnoreCase);

        public static ContentTypes Read(ZipArchive zip)
        {
            var result = new ContentTypes();
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase));
            if (entry is null) return result;
            XDocument xml;
            try
            {
                xml = LoadXml(entry);
            }
            catch (System.Xml.XmlException ex) { throw new InvalidDataException($"content types are not well-formed XML ({ex.Message})"); }
            foreach (var e in xml.Descendants())
            {
                string type = (string?)e.Attribute("ContentType") ?? "";
                bool xmlType = type.Split(';')[0].Trim().EndsWith("xml", StringComparison.OrdinalIgnoreCase);
                if (e.Name.LocalName == "Default" && (string?)e.Attribute("Extension") is string ext) Add(result._byExt, ext, xmlType);
                else if (e.Name.LocalName == "Override" && ResolvePart("", (string?)e.Attribute("PartName")) is string part) Add(result._byPart, part, xmlType);
            }
            return result;

            static void Add(Dictionary<string, bool> map, string key, bool xmlType) =>
                map[key] = xmlType || (map.TryGetValue(key, out bool before) && before);
        }

        public bool IsXml(string name)
        {
            string key = ResolvePart("", name) ?? name;
            return _byPart.TryGetValue(key, out bool t) ? t
                : _byExt.TryGetValue(Path.GetExtension(key).TrimStart('.'), out bool d) && d;
        }
    }

    private static bool LooksLikeXml(byte[] data) => LooksLikeXml(new MemoryStream(data, writable: false));

    /// <summary>
    /// XML by its first character: after a BOM and blank space, a '&lt;'. UTF-16 and UTF-32 count without
    /// a BOM too, as XML parsers detect them: the zero bytes around an ASCII character give the width and
    /// the byte order away. Reads the stream only as far as that character.
    /// </summary>
    private static bool LooksLikeXml(Stream s)
    {
        var head = new byte[4];
        int n = s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        if (n >= 2 && (head[0], head[1]) is (0xFF, 0xFE) or (0xFE, 0xFF)) return true; // UTF-16 (and UTF-32LE) BOM
        if (n >= 4 && head is [0, 0, 0xFE, 0xFF]) return true; // UTF-32BE BOM
        int width = 1, low = 0, skip = 0; // bytes per character, and where its ASCII byte sits
        if (n >= 3 && head is [0xEF, 0xBB, 0xBF, ..]) skip = 3;
        else if (n >= 4)
            (width, low) = head switch
            {
                [not 0, 0, 0, 0] => (4, 0),
                [0, 0, 0, not 0] => (4, 3),
                [not 0, 0, _, _] => (2, 0),
                [0, not 0, _, _] => (2, 1),
                _ => (1, 0),
            };
        var buffer = new byte[4096];
        head.AsSpan(0, n).CopyTo(buffer);
        int have = n, pos = skip;
        while (true)
        {
            if (have - pos < width)
            {
                // keep the partial character, read more
                int rest = have - pos;
                Buffer.BlockCopy(buffer, pos, buffer, 0, rest);
                have = rest + s.ReadAtLeast(buffer.AsSpan(rest), buffer.Length - rest, throwOnEndOfStream: false);
                pos = 0;
                if (have < width) return false;
            }
            var ch = buffer.AsSpan(pos, width);
            for (int b = 0; b < width; b++)
                if (b != low && ch[b] != 0) return false; // not ASCII: not '<' and not blank
            pos += width;
            if (ch[low] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            return ch[low] == '<';
        }
    }

    /// <summary>
    /// The parser skips a DOCTYPE without a trace, so whether a part had one is read from its bytes: a
    /// part that is not plainly UTF-8 (a BOM-less UTF-16 or UTF-32 one, or one that declares another
    /// encoding) cannot be searched that way and counts as having one. Such a part is written back, and
    /// a DOCTYPE never reaches the office program's own parser, whatever the encoding.
    /// </summary>
    private static bool MayHideDoctype(byte[] bytes)
    {
        var data = bytes.AsSpan();
        if (data.StartsWith("﻿"u8)) data = data[3..];
        else if (data.Length >= 2 && (data[0] == 0 || data[1] == 0 || data[0] >= 0xFE)) return true; // UTF-16/32, with or without a BOM
        if (data.StartsWith("<?xml"u8))
        {
            int end = data[..Math.Min(data.Length, 256)].IndexOf("?>"u8);
            if (end < 0) return true;
            var decl = System.Text.Encoding.ASCII.GetString(data[..end]);
            var enc = System.Text.RegularExpressions.Regex.Match(decl, @"encoding\s*=\s*[""']([^""']*)[""']");
            if (enc.Success && enc.Groups[1].Value.ToLowerInvariant() is not ("utf-8" or "utf8" or "us-ascii" or "ascii")) return true;
        }
        for (int i = data.IndexOf("<!"u8); i >= 0; )
        {
            if (data.Length - i >= 9 && System.Text.Encoding.ASCII.GetString(data.Slice(i + 2, 7)).Equals("DOCTYPE", StringComparison.OrdinalIgnoreCase)) return true;
            int next = data[(i + 2)..].IndexOf("<!"u8);
            i = next < 0 ? -1 : i + 2 + next;
        }
        return false;
    }

    /// <summary>
    /// Every altChunk relationship that points at a part inside the package. Word imports such a part
    /// (HTML, MHT, RTF or a whole package) into the document on open.
    /// </summary>
    private static List<ChunkRef> AltChunkRefs(ZipArchive zip)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in zip.Entries) names.TryAdd(e.FullName.TrimStart('/'), e.FullName);
        var found = new List<ChunkRef>();
        foreach (var entry in zip.Entries)
        {
            string rels = entry.FullName;
            if (!rels.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
            // "word/_rels/document.xml.rels" describes "word/document.xml": targets resolve from "word/".
            string norm = rels.Replace('\\', '/');
            int slash = norm.LastIndexOf("_rels/", StringComparison.OrdinalIgnoreCase);
            string baseDir = slash < 0 ? "" : norm[..slash];
            string source = baseDir + Path.GetFileNameWithoutExtension(norm);
            names.TryGetValue(source, out var sourceEntry);
            XDocument xml;
            try
            {
                xml = LoadXml(entry);
            }
            catch (System.Xml.XmlException ex) { throw new InvalidDataException($"part {rels} is not well-formed XML ({ex.Message})"); }
            foreach (var rel in xml.Descendants().Where(e => e.Name.LocalName == "Relationship"))
            {
                if (IsExternal(rel)) continue; // cut with the other external links
                if (((string?)rel.Attribute("Type"))?.EndsWith(AltChunkType, StringComparison.OrdinalIgnoreCase) != true) continue;
                string? real = ResolvePart(baseDir, (string?)rel.Attribute("Target")) is string part && names.TryGetValue(part, out var hit) ? hit : null;
                found.Add(new ChunkRef(rels, sourceEntry ?? source, (string?)rel.Attribute("Id") ?? "", real));
            }
        }
        return found;
    }

    /// <summary>
    /// A relationship whose target is outside the package. Anything but a missing TargetMode or
    /// "Internal" counts, so a spelling Word might still read as External ("external") is cut too.
    /// </summary>
    private static bool IsExternal(XElement rel) =>
        rel.Attribute("TargetMode") is { } mode && !mode.Value.Trim().Equals("Internal", StringComparison.OrdinalIgnoreCase);

    /// <summary>A relationship target as a package part name: escapes decoded, "." and ".." applied.</summary>
    private static string? ResolvePart(string baseDir, string? target)
    {
        if (string.IsNullOrEmpty(target)) return null;
        string t = Uri.UnescapeDataString(target).Replace('\\', '/');
        int cut = t.IndexOfAny(['?', '#']);
        if (cut >= 0) t = t[..cut];
        var parts = new List<string>();
        foreach (string seg in ((t.StartsWith('/') ? "" : baseDir) + t).Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    private static byte[]? ReadCapped(ZipArchiveEntry entry, long cap)
    {
        try
        {
            using var s = entry.Open();
            using var ms = new MemoryStream();
            var buffer = new byte[81920];
            long total = 0;
            int n;
            while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                if ((total += n) > cap) return null;
                ms.Write(buffer, 0, n);
            }
            return ms.ToArray();
        }
        catch (InvalidDataException) { return null; }
    }

    private static void Overwrite(ZipArchiveEntry entry, ReadOnlySpan<byte> data)
    {
        using var w = Recreate(entry).Open();
        w.Write(data);
    }

    /// <summary>
    /// A new, empty entry in place of <paramref name="entry"/>, to write its new contents into. Opening the
    /// old entry for writing in an archive opened for update would first inflate its old contents into
    /// memory (a nested package would be held twice). The new entry goes to the end of the archive:
    /// nothing rewritten here is an ODF mimetype (which must stay first, stored), and OOXML does not
    /// depend on the order of its parts.
    /// </summary>
    private static ZipArchiveEntry Recreate(ZipArchiveEntry entry)
    {
        var zip = entry.Archive;
        string name = entry.FullName;
        var time = entry.LastWriteTime;
        entry.Delete();
        var fresh = zip.CreateEntry(name, CompressionLevel.Optimal);
        fresh.LastWriteTime = time;
        return fresh;
    }

    /// <summary>
    /// A field whose name (its first word) is one of these never loads anything: its text is left
    /// alone, so a hyperlink to ".../rd/..." or ".../import" keeps its address. Any other field is
    /// searched for a fetching name anywhere in it, the cautious way.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex SafeField = new(
        @"^[ \t\r\n]*(?:=|(?:HYPERLINK|PAGE|NUMPAGES|SECTIONPAGES|SECTION|DATE|TIME|CREATEDATE|SAVEDATE|PRINTDATE|EDITTIME"
        + @"|REF|PAGEREF|NOTEREF|STYLEREF|TOC|TOA|TA|TC|XE|INDEX|SEQ|QUOTE|MERGEFIELD|MERGEREC|MERGESEQ|NEXT|NEXTIF|SKIPIF"
        + @"|FORMTEXT|FORMCHECKBOX|FORMDROPDOWN|SYMBOL|EQ|ADVANCE|IF|COMPARE|AUTHOR|TITLE|SUBJECT|KEYWORDS|COMMENTS|FILENAME"
        + @"|FILESIZE|NUMWORDS|NUMCHARS|DOCPROPERTY|DOCVARIABLE|USERNAME|USERINITIALS|USERADDRESS|LISTNUM|AUTONUM|AUTONUMLGL"
        + @"|AUTONUMOUT|BIBLIOGRAPHY|CITATION|BARCODE|DISPLAYBARCODE|GOTOBUTTON|MACROBUTTON|SET|PRIVATE|ADDIN|BIDIOUTLINE"
        + @"|GREETINGLINE|ADDRESSBLOCK|LASTSAVEDBY|TEMPLATE|INFO|REVNUM)(?=[ \t\r\n""\\]|$))",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Turns a fetching field into QUOTE; counts one per field, however often the name appears.</summary>
    private static string Neutralize(string instruction, ref int cut)
    {
        if (SafeField.IsMatch(instruction)) return instruction;
        int found = 0;
        string result = FetchingField.Replace(instruction, _ => { found++; return "QUOTE"; });
        if (found == 0 && FetchingField.IsMatch(Fold(instruction))) { cut++; return Inert; } // spelled with look-alike letters
        if (found > 0) cut++;
        return result;
    }

    // The field's name is spelled out before anything nested: one word, then a space.
    private static readonly System.Text.RegularExpressions.Regex NamedField = new(@"^\s*\S+\s");

    // What a field printed by QUOTE shows when its real instruction could not be trusted.
    private const string Inert = " QUOTE \"\" ";

    // A dirty field is updated on open, and an updated QUOTE prints its argument (the URL) instead of
    // what the author saw. Without the flag Word shows the stored result.
    private static void KeepResult(XElement field) =>
        field.Attributes().Where(a => a.Name.LocalName == "dirty").Remove();

    private sealed class FieldCode(XElement begin)
    {
        public readonly XElement Begin = begin; // the fldChar that opens the field
        public readonly HashSet<XElement> Above = begin.Ancestors().ToHashSet(); // where its code is read from
        public readonly List<XElement> Pieces = new();
        public readonly System.Text.StringBuilder Head = new(); // instruction text before the first nested field
        public int HeadPieces; // how many pieces that text is
        public bool Nested, Collecting = true;
        public int FirstWrapped = -1; // the first piece Word may skip (see Plain), -1 when none
        public bool Reshaped; // a begin, separate or end inside the code sits where Word may skip it
    }

    // Elements Word reads straight through when it joins a field's code: runs, tracked insertions,
    // links, content controls, paragraphs and tables. Anything else (markup-compatibility wrappers,
    // ignorable or unknown elements, tracked deletions, properties) Word may skip, so a piece below
    // one may not be part of the code Word reads.
    private static readonly HashSet<string> ReadThrough = new(StringComparer.Ordinal)
    {
        "r", "ins", "moveTo", "hyperlink", "smartTag", "customXml", "sdt", "sdtContent", "fldSimple", "bdo", "dir", "p", "tbl", "tr", "tc",
    };

    /// <summary>
    /// True when Word certainly reads <paramref name="e"/> (a w:instrText or w:fldChar) as part of the
    /// field opened above it: it is in the WordprocessingML namespace (transitional or Strict) and
    /// nothing between it and that field's begin (or, for a field's own begin, its paragraph) is a
    /// wrapper Word may skip or an element of the other namespace. A w:delInstrText is deleted text,
    /// which Word does not read as code.
    /// </summary>
    private static bool Plain(XElement e, HashSet<XElement>? above)
    {
        var ns = e.Name.Namespace;
        if (!IsW(ns) || e.Name.LocalName == "delInstrText") return false;
        for (var a = e.Parent; a is not null; a = a.Parent)
        {
            if (above is not null ? above.Contains(a) : a.Name == ns + "p") return true;
            if (a.Name.Namespace != ns || !ReadThrough.Contains(a.Name.LocalName)) return false;
        }
        return false; // no paragraph above: not where Word reads a field
    }

    /// <summary>
    /// Word splits one instruction over several w:instrText runs ("INCLUDE" in one, "PICTURE" in the
    /// next), so the pieces of each field (fldChar begin … separate/end, nesting followed) are joined
    /// before the check. A field whose name could come from a nested field cannot be read here and only
    /// prints. A changed instruction goes into its first piece, the others are emptied.
    ///
    /// Word does not read everything that is in the XML: it takes one branch of an mc:AlternateContent,
    /// skips mc:Ignorable elements and does not read deleted code, while this join reads all of it. So
    /// "INCLUDE" + [ignorable "X"] + "TEXT" is harmless here and INCLUDETEXT in Word. Every field whose
    /// code has a piece, a begin, a separate or an end where Word may skip it is therefore made inert,
    /// unless the plain pieces in front already spell a name that never loads anything. A begin, separate
    /// or end Word may skip does not open or close anything here: the field goes on collecting, so
    /// everything Word might read as its code is in its pieces and emptied with them.
    /// </summary>
    private static int NeutralizeFields(XDocument xml)
    {
        int cut = 0;
        var open = new Stack<FieldCode>();
        foreach (var e in xml.Descendants().ToList())
        {
            string name = e.Name.LocalName;
            if (name is "instrText" or "delInstrText") // deleted text of a tracked change may still be read
            {
                if (open.Count > 0 && open.Peek().Collecting)
                {
                    var f = open.Peek();
                    if (f.FirstWrapped < 0 && !Plain(e, f.Above)) f.FirstWrapped = f.Pieces.Count;
                    f.Pieces.Add(e);
                    if (!f.Nested) { f.Head.Append(e.Value); f.HeadPieces++; }
                }
                else e.Value = Neutralize(e.Value, ref cut); // outside any field: checked on its own
                continue;
            }
            if (name != "fldChar") continue;
            var top = open.Count > 0 ? open.Peek() : null;
            bool plain = Plain(e, top?.Above);
            // Word may see a different nesting: every field whose code is still being read is affected.
            if (!plain) foreach (var f in open) if (f.Collecting) f.Reshaped = true;
            // Word reads w:fldCharType in the element's own namespace. If that one is missing, holds
            // another value, or has a namesake (x:fldCharType, an unqualified one), this code cannot know
            // which of begin, separate or end Word sees: every open field becomes inert, and the mark
            // opens a field of its own here, so whatever code follows it is caught as well.
            string? type = IsW(e.Name.Namespace) ? OwnAttribute(e, e.Name.Namespace, "fldCharType") : null;
            if (type is not ("begin" or "separate" or "end"))
            {
                foreach (var f in open) cut += Disarm(f);
                KeepResult(e);
                open.Push(new FieldCode(e) { Reshaped = true });
                continue;
            }
            switch (type)
            {
                case "begin":
                    if (top is { Collecting: true }) top.Nested = true;
                    open.Push(new FieldCode(e) { Reshaped = !plain });
                    break;
                case "separate" when plain && top is { Collecting: true }:
                    cut += Finish(top);
                    top.Collecting = false;
                    break;
                case "end" when plain && top is not null:
                    open.Pop();
                    if (top.Collecting) cut += Finish(top);
                    break;
            }
        }
        while (open.Count > 0) // left open by a broken file: still checked
        {
            var f = open.Pop();
            if (f.Collecting) cut += Finish(f);
        }
        return cut;
    }

    /// <summary>
    /// For a field Word may read differently: the plain pieces before the first one Word may skip (and
    /// before any nested field) already hold a name that never loads anything, followed by its delimiter,
    /// so every reading Word could make starts with that name. Fetching names glued or spelled with
    /// look-alike letters in any reading (see <see cref="LooseFetching"/>, <see cref="Fold"/>) still make it inert.
    /// </summary>
    private static bool NameFixed(FieldCode f)
    {
        if (f.Reshaped || f.FirstWrapped <= 0) return false;
        int n = f.Nested ? Math.Min(f.FirstWrapped, f.HeadPieces) : f.FirstWrapped;
        string lead = string.Concat(f.Pieces.Take(n).Select(p => p.Value));
        var m = SafeField.Match(lead);
        if (!m.Success || m.Length >= lead.Length) return false; // the name must end inside the plain pieces
        string all = string.Concat(f.Pieces.Select(p => p.Value));
        string plainOnly = string.Concat(f.Pieces.Where((p, i) => i < f.FirstWrapped || Plain(p, f.Above)).Select(p => p.Value));
        return !LooseFetching.IsMatch(Fold(all)) && !LooseFetching.IsMatch(Fold(plainOnly));
    }

    /// <summary>
    /// A field Word may read differently from here: its code, still being read or already read and
    /// kept, is made inert and its dirty flag goes. Returns 1 when that changed a code that was kept.
    /// </summary>
    private static int Disarm(FieldCode f)
    {
        KeepResult(f.Begin);
        if (f.Collecting)
        {
            f.Reshaped = true; // made inert when it is finished
            return 0;
        }
        if (f.Pieces.Count == 0 || (f.Pieces[0].Value == Inert && f.Pieces.Skip(1).All(p => p.Value.Length == 0))) return 0;
        f.Pieces[0].Value = Inert;
        f.Pieces[0].SetAttributeValue(XNamespace.Xml + "space", "preserve");
        foreach (var p in f.Pieces.Skip(1)) p.Value = "";
        return 1;
    }

    private static int Finish(FieldCode f)
    {
        if (f.Pieces.Count == 0) return 0;
        int cut = 0;
        string result;
        if (f.Reshaped || f.FirstWrapped >= 0)
        {
            if (NameFixed(f)) return 0;
            result = Inert;
            cut = 1;
        }
        else
        {
            result = Neutralize(string.Concat(f.Pieces.Select(p => p.Value)), ref cut);
            if (cut == 0 && f.Nested && !NamedField.IsMatch(f.Head.ToString())) { result = Inert; cut = 1; }
        }
        if (cut == 0) return 0;
        f.Pieces[0].Value = result;
        f.Pieces[0].SetAttributeValue(XNamespace.Xml + "space", "preserve");
        foreach (var p in f.Pieces.Skip(1)) p.Value = "";
        KeepResult(f.Begin);
        return cut;
    }

    /// <summary>
    /// One RTF field instruction (from <c>\fldinst</c> to its group's closing brace). Its text is read
    /// the way Word reads it, groups, <c>\'hh</c> and <c>\uN</c> included, so a keyword split over groups
    /// or escaped letters is still found; such an instruction, or one whose name could come from a
    /// nested field, is replaced by an inert QUOTE. Anything else stays byte for byte.
    /// </summary>
    private static string NeutralizeRtfField(string instruction, ref int cut)
    {
        var nested = System.Text.RegularExpressions.Regex.Match(instruction, @"\\field(?![a-zA-Z])");
        bool unnamed = nested.Success && !NamedField.IsMatch(RtfText(instruction[..nested.Index]));
        string text = Fold(RtfText(instruction));
        // Simple: plain text after \fldinst, no groups, escapes or binary data, so the text read here is
        // the text Word reads. Only such an instruction may pass on its name alone (a nested field or a
        // destination group could put a harmless name in front of a fetching one).
        bool simple = !RtfTricks.IsMatch(instruction[8..]);
        if (simple && !nested.Success && SafeField.IsMatch(text)) return instruction;
        bool fetching = FetchingField.IsMatch(text) || (!simple && LooseFetching.IsMatch(text));
        if (!unnamed && !fetching) return instruction;
        cut++;
        return @"\fldinst" + Inert;
    }

    // Anything that can make Word's reading of an instruction differ from a plain left-to-right read.
    private static readonly System.Text.RegularExpressions.Regex RtfTricks = new(@"[{}]|\\(?:bin|uc|u-?[0-9])|\\'");

    // Fetching names without word boundaries, for instructions whose text may have been glued together
    // ("{\bkmkstart X}INCLUDEPICTURE" reads as "XINCLUDEPICTURE" here). HYPERLINK is not LINK.
    private static readonly System.Text.RegularExpressions.Regex LooseFetching = new(
        @"INCLUDE|IMPORT|DDE|DATABASE|FILLIN|(?<!HYPER)LINK",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Letters Word may read as the ASCII ones in a field name: fullwidth and other compatibility forms,
    /// the Turkish dotless and dotted I.
    /// </summary>
    private static string Fold(string text) =>
        text.Normalize(System.Text.NormalizationForm.FormKC).Replace('ı', 'i').Replace('İ', 'I');

    /// <summary>
    /// The text Word reads from an RTF fragment: ignorable destinations ({\*...}) and control words
    /// dropped, \'hh and \uN decoded, control symbols kept as their character.
    /// </summary>
    private static string RtfText(string rtf)
    {
        var sb = new System.Text.StringBuilder(rtf.Length);
        int uc = 1; // \ucN: how many fallback characters follow each \uN, kept per group
        var ucStack = new Stack<int>();
        for (int j = 0; j < rtf.Length; j++)
        {
            char ch = rtf[j];
            if (ch == '{')
            {
                if (j + 2 < rtf.Length && rtf[j + 1] == '\\' && rtf[j + 2] == '*') { j = GroupEnd(rtf, j + 1, 1); continue; }
                ucStack.Push(uc);
                continue;
            }
            if (ch == '}') { if (ucStack.Count > 0) uc = ucStack.Pop(); continue; }
            if (ch is '\r' or '\n') continue;
            if (ch != '\\') { sb.Append(ch); continue; }
            if (++j >= rtf.Length) break;
            ch = rtf[j];
            if (ch == '\'')
            {
                if (j + 2 < rtf.Length && int.TryParse(rtf.AsSpan(j + 1, 2), System.Globalization.NumberStyles.HexNumber, null, out int hex)) { sb.Append((char)hex); j += 2; }
                continue;
            }
            if (!char.IsAsciiLetter(ch)) { if (ch != '-') sb.Append(ch is '\\' or '{' or '}' ? ch : ' '); continue; } // \- is an invisible hyphen
            int k = j;
            while (k < rtf.Length && char.IsAsciiLetter(rtf[k])) k++;
            string word = rtf[j..k];
            int num = k;
            if (k < rtf.Length && rtf[k] == '-') k++;
            while (k < rtf.Length && char.IsAsciiDigit(rtf[k])) k++;
            bool hasNum = int.TryParse(rtf.AsSpan(num, k - num), out int n);
            if (k < rtf.Length && rtf[k] == ' ') k++; // the delimiter belongs to the control word
            if (word == "uc" && hasNum) uc = Math.Max(0, n);
            else if (word == "bin" && hasNum) k = Math.Min(rtf.Length, k + Math.Max(0, n)); // raw bytes, not text
            else if (word == "u" && hasNum)
            {
                sb.Append((char)(n < 0 ? n + 65536 : n));
                // the fallback characters readers without Unicode show instead: \ucN of them
                for (int f = 0; f < uc && k < rtf.Length; f++)
                {
                    if (k + 3 < rtf.Length && rtf[k] == '\\' && rtf[k + 1] == '\'') k += 4;
                    else if (rtf[k] is not ('\\' or '{' or '}')) k++;
                    else break;
                }
            }
            else if (word is "par" or "line" or "tab" or "cell" or "row" or "sect" or "page") sb.Append(' ');
            j = k - 1;
        }
        return sb.ToString();
    }

    /// <summary>
    /// RTF can name a remote template (<c>{\*\template ...}</c>) or a mail-merge data source, pull files
    /// in through fields and carry OLE links that update themselves on open. Those groups are removed;
    /// every field instruction that fetches or prompts (see <see cref="FetchingField"/>) becomes an inert QUOTE, see
    /// <see cref="NeutralizeRtfField"/>; automatic OLE links become manual ones and lose their
    /// "update before showing" flag. Returns how many were cut.
    /// </summary>
    private static int CutRtfReferences(string path)
    {
        // RTF is 7-bit ASCII with escapes; Latin-1 keeps every byte as one char and back.
        var latin1 = System.Text.Encoding.Latin1;
        int cut = 0;
        string rtf = CleanRtf(File.ReadAllText(path, latin1), ref cut);
        if (cut > 0) File.WriteAllText(path, rtf, latin1);
        return cut;
    }

    private static readonly System.Text.RegularExpressions.Regex ActingGroup = new(
        @"\{[\r\n ]*(?:\\\*[\r\n ]*)?\\(?:template|mailmerge)(?![a-zA-Z])", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string CleanRtf(string rtf, ref int cut)
    {
        var sb = new System.Text.StringBuilder(rtf.Length);
        int i = 0;
        while (i < rtf.Length)
        {
            // The attached template and a mail merge's data source act on open. Readers skip line
            // breaks between tokens and do not need the \* marker, so neither does the match.
            var group = ActingGroup.Match(rtf, i);
            int at = group.Success ? group.Index : -1;
            if (at < 0) { sb.Append(rtf, i, rtf.Length - i); break; }
            // An empty group in its place: removing it outright could join the control word before it
            // with the text after it ("\ansi{...}Hello" would become "\ansiHello" and eat the word).
            sb.Append(rtf, i, at - i).Append("{}");
            i = GroupEnd(rtf, at + 1, 1) + 1;
            cut++;
        }
        rtf = sb.ToString();

        sb.Clear();
        i = 0;
        while (i < rtf.Length)
        {
            int at = rtf.IndexOf(@"\fldinst", i, StringComparison.Ordinal);
            if (at < 0) { sb.Append(rtf, i, rtf.Length - i); break; }
            int end = GroupEnd(rtf, at, 1); // the instruction group that holds \fldinst, nested groups included
            string instruction = rtf[at..end];
            string cleaned = NeutralizeRtfField(instruction, ref cut);
            sb.Append(rtf, i, at - i);
            if (cleaned != instruction)
            {
                sb.Append(cleaned); // replaced as a whole, nested fields included
                i = end;
            }
            else
            {
                // Kept: a field nested in it ({\*\fldinst ...} is skipped when the outer text is read)
                // still has to be checked on its own, so the scan goes on inside.
                const string Keyword = @"\fldinst";
                sb.Append(Keyword);
                i = at + Keyword.Length;
            }
        }
        rtf = sb.ToString();

        int n = 0;
        rtf = System.Text.RegularExpressions.Regex.Replace(rtf, @"\\objupdate(?![a-zA-Z])", _ => { n++; return ""; });
        rtf = System.Text.RegularExpressions.Regex.Replace(rtf, @"\\objautlink(?![a-zA-Z])", _ => { n++; return @"\objlink"; });
        cut += n;
        return rtf;
    }

    private static readonly System.Text.RegularExpressions.Regex BinWord = new(@"\G\\bin([0-9]+) ?");

    /// <summary>Index of the brace that closes the group open at <paramref name="from"/> with the given depth (or the end of the text).</summary>
    private static int GroupEnd(string rtf, int from, int depth)
    {
        for (int j = from; j < rtf.Length; j++)
        {
            if (rtf[j] == '\\')
            {
                // \binN: N raw bytes follow, braces among them do not count
                var bin = BinWord.Match(rtf, j);
                if (bin.Success && int.TryParse(bin.Groups[1].ValueSpan, out int count)) { j = Math.Min(rtf.Length, bin.Index + bin.Length + count) - 1; continue; }
                j++; // escaped brace or control symbol
                continue;
            }
            if (rtf[j] == '{') depth++;
            else if (rtf[j] == '}' && --depth == 0) return j;
        }
        return rtf.Length;
    }

    // ---------- Microsoft Word (COM automation) ----------

    // WdSaveFormat values
    private const int WdFormatText = 2, WdFormatDocumentDefault = 16, WdFormatPdf = 17;
    private const int MsoAutomationSecurityForceDisable = 3, WdAlertsNone = 0, WdDoNotSaveChanges = 0;

    /// <summary>
    /// Runs Word invisibly on its own STA thread. Macros are force-disabled before the file is opened
    /// (the dropped file is not trusted), prompts are suppressed, and a Word process that hangs past
    /// the timeout is killed.
    /// </summary>
    // One Word conversion at a time: two wheels can run at once, and overlapping runs would read each
    // other's DisableConvertPdfWarning as "the user's value" and leave it changed for good.
    private static readonly object WordGate = new();

    private static bool TryWord(string input, string target, string ext)
    {
        lock (WordGate) return TryWordOnce(input, target, ext);
    }

    private static bool TryWordOnce(string input, string target, string ext)
    {
        // An earlier crash or kill may have left this file on Word's list; its prompt would hang us.
        ForgetCrash(input);
        var before = Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet();
        int ownPid = 0; // the WINWORD that our CreateInstance started, once known for certain
        bool ok = false, isPdf = Path.GetExtension(input).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        // Opening a PDF shows a "Word will now convert your PDF" message that DisplayAlerts does not
        // cover; invisible, it would hang. Word's own switch turns it off. Word reads it at start-up,
        // so it is set before Word starts and put back afterwards.
        Action? restorePdfWarning = isPdf && WordVersion() is string version ? SuppressPdfWarning(version) : null;
        var thread = new Thread(() =>
        {
            dynamic? app = null, doc = null;
            bool foreign = false;
            try
            {
                app = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!)!;
                // Ours is new and was started by COM ("/Automation" on its command line); a Word the user
                // opened meanwhile is new too but was not. Two such candidates cannot be told apart.
                var started = Process.GetProcessesByName("WINWORD").Where(p => !before.Contains(p.Id)).ToList();
                var automated = started.Where(p => IsAutomationWord(p.Id)).ToList();
                if (automated.Count == 1) Volatile.Write(ref ownPid, automated[0].Id);
                // Word 16 always starts a new instance for automation. If none started, COM handed us a
                // Word that was already running, maybe the user's own: hiding it, changing its settings or
                // quitting it is not ours to do, so this engine steps aside.
                if (started.Count == 0 && before.Count > 0)
                {
                    foreign = true;
                    Log.Write("word: no new instance started, the running Word is left alone");
                    return;
                }
                app.Visible = false;
                app.DisplayAlerts = WdAlertsNone;
                app.AutomationSecurity = MsoAutomationSecurityForceDisable;
                doc = app.Documents.Open(FileName: input, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false,
                    PasswordDocument: "", Visible: false, NoEncodingDialog: true);
                if (ext == ".pdf") doc.ExportAsFixedFormat(OutputFileName: target, ExportFormat: WdFormatPdf);
                else if (ext == ".docx") doc.SaveAs2(FileName: target, FileFormat: WdFormatDocumentDefault, AddToRecentFiles: false);
                else doc.SaveAs2(FileName: target, FileFormat: WdFormatText, AddToRecentFiles: false, Encoding: 65001); // UTF-8
                ok = File.Exists(target);
            }
            catch (Exception ex)
            {
                Log.Write($"word conversion failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                try { doc?.Close(WdDoNotSaveChanges); } catch { /* Word may already be gone */ }
                if (!foreign) try { app?.Quit(WdDoNotSaveChanges); } catch { /* same */ }
                if (doc is not null) Marshal.FinalReleaseComObject(doc);
                if (app is not null) Marshal.FinalReleaseComObject(app);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeoutMs))
        {
            // Only the Word this run started, never the user's own (not even one opened during the wait,
            // which holds their unsaved work). If start-up itself hung, ownPid is unknown: then the one new
            // WINWORD started by COM is ours. If there is not exactly one, nothing is killed.
            int pid = Volatile.Read(ref ownPid);
            var victims = Process.GetProcessesByName("WINWORD")
                .Where(p => (pid != 0 ? p.Id == pid : !before.Contains(p.Id)) && IsAutomationWord(p.Id)).ToList();
            if (victims.Count == 1)
            {
                try { victims[0].Kill(true); } catch { /* already gone */ }
                Log.Write($"word conversion timed out (killed pid {victims[0].Id})");
            }
            else Log.Write($"word conversion timed out ({victims.Count} candidate Word processes, none killed)");
            ok = false;
            ForgetCrash(input);
        }
        restorePdfWarning?.Invoke();
        if (!ok) TryDelete(target);
        return ok;
    }

    /// <summary>
    /// A Word started through COM: its command line carries "/Automation" (with -Embedding); one the
    /// user starts does not. False when the command line cannot be read, so such a process is never
    /// taken for ours.
    /// </summary>
    private static bool IsAutomationWord(int pid) =>
        CommandLine(pid)?.Contains("/Automation", StringComparison.OrdinalIgnoreCase) == true;

    private const int ProcessQueryLimitedInformation = 0x1000, ProcessCommandLineInformation = 60;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returned);

    /// <summary>Another process's command line (Windows 8.1 and later), null when it cannot be read.</summary>
    private static string? CommandLine(int pid)
    {
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(process, ProcessCommandLineInformation, IntPtr.Zero, 0, out int size);
            if (size <= 0 || size > 1 << 20) return null;
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (NtQueryInformationProcess(process, ProcessCommandLineInformation, buffer, size, out _) != 0) return null;
                // A UNICODE_STRING: its length in bytes, then a pointer to the text (inside the same buffer).
                int bytes = (ushort)Marshal.ReadInt16(buffer);
                IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return text == IntPtr.Zero ? null : Marshal.PtrToStringUni(text, bytes / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return null; }
        finally { CloseHandle(process); }
    }

    /// <summary>
    /// Killing Word makes it list the file under Resiliency\DisabledItems, and next time Word asks
    /// "this file caused a serious error, open anyway?" in a dialog nobody sees. Word writes the entry
    /// when it next starts, not when it is killed, and each run converts from a new work folder, so
    /// besides the entries that name this file, every entry under one of Swivra's own work folders
    /// (%TEMP%\swivra-word-…) goes too. The user's other entries stay.
    /// </summary>
    private static void ForgetCrash(string input)
    {
        if (WordVersion() is not string version) return;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Office\{version}\Word\Resiliency\DisabledItems", writable: true);
            if (key is null) return;
            string needle = input.ToLowerInvariant();
            foreach (string name in key.GetValueNames())
            {
                if (key.GetValue(name) is not byte[] data) continue;
                string text = System.Text.Encoding.Unicode.GetString(data).ToLowerInvariant();
                if (!text.Contains(needle) && !text.Contains(@"\swivra-word-", StringComparison.Ordinal)) continue;
                key.DeleteValue(name, false);
                Log.Write("word crash entry for a converted file removed");
            }
        }
        catch (Exception ex) { Log.Write($"word resiliency cleanup failed: {ex.Message}"); }
    }

    /// <summary>"16.0" from the registered Word.Application.16 ProgID.</summary>
    private static string? WordVersion()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"Word.Application\CurVer");
            return key?.GetValue(null) is string cur && cur.LastIndexOf('.') is int dot and > 0 ? cur[(dot + 1)..] + ".0" : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Sets Word's DisableConvertPdfWarning option for the current user and returns how to put the
    /// previous value back, so the user's own Word keeps behaving as before.
    /// </summary>
    private static Action? SuppressPdfWarning(string version)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Office\{version}\Word\Options");
            object? old = key.GetValue(PdfWarningName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            var kind = old is null ? RegistryValueKind.Unknown : key.GetValueKind(PdfWarningName);
            // Noted first: if Swivra is closed mid-conversion, the next start puts the user's value back.
            Settings.Set(PdfWarningPending, $"{version}|{(int)kind}|{Encode(old)}");
            key.SetValue(PdfWarningName, 1, RegistryValueKind.DWord);
            return () => RestorePdfWarning(version, old, kind);
        }
        catch (Exception ex)
        {
            Log.Write($"word option set failed: {ex.Message}");
            return null;
        }
    }

    private const string PdfWarningName = "DisableConvertPdfWarning";
    private const string PdfWarningPending = "wordPdfWarningRestore";

    /// <summary>Puts the user's value back exactly as it was, type included; no value before means none after.</summary>
    private static void RestorePdfWarning(string version, object? old, RegistryValueKind kind)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Office\{version}\Word\Options");
            if (old is null) k.DeleteValue(PdfWarningName, false);
            else k.SetValue(PdfWarningName, old, kind);
            Settings.Set(PdfWarningPending, null);
        }
        catch (Exception ex) { Log.Write($"word option restore failed: {ex.Message}"); }
    }

    // The old value as text for settings.json, and back. Binary as base64, a multi-string joined by NUL.
    private static string Encode(object? value) => value switch
    {
        null => "",
        byte[] b => System.Convert.ToBase64String(b),
        string[] m => string.Join('\0', m),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static object? Decode(RegistryValueKind kind, string text) => kind switch
    {
        RegistryValueKind.Unknown => null,
        RegistryValueKind.DWord => int.Parse(text, System.Globalization.CultureInfo.InvariantCulture), // GetValue gives a DWORD as int
        RegistryValueKind.QWord => long.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
        RegistryValueKind.Binary => System.Convert.FromBase64String(text),
        RegistryValueKind.MultiString => text.Split('\0'),
        _ => text,
    };

    /// <summary>At start-up: a PDF conversion cut short by closing Swivra left Word's option changed; put it back.</summary>
    public static void RestoreLeftovers()
    {
        if (Settings.GetString(PdfWarningPending) is not { } pending) return;
        var parts = pending.Split('|', 3);
        try
        {
            if (parts.Length != 3 || parts[0].Length == 0) throw new FormatException(pending);
            var kind = (RegistryValueKind)int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            RestorePdfWarning(parts[0], Decode(kind, parts[2]), kind);
            Log.Write("word option restored after an interrupted conversion");
        }
        catch (Exception ex)
        {
            Log.Write($"word option note unreadable, dropped: {ex.Message}");
            Settings.Set(PdfWarningPending, null);
        }
    }

    // ---------- LibreOffice (headless) ----------

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

    private static bool TryLibreOffice(string soffice, string input, string target, string ext)
    {
        string work = Directory.CreateTempSubdirectory("swivra-lo-").FullName;
        try
        {
            string outDir = Path.Combine(work, "out");
            Directory.CreateDirectory(outDir);
            // Private profile so a running LibreOffice window or a locked default profile cannot block us.
            var psi = new ProcessStartInfo(soffice) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            WriteLibreOfficeProfile(Path.Combine(work, "profile")); // hardened: no macros, no link updates, see there
            psi.ArgumentList.Add("-env:UserInstallation=" + new Uri(Path.Combine(work, "profile")).AbsoluteUri);
            psi.ArgumentList.Add("--headless");
            psi.ArgumentList.Add("--norestore");
            if (Path.GetExtension(input).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add("--infilter=writer_pdf_import");
            psi.ArgumentList.Add("--convert-to");
            psi.ArgumentList.Add(ext == ".txt" ? "txt:Text (encoded):UTF8" : ext.TrimStart('.'));
            psi.ArgumentList.Add("--outdir");
            psi.ArgumentList.Add(outDir);
            psi.ArgumentList.Add(input);
            using var proc = Process.Start(psi)!;
            // Drain output so a full pipe cannot stall the process.
            _ = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(TimeoutMs))
            {
                try { proc.Kill(true); } catch { /* already gone */ }
                Log.Write("libreoffice conversion timed out");
                return false;
            }
            string produced = Path.Combine(outDir, Path.GetFileNameWithoutExtension(input) + ext);
            if (!File.Exists(produced)) return false;
            File.Move(produced, target);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"libreoffice conversion failed: {ex.Message}");
            return false;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* harmless leftovers */ }
        }
    }

    private static void TryDelete(string path)
    {
        SafeFile.TryDelete(path);
    }
}

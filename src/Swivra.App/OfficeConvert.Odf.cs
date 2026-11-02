using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace Swivra.App;

/// <summary>Raised when a document cannot be cleaned with confidence: it is not handed to an office program at all.</summary>
internal sealed class UnsafeDocumentException(string reason) : Exception(reason);

/// <summary>
/// OpenDocument text (.odt) from someone else. LibreOffice, which gets an .odt first, loads linked
/// pictures, sections, objects and fonts, probes the attached template, connects DDE and form data
/// sources, and can run the macros a package carries. Word's ODF import was measured to fetch none of
/// these, but the same cleaned copy goes to both.
///
/// The cleaning is generic rather than a list of elements: in every XML part (sub-documents in
/// "Object N/" and SVG pictures included) every href that does not point inside the package is cut.
/// Code goes as a whole (Basic, Scripts, Dialogs, Configurations2, event listeners), and settings.xml is
/// edited, never replaced, because its compatibility flags decide the layout. Whatever cannot be read
/// with confidence is refused, never passed on as it is.
/// </summary>
internal static partial class OfficeConvert
{
    private static readonly XNamespace XLink = "http://www.w3.org/1999/xlink";
    private static readonly XNamespace OdfOffice = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace OdfScript = "urn:oasis:names:tc:opendocument:xmlns:script:1.0";
    private static readonly XNamespace OdfText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace OdfTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace OdfDraw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace OdfMeta = "urn:oasis:names:tc:opendocument:xmlns:meta:1.0";
    private static readonly XNamespace OdfForm = "urn:oasis:names:tc:opendocument:xmlns:form:1.0";
    private static readonly XNamespace OdfConfig = "urn:oasis:names:tc:opendocument:xmlns:config:1.0";
    private static readonly XNamespace OdfPresentation = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";
    private static readonly XNamespace OdfManifest = "urn:oasis:names:tc:opendocument:xmlns:manifest:1.0";
    private static readonly XNamespace OdfStyle = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";

    private const string OdfPrefix = "application/vnd.oasis.opendocument.";

    // Only text documents are accepted (.odt and its template); a spreadsheet's formulas can fetch too
    // and cannot be rewritten safely.
    private static readonly HashSet<string> OdfTextTypes = new(StringComparer.Ordinal)
    {
        "application/vnd.oasis.opendocument.text",
        "application/vnd.oasis.opendocument.text-template",
    };

    // Storages that hold code or UI bindings to code; signatures go too (the file is changed anyway, and a
    // trusted signature is what could let macros run).
    private static readonly string[] CodeStorages = ["Basic/", "Scripts/", "Dialogs/", "Configurations2/"];

    private static bool IsCodeStorage(string path) => CodeStorages.Any(c => path.StartsWith(c, StringComparison.OrdinalIgnoreCase));

    // "" is a plain folder (older OpenOffice lists "Pictures/" that way).
    private static bool SubDocumentOk(string mediaType) => mediaType is "" or
        "application/vnd.oasis.opendocument.chart" or "application/vnd.oasis.opendocument.formula" or
        "application/vnd.oasis.opendocument.graphics" or "application/vnd.oasis.opendocument.text";

    /// <summary>True for a ZIP that is an OpenDocument package (mimetype entry or ODF manifest).</summary>
    private static bool IsOdfPackage(string path)
    {
        using var f = SafeFile.OpenRead(path);
        using var zip = new ZipArchive(f, ZipArchiveMode.Read);
        return OdfMime(zip) is not null || zip.GetEntry("META-INF/manifest.xml") is not null;
    }

    private static bool HasContentTypes(string path)
    {
        using var f = SafeFile.OpenRead(path);
        using var zip = new ZipArchive(f, ZipArchiveMode.Read);
        return zip.GetEntry("[Content_Types].xml") is not null;
    }

    private static string? OdfMime(ZipArchive zip)
    {
        var e = zip.GetEntry("mimetype");
        if (e is null || e.Length > 256) return null;
        using var r = new StreamReader(e.Open(), Encoding.ASCII);
        string mime = r.ReadToEnd().Trim();
        return mime.StartsWith(OdfPrefix, StringComparison.Ordinal) ? mime : null;
    }

    /// <summary>Cleans the copy in place; throws <see cref="UnsafeDocumentException"/> when it cannot.</summary>
    private static void CleanOdf(string path, Cleaning report)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        using var zip = new ZipArchive(file, ZipArchiveMode.Update);
        try { CheckEntries(zip); }
        catch (InvalidDataException ex) { throw new UnsafeDocumentException(ex.Message); }

        // A package that is both OOXML and ODF reads differently in each program: no cleaning covers both.
        if (zip.GetEntry("[Content_Types].xml") is not null) throw new UnsafeDocumentException("both OOXML and ODF");
        string mime = OdfMime(zip) ?? throw new UnsafeDocumentException("no ODF mimetype");
        if (!OdfTextTypes.Contains(mime)) throw new UnsafeDocumentException($"ODF type {mime} not handled");

        // Entry names are matched as written below: a "./Basic/…", "/Scripts/…" or "a\b" spelling would slip past
        // those matches while a reader may still resolve it. No real package needs one.
        foreach (var e in zip.Entries)
        {
            string n = e.FullName;
            if (n.StartsWith('/') || n.StartsWith("./", StringComparison.Ordinal) || n.Contains('\\') || n.Split('/').Any(s => s is ".." or "."))
                throw new UnsafeDocumentException($"entry name {n}");
        }

        var manifestEntry = zip.GetEntry("META-INF/manifest.xml") ?? throw new UnsafeDocumentException("no manifest");
        XDocument manifest = ParseOdfPart(manifestEntry);
        if (manifest.Descendants(OdfManifest + "encryption-data").Any()) throw new UnsafeDocumentException("encrypted ODF");
        // A sub-document loads its own module: a spreadsheet brings formulas that fetch, a presentation its
        // sounds. Charts, formulas, drawings and text are what a text document embeds. The manifest may name
        // a folder with or without its trailing slash; a folder it does not name at all is not trusted.
        var folderTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fe in manifest.Descendants(OdfManifest + "file-entry"))
        {
            string full = (string?)fe.Attribute(OdfManifest + "full-path") ?? "";
            string type = (string?)fe.Attribute(OdfManifest + "media-type") ?? "";
            // LibreOffice reads the type from the manifest too: it must agree with mimetype.
            if (full == "/") { if (type != mime) throw new UnsafeDocumentException($"manifest type {type} differs from mimetype {mime}"); continue; }
            folderTypes[full.TrimEnd('/')] = type;
        }
        var folders = zip.Entries.Select(e => e.FullName).Where(n => n.Contains('/'))
            .SelectMany(n => { var segs = n.Split('/'); return Enumerable.Range(1, segs.Length - 1).Select(i => string.Join('/', segs.Take(i))); })
            .ToHashSet(StringComparer.Ordinal);
        foreach (string folder in folders)
        {
            if (IsCodeStorage(folder + "/") || folder.Equals("META-INF", StringComparison.OrdinalIgnoreCase)) continue;
            bool isDocument = zip.GetEntry(folder + "/content.xml") is not null;
            folderTypes.TryGetValue(folder, out string? type);
            if (type is not null && !SubDocumentOk(type)) throw new UnsafeDocumentException($"embedded {type} at {folder}");
            if (isDocument && string.IsNullOrEmpty(type)) throw new UnsafeDocumentException($"sub-document {folder} without a known type");
        }
        // Also by name without a folder: an entry "Object 1" typed as a document but stored as one stream.
        foreach (var (entryPath, type) in folderTypes)
            if (!folders.Contains(entryPath) && type.StartsWith(OdfPrefix, StringComparison.Ordinal) && !SubDocumentOk(type))
                throw new UnsafeDocumentException($"embedded {type} at {entryPath}");
        // An OLE object (binary storage, or inline in the XML) cannot be looked into; on Windows LibreOffice
        // may start its server to draw it, and it can carry a link moniker to a share.
        foreach (var e in zip.Entries.Where(e => !e.FullName.EndsWith('/')))
        {
            Span<byte> head = stackalloc byte[8];
            int got;
            using (var s = e.Open()) got = s.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            if (got >= 8 && head.SequenceEqual(OleSignature)) throw new UnsafeDocumentException($"OLE object {e.FullName}");
            // A gzip part (an .svgz picture) is opened by the SVG importer by its content; it is not read here.
            if (got >= 2 && head[0] == 0x1F && head[1] == 0x8B) throw new UnsafeDocumentException($"compressed part {e.FullName}");
            // A package inside the package: real ODF stores sub-documents as folders, never as a nested ZIP.
            if (got >= 4 && head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4) throw new UnsafeDocumentException($"nested package {e.FullName}");
        }

        // Code and signatures go as a whole; a storage counts once.
        foreach (string storage in CodeStorages)
        {
            var inside = zip.Entries.Where(e => e.FullName.StartsWith(storage, StringComparison.OrdinalIgnoreCase)).ToList();
            if (inside.Count == 0) continue;
            foreach (var e in inside) e.Delete();
            report.Macros++;
        }
        foreach (var e in zip.Entries.Where(e => e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)
                     && e.FullName.EndsWith("signatures.xml", StringComparison.OrdinalIgnoreCase)).ToList())
            e.Delete();

        var names = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
        foreach (var entry in zip.Entries.ToList())
        {
            string name = entry.FullName;
            if (name == "mimetype" || name.EndsWith('/') || name.Equals("META-INF/manifest.xml", StringComparison.OrdinalIgnoreCase)) continue;
            byte[] bytes = ReadCapped(entry, MaxChunkBytes) ?? throw new UnsafeDocumentException($"part {name} too large");
            bool xmlByName = name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".rdf", StringComparison.OrdinalIgnoreCase);
            if (!xmlByName && !LooksLikeXml(bytes)) continue; // pictures and other binary parts
            XDocument xml;
            try
            {
                using var s = new MemoryStream(bytes);
                xml = LoadXml(s);
            }
            catch (System.Xml.XmlException ex)
            {
                // Markup this parser rejects (an internal DTD entity, XML 1.1) may still be read by another
                // parser, an SVG importer say, with whatever it hides: refused, never passed on uncleaned.
                throw new UnsafeDocumentException($"part {name} is not well-formed XML ({ex.Message})");
            }
            bool changed = CleanOdfXml(xml, PartDir(name), names, report);
            // Sub-documents have their own settings.xml.
            if (name == "settings.xml" || name.EndsWith("/settings.xml", StringComparison.Ordinal)) changed |= CleanOdfSettings(xml, report);
            // A DOCTYPE is dropped by the parser (never resolved here); writing the part back removes it, so an
            // external DTD it names never reaches the office program's own parser.
            if (bytes.AsSpan().IndexOf("<!DOCTYPE"u8) >= 0) changed = true;
            if (changed) Overwrite(entry, SaveXml(xml));
        }

        // The manifest must match the package, or LibreOffice asks to repair it (and the conversion fails).
        bool manifestChanged = false;
        foreach (var fe in manifest.Descendants(OdfManifest + "file-entry").ToList())
        {
            string? full = (string?)fe.Attribute(OdfManifest + "full-path");
            if (full is null or "/") continue;
            bool exists = full.EndsWith('/') ? names.Any(n => n.StartsWith(full, StringComparison.Ordinal)) : names.Contains(full);
            if (exists) continue;
            fe.Remove();
            manifestChanged = true;
        }
        if (manifestChanged) Overwrite(manifestEntry, SaveXml(manifest));
    }

    private static XDocument ParseOdfPart(ZipArchiveEntry entry)
    {
        byte[] bytes = ReadCapped(entry, MaxChunkBytes) ?? throw new UnsafeDocumentException($"part {entry.FullName} too large");
        try
        {
            using var s = new MemoryStream(bytes);
            return LoadXml(s);
        }
        catch (System.Xml.XmlException ex) { throw new UnsafeDocumentException($"part {entry.FullName} is not well-formed XML ({ex.Message})"); }
    }

    private static byte[] SaveXml(XDocument xml)
    {
        using var ms = new MemoryStream();
        using (var w = System.Xml.XmlWriter.Create(ms, new System.Xml.XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
            xml.Save(w);
        return ms.ToArray();
    }

    /// <summary>"Object 1/content.xml" resolves its links from "Object 1/"; a root part from the root.</summary>
    private static string PartDir(string name)
    {
        int slash = name.LastIndexOf('/');
        return slash < 0 ? "" : name[..(slash + 1)];
    }

    // Elements that run code or open a live connection; removed whole.
    private static readonly XName[] CodeElements =
    [
        OdfOffice + "scripts", OdfOffice + "event-listeners", OdfScript + "event-listener",
        OdfPresentation + "event-listener", OdfText + "script", OdfDraw + "applet",
        OdfText + "dde-connection-decls", OdfText + "section-source-dde", OdfTable + "dde-links",
        OdfMeta + "template", OdfMeta + "auto-reload",
    ];

    // Form attributes that name a data source, a query or where a form sends its data.
    private static readonly XName[] FormSourceAttributes =
    [
        OdfForm + "datasource", OdfForm + "command", OdfForm + "action", OdfForm + "href",
        OdfForm + "image-data", OdfForm + "target-location",
    ];

    // Links only followed on a click keep their address, unless the address runs code.
    private static readonly HashSet<XName> ClickLinks = [OdfText + "a", OdfDraw + "a"];

    private static bool CleanOdfXml(XDocument xml, string partDir, HashSet<string> names, Cleaning report)
    {
        bool changed = false;

        foreach (var el in xml.Descendants().Where(e => CodeElements.Contains(e.Name)).ToList())
        {
            if (el.Document is null) continue; // already gone with an ancestor (a listener inside a removed list)
            bool dde = el.Name.LocalName.Contains("dde", StringComparison.Ordinal);
            if (dde) report.Links++;
            else if (el.Name == OdfMeta + "template" || el.Name == OdfMeta + "auto-reload")
            {
                // Always removed. Word writes a bare "Normal.dotm" into every .odt it saves; that names no other
                // place, so only a template or reload address that does is reported as a cut link.
                string href = ((string?)el.Attribute(XLink + "href") ?? "").Trim();
                if (href.Length > 0 && (href.Contains('/') || href.Contains('\\') || href.Contains(':'))) report.Links++;
            }
            else report.Macros++;
            el.Remove();
            changed = true;
        }
        // A DDE field keeps the text it last showed, as plain text.
        foreach (var el in xml.Descendants(OdfText + "dde-connection").ToList())
        {
            el.ReplaceWith(new XText(el.Value));
            changed = true;
        }

        // Processing instructions (an xml-stylesheet pulls in a sheet from anywhere) mean nothing in a package part.
        foreach (var pi in xml.DescendantNodes().OfType<XProcessingInstruction>().ToList())
        {
            pi.Remove();
            changed = true;
        }

        foreach (var el in xml.Descendants().ToList())
        {
            // A picture inline in the XML: raster images are kept, anything else (an SVG, an OLE object) is
            // not read here, so the document is refused rather than passed on with it.
            if (el.Name == OdfOffice + "binary-data" && !IsRasterBase64(el.Value))
                throw new UnsafeDocumentException("inline object that is not a raster picture");
            // CSS in SVG pictures (a <style> sheet in any namespace but ODF's own style elements) can load from an address too.
            if (el.Name.LocalName == "style" && el.Name.Namespace != OdfStyle && FetchesCss(el.Value))
                throw new UnsafeDocumentException("style sheet that loads from an address");

            foreach (var attr in el.Attributes().ToList())
            {
                // xml:base would move every relative link below it somewhere else.
                if (attr.Name == XNamespace.Xml + "base")
                {
                    attr.Remove();
                    changed = true;
                    continue;
                }
                // style="", and presentation attributes (fill, filter, mask, clip-path, marker-*) that take url().
                if (FetchesCss(attr.Value))
                    throw new UnsafeDocumentException($"attribute {attr.Name.LocalName} that loads from an address");
                if (FormSourceAttributes.Contains(attr.Name))
                {
                    attr.Remove();
                    report.Links++;
                    changed = true;
                    continue;
                }
                // xlink:href, and plain href (SVG 2 pictures) or any other namespace's href.
                if (attr.Name.LocalName != "href") continue;
                string href = attr.Value.Trim();
                bool click = ClickLinks.Contains(el.Name);
                if (click ? !RunsCode(href) && !LocalOrShare(href) : IsInsidePackage(href, partDir, names)) continue;
                attr.Value = Blocked;
                report.Links++;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>A hyperlink that starts a macro, a command or a script instead of opening an address.</summary>
    private static bool RunsCode(string href) =>
        href.StartsWith("vnd.sun.star.", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith("macro:", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith("service:", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith(".uno:", StringComparison.OrdinalIgnoreCase)
        || href.StartsWith("slot:", StringComparison.OrdinalIgnoreCase);

    /// <summary>A click link to a file or a network share: web addresses stay, these go (a click would reach the share).</summary>
    private static bool LocalOrShare(string href)
    {
        string h = href.Replace('\\', '/');
        return h.StartsWith("//", StringComparison.Ordinal) || h.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            || h.StartsWith("smb:", StringComparison.OrdinalIgnoreCase) || (h.Length >= 2 && h[1] is ':' or '|');
    }

    /// <summary>
    /// CSS that imports a sheet or uses url() for anything but a place in the same picture or inline data.
    /// Escapes ("\75 rl(") and comments are undone first, so they cannot hide either.
    /// </summary>
    private static bool FetchesCss(string css)
    {
        if (css.IndexOf('\\') >= 0) css = CssEscape.Replace(css, m => m.Groups[1].Success
            ? char.ConvertFromUtf32(Math.Min(0x10FFFF, System.Convert.ToInt32(m.Groups[1].Value, 16)))
            : m.Groups[2].Value);
        if (css.Contains("/*", StringComparison.Ordinal)) css = CssComment.Replace(css, "");
        if (css.Contains("@import", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (System.Text.RegularExpressions.Match m in CssUrl.Matches(css))
        {
            string target = m.Groups[1].Value.TrimStart();
            if (!target.StartsWith('#') && !target.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static readonly System.Text.RegularExpressions.Regex CssEscape = new(@"\\(?:([0-9a-fA-F]{1,6})\s?|(.))", System.Text.RegularExpressions.RegexOptions.Singleline);
    private static readonly System.Text.RegularExpressions.Regex CssComment = new(@"/\*.*?\*/", System.Text.RegularExpressions.RegexOptions.Singleline);

    private static readonly System.Text.RegularExpressions.Regex CssUrl = new(
        @"url\s*\(\s*['""]?([^'"")]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>PNG, JPEG, GIF, BMP, TIFF, WMF or EMF by its first bytes; anything else is not taken as a picture.</summary>
    private static bool IsRasterBase64(string base64)
    {
        string s = new(base64.Where(c => !char.IsWhiteSpace(c)).Take(64).ToArray());
        s = s[..(s.Length / 4 * 4)];
        byte[] h;
        try { h = System.Convert.FromBase64String(s); }
        catch (FormatException) { return false; }
        if (h.Length < 4) return false;
        return h is [0x89, (byte)'P', (byte)'N', (byte)'G', ..] or [0xFF, 0xD8, 0xFF, ..] or [(byte)'G', (byte)'I', (byte)'F', ..]
            or [(byte)'B', (byte)'M', ..] or [(byte)'I', (byte)'I', 42, 0, ..] or [(byte)'M', (byte)'M', 0, 42, ..]
            or [0xD7, 0xCD, 0xC6, 0x9A, ..] or [0x01, 0x00, 0x00, 0x00, ..] or [0x01, 0x00, 0x09, 0x00, ..] or [0x02, 0x00, 0x09, 0x00, ..];
    }

    /// <summary>
    /// An href that names a part (or sub-storage) of this package: no scheme, no absolute or UNC path,
    /// and it resolves, from the part's own folder, to something that is in the package. A relative path
    /// that leaves the package or names nothing in it would be looked for next to the document on disk.
    /// </summary>
    private static bool IsInsidePackage(string href, string partDir, HashSet<string> names)
    {
        if (href.Length == 0 || href.StartsWith('#')) return true; // nothing, or a place in this document
        string path;
        try { path = Uri.UnescapeDataString(href); }
        catch (UriFormatException) { return false; }
        path = path.Replace('\\', '/');
        if (path.StartsWith('/') || (path.Length >= 2 && path[1] == ':')) return false; // absolute, UNC or a drive
        int colon = path.IndexOf(':'), slash = path.IndexOf('/');
        if (colon >= 0 && (slash < 0 || colon < slash)) return false; // a scheme: http, file, smb, vnd.sun.star…
        int hash = path.IndexOf('#');
        if (hash >= 0) path = path[..hash];

        var parts = new List<string>(partDir.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (string seg in path.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..")
            {
                if (parts.Count == 0) return false; // leaves the package
                parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(seg);
        }
        if (parts.Count == 0) return false;
        string full = string.Join('/', parts);
        return names.Contains(full) || names.Any(n => n.StartsWith(full + "/", StringComparison.Ordinal));
    }

    /// <summary>
    /// Document settings that load or connect: links update never, no update from the template, no
    /// printer (a printer name can be a network share). Only these items change; the rest stays.
    /// </summary>
    private static bool CleanOdfSettings(XDocument xml, Cleaning report)
    {
        bool changed = false;
        foreach (var item in xml.Descendants(OdfConfig + "config-item").ToList())
        {
            string? n = (string?)item.Attribute(OdfConfig + "name");
            switch (n)
            {
                case "LinkUpdateMode":
                    if (item.Value != "0") { item.Value = "0"; changed = true; } // 0 = never
                    break;
                case "UpdateFromTemplate":
                    if (item.Value != "false") { item.Value = "false"; changed = true; }
                    break;
                case "PrinterName" or "PrinterSetup":
                    // A printer on a share is a network address like any link; the user hears about that one.
                    if (n == "PrinterName" && item.Value.TrimStart().StartsWith(@"\\", StringComparison.Ordinal)) report.Links++;
                    item.Remove();
                    changed = true;
                    break;
            }
        }
        return changed;
    }

    /// <summary>
    /// LibreOffice's private profile, written before it starts: macros never run, links never update,
    /// untrusted link targets are blocked, the document's printer is not loaded, and HTTP goes to a
    /// closed local port. Link, recalculation and printer values checked against LibreOffice's officecfg
    /// schema; the Security/Scripting names are from its documentation (unknown names are ignored).
    /// </summary>
    private static void WriteLibreOfficeProfile(string profileDir)
    {
        string user = Path.Combine(profileDir, "user");
        Directory.CreateDirectory(user);
        static string Item(string path, string prop, string value) =>
            $"<item oor:path=\"{path}\"><prop oor:name=\"{prop}\" oor:op=\"fuse\"><value>{value}</value></prop></item>";
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<oor:items xmlns:oor=\"http://openoffice.org/2001/registry\" xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">\n");
        sb.Append(Item("/org.openoffice.Office.Common/Security/Scripting", "MacroSecurityLevel", "3")).Append('\n');
        sb.Append(Item("/org.openoffice.Office.Common/Security/Scripting", "DisableMacrosExecution", "true")).Append('\n');
        sb.Append(Item("/org.openoffice.Office.Common/Security/Scripting", "BlockUntrustedRefererLinks", "true")).Append('\n');
        // The enumerations differ (officecfg Writer.xcs / Calc.xcs): Writer 0 always, 1 on request, 2 never;
        // Calc 0 always, 1 never, 2 on request. Recalculation on load: 1 never.
        sb.Append(Item("/org.openoffice.Office.Writer/Content/Update", "Link", "2")).Append('\n');
        sb.Append(Item("/org.openoffice.Office.Calc/Content/Update", "Link", "1")).Append('\n');
        sb.Append(Item("/org.openoffice.Office.Calc/Formula/Load", "ODFRecalcMode", "1")).Append('\n');
        sb.Append(Item("/org.openoffice.Office.Calc/Formula/Load", "OOXMLRecalcMode", "1")).Append('\n');
        sb.Append(Item("/org.openoffice.Office.Common/Save/Document", "LoadPrinter", "false")).Append('\n');
        sb.Append(Item("/org.openoffice.Inet/Settings", "ooInetProxyType", "2")).Append('\n');
        sb.Append(Item("/org.openoffice.Inet/Settings", "ooInetHTTPProxyName", "127.0.0.1")).Append('\n');
        sb.Append(Item("/org.openoffice.Inet/Settings", "ooInetHTTPProxyPort", "9")).Append('\n');
        sb.Append(Item("/org.openoffice.Inet/Settings", "ooInetHTTPSProxyName", "127.0.0.1")).Append('\n');
        sb.Append(Item("/org.openoffice.Inet/Settings", "ooInetHTTPSProxyPort", "9")).Append('\n');
        sb.Append("</oor:items>\n");
        File.WriteAllText(Path.Combine(user, "registrymodifications.xcu"), sb.ToString(), new UTF8Encoding(false));
    }
}

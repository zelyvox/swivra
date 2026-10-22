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
internal static class OfficeConvert
{
    internal sealed record Result(string Path, string Engine);

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
        string? work = null;
        try
        {
            // The dropped file is not trusted. Word fetches a document's remote template (and linked
            // content) on open, which can leak the user's NTLM hash to a UNC path; Protected View avoids
            // that but cannot export, and leaving it triggers the fetch. So OOXML and RTF go in as a copy
            // with their external references cut. Legacy .doc and .odt cannot be cleaned: LibreOffice
            // (which does not load Word templates) gets them first, Word only when it is the only engine.
            string safe = input;
            if (OoxmlExts.Contains(inExt) || inExt == ".rtf")
            {
                work = Directory.CreateTempSubdirectory("swivra-word-").FullName;
                safe = Path.Combine(work, Path.GetFileName(input));
                File.Copy(input, safe);
                int cut = inExt == ".rtf" ? CutRtfReferences(safe) : CutExternalReferences(safe);
                if (cut > 0) Log.Write($"office input: {cut} external reference(s) cut before conversion");
            }

            if (inExt is ".doc" or ".odt" && soffice is not null && TryLibreOffice(soffice, safe, target, ext)) return new Result(target, "LibreOffice");
            if (WordInstalled && TryWord(safe, target, ext)) return new Result(target, "Word");
            if (inExt is not (".doc" or ".odt") && soffice is not null && TryLibreOffice(soffice, safe, target, ext)) return new Result(target, "LibreOffice");
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"office conversion failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (work is not null) try { Directory.Delete(work, true); } catch { /* harmless leftovers */ }
        }
    }

    // ---------- untrusted input: cut external references ----------

    private static readonly HashSet<string> OoxmlExts = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm", ".dotx", ".dotm" };

    // A local path that does not exist: Word looks for it on disk and moves on, nothing leaves the machine.
    private const string Blocked = "file:///C:/swivra-blocked-reference";

    // Fields that pull in a file or a live link when Word updates them. QUOTE only prints its argument.
    private static readonly System.Text.RegularExpressions.Regex FetchingField = new(
        @"\b(INCLUDEPICTURE|INCLUDETEXT|IMPORT|LINK|DDEAUTO|DDE)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Points every external relationship except hyperlinks (attached template, linked pictures and
    /// objects, frames, subdocuments, altChunks) at <see cref="Blocked"/>. Ids stay, so the file stays
    /// valid. Hyperlinks are only followed on a click. In the document parts, fields that fetch a file
    /// (w:instrText, w:fldSimple@w:instr) become QUOTE fields, as for RTF. Returns how many were cut.
    /// </summary>
    private static int CutExternalReferences(string path)
    {
        int cut = 0;
        using var zip = ZipFile.Open(path, ZipArchiveMode.Update);
        foreach (var entry in zip.Entries.ToList())
        {
            string name = entry.FullName;
            bool rels = name.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);
            bool story = name.StartsWith("word/", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
            if (!rels && !story) continue;
            XDocument xml;
            try
            {
                using var s = entry.Open();
                xml = XDocument.Load(s, LoadOptions.PreserveWhitespace);
            }
            catch (System.Xml.XmlException) { continue; } // not XML: Word cannot read it either
            int before = cut;
            if (rels)
            {
                foreach (var rel in xml.Descendants().Where(e => e.Name.LocalName == "Relationship"))
                {
                    if ((string?)rel.Attribute("TargetMode") != "External") continue;
                    if (((string?)rel.Attribute("Type"))?.EndsWith("/hyperlink", StringComparison.Ordinal) == true) continue;
                    rel.SetAttributeValue("Target", Blocked);
                    cut++;
                }
            }
            else
            {
                cut += NeutralizeFields(xml);
                foreach (var a in xml.Descendants().Attributes().Where(a => a.Name.LocalName == "instr").ToList())
                {
                    string value = Neutralize(a.Value, ref cut);
                    if (value == a.Value) continue;
                    a.Value = value;
                    KeepResult(a.Parent!);
                }
            }
            if (cut == before) continue;
            using var w = entry.Open();
            w.SetLength(0);
            xml.Save(w, SaveOptions.DisableFormatting);
        }
        return cut;
    }

    private static string Neutralize(string instruction, ref int cut)
    {
        int found = 0;
        string result = FetchingField.Replace(instruction, _ => { found++; return "QUOTE"; });
        cut += found;
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

    private sealed class FieldCode
    {
        public XElement? Begin; // the fldChar that opens the field
        public readonly List<XElement> Pieces = new();
        public readonly System.Text.StringBuilder Head = new(); // instruction text before the first nested field
        public bool Nested, Collecting = true;
    }

    /// <summary>
    /// Word splits one instruction over several w:instrText runs ("INCLUDE" in one, "PICTURE" in the
    /// next), so the pieces of each field (fldChar begin … separate/end, nesting followed) are joined
    /// before the check. A field whose name could come from a nested field cannot be read here and only
    /// prints. A changed instruction goes into its first piece, the others are emptied.
    /// </summary>
    private static int NeutralizeFields(XDocument xml)
    {
        int cut = 0;
        var open = new Stack<FieldCode>();
        foreach (var e in xml.Descendants().ToList())
        {
            string name = e.Name.LocalName;
            if (name == "instrText")
            {
                if (open.Count > 0 && open.Peek().Collecting)
                {
                    var f = open.Peek();
                    f.Pieces.Add(e);
                    if (!f.Nested) f.Head.Append(e.Value);
                }
                else e.Value = Neutralize(e.Value, ref cut); // outside any field: checked on its own
                continue;
            }
            if (name != "fldChar") continue;
            switch ((string?)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "fldCharType"))
            {
                case "begin":
                    if (open.Count > 0 && open.Peek().Collecting) open.Peek().Nested = true;
                    open.Push(new FieldCode { Begin = e });
                    break;
                case "separate" when open.Count > 0 && open.Peek().Collecting:
                    cut += Finish(open.Peek());
                    open.Peek().Collecting = false;
                    break;
                case "end" when open.Count > 0:
                    var done = open.Pop();
                    if (done.Collecting) cut += Finish(done);
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

    private static int Finish(FieldCode f)
    {
        if (f.Pieces.Count == 0) return 0;
        int cut = 0;
        string result = Neutralize(string.Concat(f.Pieces.Select(p => p.Value)), ref cut);
        if (cut == 0 && f.Nested && !NamedField.IsMatch(f.Head.ToString())) { result = Inert; cut = 1; }
        if (cut == 0) return 0;
        f.Pieces[0].Value = result;
        f.Pieces[0].SetAttributeValue(XNamespace.Xml + "space", "preserve");
        foreach (var p in f.Pieces.Skip(1)) p.Value = "";
        if (f.Begin is not null) KeepResult(f.Begin);
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
        if (!unnamed && !FetchingField.IsMatch(RtfText(instruction))) return instruction;
        cut++;
        return @"\fldinst" + Inert;
    }

    /// <summary>
    /// The text Word reads from an RTF fragment: ignorable destinations ({\*...}) and control words
    /// dropped, \'hh and \uN decoded, control symbols kept as their character.
    /// </summary>
    private static string RtfText(string rtf)
    {
        var sb = new System.Text.StringBuilder(rtf.Length);
        for (int j = 0; j < rtf.Length; j++)
        {
            char ch = rtf[j];
            if (ch == '{')
            {
                if (j + 2 < rtf.Length && rtf[j + 1] == '\\' && rtf[j + 2] == '*') j = GroupEnd(rtf, j + 1, 1);
                continue;
            }
            if (ch is '}' or '\r' or '\n') continue;
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
            if (word == "u" && hasNum)
            {
                sb.Append((char)(n < 0 ? n + 65536 : n));
                // the fallback character readers without Unicode show instead
                if (k + 3 < rtf.Length && rtf[k] == '\\' && rtf[k + 1] == '\'') k += 4;
                else if (k < rtf.Length && rtf[k] is not ('\\' or '{' or '}')) k++;
            }
            else if (word is "par" or "line" or "tab" or "cell" or "row" or "sect" or "page") sb.Append(' ');
            j = k - 1;
        }
        return sb.ToString();
    }

    /// <summary>
    /// RTF can name a remote template (<c>{\*\template ...}</c>), pull files in through fields and carry
    /// OLE links that update themselves on open. The template group is removed; every field instruction
    /// that fetches (INCLUDEPICTURE, INCLUDETEXT, IMPORT, LINK, DDE) becomes an inert QUOTE, see
    /// <see cref="NeutralizeRtfField"/>; automatic OLE links become manual ones and lose their
    /// "update before showing" flag. Returns how many were cut.
    /// </summary>
    private static int CutRtfReferences(string path)
    {
        // RTF is 7-bit ASCII with escapes; Latin-1 keeps every byte as one char and back.
        var latin1 = System.Text.Encoding.Latin1;
        string rtf = File.ReadAllText(path, latin1);
        int cut = 0;

        var sb = new System.Text.StringBuilder(rtf.Length);
        int i = 0;
        while (i < rtf.Length)
        {
            int at = rtf.IndexOf(@"{\*\template", i, StringComparison.Ordinal);
            if (at < 0) { sb.Append(rtf, i, rtf.Length - i); break; }
            sb.Append(rtf, i, at - i);
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
            sb.Append(rtf, i, at - i).Append(NeutralizeRtfField(rtf[at..end], ref cut));
            i = end;
        }
        rtf = sb.ToString();

        rtf = System.Text.RegularExpressions.Regex.Replace(rtf, @"\\objupdate(?![a-zA-Z])", _ => { cut++; return ""; });
        rtf = System.Text.RegularExpressions.Regex.Replace(rtf, @"\\objautlink(?![a-zA-Z])", _ => { cut++; return @"\objlink"; });
        if (cut > 0) File.WriteAllText(path, rtf, latin1);
        return cut;
    }

    /// <summary>Index of the brace that closes the group open at <paramref name="from"/> with the given depth (or the end of the text).</summary>
    private static int GroupEnd(string rtf, int from, int depth)
    {
        for (int j = from; j < rtf.Length; j++)
        {
            if (rtf[j] == '\\') { j++; continue; } // escaped brace or control symbol
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
    private static bool TryWord(string input, string target, string ext)
    {
        // An earlier crash or kill may have left this file on Word's list; its prompt would hang us.
        ForgetCrash(input);
        var before = Process.GetProcessesByName("WINWORD").Select(p => p.Id).ToHashSet();
        int ownPid = 0; // the WINWORD that our CreateInstance started, once known
        bool ok = false, isPdf = Path.GetExtension(input).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        // Opening a PDF shows a "Word will now convert your PDF" message that DisplayAlerts does not
        // cover; invisible, it would hang. Word's own switch turns it off. Word reads it at start-up,
        // so it is set before Word starts and put back afterwards.
        Action? restorePdfWarning = isPdf && WordVersion() is string version ? SuppressPdfWarning(version) : null;
        var thread = new Thread(() =>
        {
            dynamic? app = null, doc = null;
            try
            {
                app = Activator.CreateInstance(Type.GetTypeFromProgID("Word.Application")!)!;
                // Right after start-up the only new WINWORD is ours. A Word the user opens later is not.
                var started = Process.GetProcessesByName("WINWORD").Where(p => !before.Contains(p.Id)).ToList();
                if (started.Count == 1) Volatile.Write(ref ownPid, started[0].Id);
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
                try { app?.Quit(WdDoNotSaveChanges); } catch { /* same */ }
                if (doc is not null) Marshal.FinalReleaseComObject(doc);
                if (app is not null) Marshal.FinalReleaseComObject(app);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeoutMs))
        {
            // Only the Word this run started, never the user's own (not even one opened during the wait).
            // If start-up itself hung, ownPid is unknown; then every new WINWORD is from that start-up.
            int pid = Volatile.Read(ref ownPid);
            var victims = pid != 0
                ? Process.GetProcessesByName("WINWORD").Where(p => p.Id == pid)
                : Process.GetProcessesByName("WINWORD").Where(p => !before.Contains(p.Id));
            foreach (var p in victims)
                try { p.Kill(true); } catch { /* already gone */ }
            Log.Write($"word conversion timed out (killed {(pid != 0 ? $"pid {pid}" : "new instances")})");
            ok = false;
            ForgetCrash(input);
        }
        restorePdfWarning?.Invoke();
        if (!ok) TryDelete(target);
        return ok;
    }

    /// <summary>
    /// Killing Word makes it list the file under Resiliency\DisabledItems, and next time Word asks
    /// "this file caused a serious error, open anyway?" in a dialog nobody sees. Remove only the
    /// entries that name this file; the user's other entries stay.
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
                if (key.GetValue(name) is byte[] data && System.Text.Encoding.Unicode.GetString(data).ToLowerInvariant().Contains(needle))
                {
                    key.DeleteValue(name, false);
                    Log.Write("word crash entry for the converted file removed");
                }
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
        const string Name = "DisableConvertPdfWarning";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Office\{version}\Word\Options");
            object? old = key.GetValue(Name);
            key.SetValue(Name, 1, RegistryValueKind.DWord);
            return () =>
            {
                try
                {
                    using var k = Registry.CurrentUser.CreateSubKey($@"Software\Microsoft\Office\{version}\Word\Options");
                    if (old is null) k.DeleteValue(Name, false);
                    else k.SetValue(Name, old, RegistryValueKind.DWord);
                }
                catch (Exception ex) { Log.Write($"word option restore failed: {ex.Message}"); }
            };
        }
        catch (Exception ex)
        {
            Log.Write($"word option set failed: {ex.Message}");
            return null;
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
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}

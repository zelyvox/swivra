using System.Diagnostics;
using System.Runtime.InteropServices;
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
        if (WordInstalled && TryWord(input, target, ext)) return new Result(target, "Word");
        string? soffice = FindLibreOffice();
        if (soffice is not null && TryLibreOffice(soffice, input, target, ext)) return new Result(target, "LibreOffice");
        return null;
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
            // Only the Word processes that appeared for this run, never the user's own.
            foreach (var p in Process.GetProcessesByName("WINWORD").Where(p => !before.Contains(p.Id)))
                try { p.Kill(true); } catch { /* already gone */ }
            Log.Write("word conversion timed out");
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

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Swivra.App;

/// <summary>
/// The Send wheel's WhatsApp and Mail slices.
/// WhatsApp Desktop has no API to attach a file, so the prepared files go on the clipboard as a
/// file drop list and the user presses Ctrl+V in a chat. Mail attaches through Simple MAPI and
/// falls back to the same clipboard route when no MAPI client is configured.
/// </summary>
internal static class SendActions
{
    private const int MaxSide = 1600;
    private const long JpegQuality = 85;
    private const long MailLimit = 20L * 1024 * 1024; // typical attachment cap of web and Exchange mail

    private static string TempRoot => Path.Combine(Path.GetTempPath(), "Swivra", "send");

    public static bool IsSendStep(string step) => step is "wa" or "mail";

    public static ActionResult Run(IReadOnlyList<string> files, IReadOnlyList<string> allSteps)
    {
        // "path" may have joined the chain by dwelling on the way here; the clipboard carries the files instead.
        var steps = allSteps.Where(s => s != "path").ToList();
        string title = steps[0] == "wa" ? "WhatsApp" : "Mail";
        if (steps.Count > 1)
            return new ActionResult(false, title, Lang.T("Gönderme adımı zincirde tek başına olmalı", "A send step must be alone in the chain"));
        var real = files.Where(File.Exists).ToList();
        if (real.Count == 0)
            return new ActionResult(false, title, Lang.T("Klasör gönderilmez, dosya sürükle", "Folders cannot be sent, drag files"));
        try
        {
            return steps[0] == "wa" ? WhatsApp(real) : Mail(real);
        }
        catch (Exception ex)
        {
            Log.Write($"send {steps[0]} failed: {ex}");
            return new ActionResult(false, Lang.T("Olmadı", "Failed"), Lang.T("Dosya hazırlanamadı", "Could not prepare the file"));
        }
    }

    // ---------- WhatsApp ----------

    private static ActionResult WhatsApp(List<string> files)
    {
        // Images are shrunk into a temp copy; everything else is referenced as is (never modified).
        // The README promises location-free photos here, so an image that cannot be re-encoded stays behind.
        var prepared = Prepare(files, shrinkImages: true, out var held, out string? reason);
        if (prepared.Count == 0)
            return new ActionResult(false, Lang.T("Gönderilmedi", "Not sent"), reason ?? Lang.T("Konum bilgisi silinemedi", "Could not remove the location data"));
        bool installed = WhatsAppInstalled();
        string where = installed ? Lang.T("WhatsApp'ta", "in WhatsApp") : Lang.T("WhatsApp Web'de", "in WhatsApp Web");
        string detail = prepared.Count == 1
            ? Lang.T($"Panoda · {where} Ctrl+V", $"On clipboard · Ctrl+V {where}")
            : Lang.T($"{prepared.Count} dosya panoda · {where} Ctrl+V", $"{prepared.Count} files on clipboard · Ctrl+V {where}");
        if (held.Count > 0)
            detail += Lang.T($" · {held.Count} resim konumu silinemediği için eklenmedi", $" · {held.Count} image(s) left out, location could not be removed");
        Log.Write($"wa prepared {prepared.Count} file(s), held back {held.Count}, whatsapp installed={installed}");
        return new ActionResult(true, Lang.T("Hazır", "Ready"), detail, OnUi: () =>
        {
            if (!SetClipboardFiles(prepared)) return false;
            if (installed) OpenWhatsApp();
            return true;
        });
    }

    /// <summary>The Store/desktop app registers the whatsapp: URI scheme; without it there is nothing to open.</summary>
    private static bool WhatsAppInstalled()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey("whatsapp");
            return key?.GetValue("URL Protocol") is not null;
        }
        catch { return false; }
    }

    private static void OpenWhatsApp()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("whatsapp:") { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"whatsapp launch failed: {ex.Message}"); }
    }

    /// <summary>Must run on the STA UI thread.</summary>
    internal static bool SetClipboardFiles(IReadOnlyList<string> paths)
    {
        try
        {
            var list = new System.Collections.Specialized.StringCollection();
            list.AddRange(paths.ToArray());
            Clipboard.SetFileDropList(list);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"clipboard file drop failed: {ex.Message}");
            return false;
        }
    }

    // ---------- Mail ----------

    private static ActionResult Mail(List<string> files)
    {
        // Rule: originals are attached untouched while the total fits a normal mailbox (20 MB).
        // Only above that are images shrunk like for WhatsApp; other files cannot be shrunk.
        long total = files.Sum(f => new FileInfo(f).Length);
        bool shrink = total > MailLimit;
        // Mail makes no location promise (small sets go out untouched), so an image that cannot be shrunk goes as it is.
        var attach = shrink ? Prepare(files, shrinkImages: true, out _, out _, passThroughFailures: true) : files;
        long sent = attach.Sum(f => new FileInfo(f).Length);
        string note = shrink
            ? (sent > MailLimit
                ? Lang.T($" · resimler küçüldü, yine de {FileKinds.HumanSize(sent)}", $" · images shrunk, still {FileKinds.HumanSize(sent)}")
                : Lang.T(" · resimler küçüldü", " · images shrunk"))
            : "";

        string? why = null;
        if (!MapiClientConfigured()) why = "no MAPI client configured";
        else
        {
            int rc = MapiSend(attach);
            if (rc == 0 || rc == MapiUserAbort) return new ActionResult(true, Lang.T("Hazır", "Ready"), Lang.T("Mail istemcisine gönderildi", "Handed to your mail client") + note);
            why = $"MAPISendMail returned {rc}";
        }

        // No usable mail client (typical with web mail): files go on the clipboard like for WhatsApp,
        // and a new Gmail message opens in the browser. No web mail lets a link attach files, so the
        // user pastes or drags them in.
        Log.Write($"mail fallback to clipboard + Gmail: {why}");
        string detail = Lang.T("Gmail açıldı · dosyalar panoda, iletiye Ctrl+V ya da sürükle", "Gmail opened · files on clipboard, Ctrl+V or drag them into the message") + note;
        return new ActionResult(true, Lang.T("Hazır", "Ready"), detail, OnUi: () =>
        {
            if (!SetClipboardFiles(attach)) return false;
            OpenGmailCompose();
            return true;
        });
    }

    private const string GmailCompose = "https://mail.google.com/mail/?view=cm&fs=1";

    private static void OpenGmailCompose()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(GmailCompose) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write($"gmail launch failed: {ex.Message}"); }
    }

    private static bool MapiClientConfigured()
    {
        try
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                using var key = hive.OpenSubKey(@"Software\Clients\Mail");
                if (key?.GetValue(null) is string s && s.Length > 0)
                    return MapiAvailable() && (!s.Contains("Outlook", StringComparison.OrdinalIgnoreCase) || OutlookHasProfile());
            }
        }
        catch { /* fall through */ }
        return false;
    }

    /// <summary>
    /// Classic Outlook without a profile answers MAPI with a modal "create a profile" error and blocks,
    /// which would look like an open compose window; treat it as "no client".
    /// </summary>
    private static bool OutlookHasProfile()
    {
        try
        {
            using var office = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office");
            foreach (string ver in office?.GetSubKeyNames() ?? [])
            {
                using var profiles = office!.OpenSubKey(ver + @"\Outlook\Profiles");
                if (profiles?.SubKeyCount > 0) return true;
            }
            using var legacy = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Windows Messaging Subsystem\Profiles");
            return legacy?.SubKeyCount > 0;
        }
        catch { return false; }
    }

    private static bool MapiAvailable()
    {
        if (!NativeLibrary.TryLoad("mapi32.dll", out var lib)) return false;
        bool ok = NativeLibrary.TryGetExport(lib, "MAPISendMailW", out _);
        NativeLibrary.Free(lib);
        return ok;
    }

    private const int MapiUserAbort = 1;

    /// <summary>
    /// Opens a compose window through Simple MAPI. The call can block until the user sends or
    /// closes the message, so it runs on its own thread; if it is still running after a few
    /// seconds the window is up and we report success. An early error code means no usable client.
    /// </summary>
    private static int MapiSend(IReadOnlyList<string> paths)
    {
        int rc = -1;
        var done = new ManualResetEventSlim();
        var th = new Thread(() =>
        {
            try { rc = MapiSendBlocking(paths); }
            catch (Exception ex) { Log.Write($"mapi failed: {ex.Message}"); rc = -2; }
            finally { done.Set(); }
        }) { IsBackground = true, Name = "swivra-mapi" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        if (!done.Wait(4000)) { Log.Write("mapi: dialog still open after 4 s, treating as shown"); return 0; }
        Log.Write($"mapi returned {rc}");
        return rc;
    }

    private static int MapiSendBlocking(IReadOnlyList<string> paths)
    {
        int size = Marshal.SizeOf<MapiFileDescW>();
        IntPtr block = Marshal.AllocHGlobal(size * paths.Count);
        try
        {
            for (int i = 0; i < paths.Count; i++)
            {
                var d = new MapiFileDescW { PathName = paths[i], FileName = Path.GetFileName(paths[i]) };
                Marshal.StructureToPtr(d, block + i * size, false);
            }
            var msg = new MapiMessageW { FileCount = (uint)paths.Count, Files = block };
            try
            {
                return (int)MAPISendMailW(IntPtr.Zero, IntPtr.Zero, ref msg, MapiLogonUi | MapiDialog, 0);
            }
            finally
            {
                for (int i = 0; i < paths.Count; i++) Marshal.DestroyStructure<MapiFileDescW>(block + i * size);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    private const uint MapiLogonUi = 0x1, MapiDialog = 0x8;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiFileDescW
    {
        public uint Reserved, Flags, Position;
        public string PathName, FileName;
        public IntPtr FileType;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MapiMessageW
    {
        public uint Reserved;
        public string? Subject, NoteText, MessageType, DateReceived, ConversationId;
        public uint Flags;
        public IntPtr Originator;
        public uint RecipCount;
        public IntPtr Recips;
        public uint FileCount;
        public IntPtr Files;
    }

    [DllImport("mapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MAPISendMailW(IntPtr session, IntPtr uiParam, ref MapiMessageW message, uint flags, uint reserved);

    // ---------- file preparation ----------

    /// <summary>
    /// Returns the paths to hand over. With <paramref name="shrinkImages"/>, still images become
    /// a temp JPEG (long side at most 1600 px, quality 85, EXIF orientation applied, all EXIF
    /// incl. location dropped); other files are passed through. Images that cannot be decoded are passed
    /// through only with <paramref name="passThroughFailures"/>; otherwise they land in <paramref name="held"/>
    /// and <paramref name="reason"/> says why (the codec hint for HEIC/AVIF when that is the cause).
    /// </summary>
    private static List<string> Prepare(List<string> files, bool shrinkImages, out List<string> held, out string? reason, bool passThroughFailures = false)
    {
        string? dir = null;
        var result = new List<string>();
        held = [];
        reason = null;
        foreach (string f in files)
        {
            string ext = Path.GetExtension(f);
            // GIFs would lose their animation; the rest of the list is what GDI+ may fail to read.
            if (!shrinkImages || !FileKinds.IsImage(ext) || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)) { result.Add(f); continue; }
            try
            {
                dir ??= Directory.CreateDirectory(Path.Combine(TempRoot, Guid.NewGuid().ToString("N"))).FullName;
                result.Add(ShrinkImage(f, dir));
            }
            catch (Exception ex)
            {
                if (passThroughFailures)
                {
                    Log.Write($"could not shrink {f}, passing it through: {ex.Message}");
                    result.Add(f);
                    continue;
                }
                Log.Write($"could not shrink {f}, holding it back: {ex.Message}");
                held.Add(f);
                // The codec hint tells the user what to install, so it wins over the generic reason.
                if (ex is MissingCodecException) reason = ex.Message;
                else reason ??= Lang.T("Konum bilgisi silinemedi", "Could not remove the location data");
            }
        }
        return result;
    }

    private static string ShrinkImage(string path, string dir)
    {
        using var src = FileActions.LoadOriented(path, out _);
        double k = Math.Min(1.0, MaxSide / (double)Math.Max(src.Width, src.Height));
        int w = Math.Max(1, (int)Math.Round(src.Width * k)), h = Math.Max(1, (int)Math.Round(src.Height * k));
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb); // a new bitmap carries no EXIF/GPS
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White); // JPEG has no alpha
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var wrap = new ImageAttributes();
            wrap.SetWrapMode(WrapMode.TileFlipXY); // no dark rim along the edges
            g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, wrap);
        }
        string target = FileActions.UniquePath(dir, Path.GetFileNameWithoutExtension(path), "jpg");
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ps = new EncoderParameters(1);
        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);
        bmp.Save(target, codec, ps);
        return target;
    }

    /// <summary>Removes temp copies: everything on exit, anything older than an hour after a crash.</summary>
    public static void CleanTemp(bool all)
    {
        try
        {
            if (!Directory.Exists(TempRoot)) return;
            foreach (string d in Directory.GetDirectories(TempRoot))
            {
                if (!all && DateTime.UtcNow - Directory.GetCreationTimeUtc(d) < TimeSpan.FromHours(1)) continue;
                try { Directory.Delete(d, true); } catch { /* still in use: next time */ }
            }
        }
        catch (Exception ex) { Log.Write($"temp cleanup failed: {ex.Message}"); }
    }
}

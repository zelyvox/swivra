using System.Drawing.Imaging;
using System.Text.Json;

namespace Swivra.App;

/// <summary>
/// Wires the pieces together: hook summons the wheel, the catcher feeds drag events to the
/// web UI, the web UI picks steps, FileActions runs them.
/// </summary>
internal sealed class SwivraContext : ApplicationContext
{
    private readonly OverlayForm _overlay = new();
    private readonly CatcherForm _catcher = new();
    private readonly InputHook _hook = new();
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = 350 };
    private readonly string[] _args;

    private bool _visible;
    private bool _dropping;
    private bool _panel;
    private string[] _files = [];

    public SwivraContext(string[] args)
    {
        _args = args;

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            ContextMenuStrip = new ContextMenuStrip(),
            Visible = true,
        };
        BuildTrayMenu();

        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!_dropping) CloseWheel();
        };

        _hook.ModifierDuringDrag += (mode, pt) => _overlay.BeginInvoke(() => OnModifier(mode, pt));
        _hook.LeftButtonUp += () => _overlay.BeginInvoke(OnLeftUp);
        _hook.KeyPressed += vk => _overlay.BeginInvoke(() => OnKey(vk));

        _catcher.DragEnter += OnDragEnter;
        _catcher.DragOver += OnDragOver;
        _catcher.DragLeave += (_, _) => _overlay.Post(new { type = "leave" });
        _catcher.DragDrop += OnDragDrop;

        _overlay.Message += OnWebMessage;
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            await _overlay.InitAsync();
            _overlay.Post(new { type = "lang", lang = Lang.Current });
            _hook.Install();
            int demo = Array.IndexOf(_args, "--demo");
            if (demo >= 0)
            {
                int shot = Array.IndexOf(_args, "--shot");
                RunDemo(shot >= 0 && shot + 1 < _args.Length ? _args[shot + 1] : null);
            }
            int demoTool = Array.IndexOf(_args, "--demo-tool");
            if (demoTool >= 0 && demoTool + 2 < _args.Length)
            {
                int shot = Array.IndexOf(_args, "--shot");
                RunToolDemo(_args[demoTool + 1], _args[demoTool + 2], shot >= 0 && shot + 1 < _args.Length ? _args[shot + 1] : null);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"startup failed: {ex}");
            MessageBox.Show("Swivra başlatılamadı. Ayrıntı kayıt dosyasında.", "Swivra");
            ExitThread();
        }
    }

    /// <summary>Tray menu and tooltip in the current language; rebuilt when the language changes.</summary>
    private void BuildTrayMenu()
    {
        _tray.Text = Lang.T("Swivra: dosyayı sürüklerken Shift, Ctrl ya da Alt'a bas", "Swivra: press Shift, Ctrl or Alt while dragging a file");
        var menu = _tray.ContextMenuStrip!;
        menu.Items.Clear();
        menu.Items.Add(Lang.T("Çarkı göster (demo)", "Show the wheel (demo)"), null, (_, _) => RunDemo(null));
        var lang = new ToolStripMenuItem("Dil / Language");
        lang.DropDownItems.Add(new ToolStripMenuItem("Türkçe", null, (_, _) => SetLanguage("tr")) { Checked = Lang.IsTurkish });
        lang.DropDownItems.Add(new ToolStripMenuItem("English", null, (_, _) => SetLanguage("en")) { Checked = !Lang.IsTurkish });
        menu.Items.Add(lang);
        menu.Items.Add(Lang.T("Kayıtları aç", "Open logs"), null, (_, _) => OpenLogFolder());
        menu.Items.Add(Lang.T("Çıkış", "Exit"), null, (_, _) => ExitThread());
    }

    private void SetLanguage(string lang)
    {
        Lang.Set(lang);
        Log.Write($"language {Lang.Current}");
        BuildTrayMenu();
        _overlay.Post(new { type = "lang", lang = Lang.Current });
    }

    private static string ModeName(WheelMode m) => m switch
    {
        WheelMode.Tools => "tools",
        WheelMode.Send => "send",
        _ => "convert",
    };

    private void OnModifier(WheelMode mode, Point pt)
    {
        if (!_overlay.Ready) return;
        if (_visible)
        {
            _overlay.Post(new { type = "mode", mode = ModeName(mode) });
            return;
        }
        if (IsShellDrag())
        {
            Summon(mode);
            return;
        }
        // Explorer can create its drag image a beat after the drag starts; look once more.
        After(120, () =>
        {
            if (!_visible && _hook.IsDragging && IsShellDrag()) Summon(mode);
            else Log.Write($"ignored mode={mode}: no file drag in progress");
        });
    }

    /// <summary>
    /// Explorer shows a "SysDragImage" window while files are dragged. Text selection, window
    /// moves and other plain mouse drags have none, so Shift-selecting text no longer opens the wheel.
    /// </summary>
    private static bool IsShellDrag() => Native.IsWindowVisible(Native.FindWindow("SysDragImage", null));

    private void Summon(WheelMode mode)
    {
        Point pt = Cursor.Position;
        Log.Write($"summon mode={mode} at={pt}");
        ShowWheel(mode, pt);
    }

    private void ShowWheel(WheelMode mode, Point pt)
    {
        _hideTimer.Stop();
        _dropping = false;
        _files = [];
        _overlay.ShowAt(pt);
        _catcher.Cover(_overlay.Bounds);
        _catcher.Show();
        _visible = true;
        _overlay.Post(new { type = "open", mode = ModeName(mode) });
    }

    private void OnLeftUp()
    {
        if (_panel)
        {
            // The panel is clicked like any window; a click outside it means "never mind".
            if (!_overlay.Bounds.Contains(Cursor.Position)) CancelWheel();
            return;
        }
        if (_visible) _hideTimer.Start();
    }

    /// <summary>The panel never takes focus (it must not steal it from Explorer), so its keys come from the hook.</summary>
    private void OnKey(uint vk)
    {
        if (!_panel) return;
        if (vk == 0x1B) _overlay.Post(new { type = "key", key = "esc" });
        else if (vk == 0x0D) _overlay.Post(new { type = "key", key = "enter" });
    }

    private void CancelWheel()
    {
        _dropping = false;
        CloseWheel();
    }

    private void CloseWheel()
    {
        if (!_visible) return;
        _visible = false;
        _panel = false;
        _overlay.DropFocus();
        _overlay.Post(new { type = "close" });
        _catcher.Hide();
        var t = new System.Windows.Forms.Timer { Interval = 180 };
        t.Tick += (_, _) =>
        {
            t.Dispose();
            if (!_visible) _overlay.Hide();
        };
        t.Start();
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = DragDropEffects.Copy; // never Move: the original must stay where it is
        _files = e.Data?.GetData(DataFormats.FileDrop) as string[] ?? [];
        var infos = _files.Take(1).Select(p =>
        {
            string ext = Path.GetExtension(p);
            long size = File.Exists(p) ? new FileInfo(p).Length : 0;
            return new
            {
                name = Path.GetFileNameWithoutExtension(p),
                ext = ext.TrimStart('.').ToLowerInvariant(),
                kind = FileKinds.KindOf(ext),
                size = FileKinds.HumanSize(size),
            };
        }).ToArray();
        _overlay.Post(new { type = "enter", files = infos, count = _files.Length });

        if (_files.Length > 0)
        {
            string first = _files[0];
            Task.Run(() => Radar.Scan(first)).ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully && _files.Length > 0 && _files[0] == first)
                    _overlay.BeginInvoke(() => _overlay.Post(new { type = "radar", sens = t.Result }));
            });
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.Effect = DragDropEffects.Copy;
        var css = _overlay.ToCss(new Point(e.X, e.Y));
        _overlay.Post(new { type = "over", x = css.X, y = css.Y });
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        e.Effect = DragDropEffects.Copy;
        _dropping = true;
        _hideTimer.Stop();
        _catcher.Hide();
        _overlay.Post(new { type = "drop" });
    }

    private void OnWebMessage(JsonElement msg)
    {
        string type = msg.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
        switch (type)
        {
            case "run":
                var steps = msg.GetProperty("steps").EnumerateArray().Select(s => s.GetString() ?? "").ToList();
                RunSteps(steps, ReadOptions(msg));
                break;
            case "tool":
                string? tool = msg.TryGetProperty("tool", out var tl) ? tl.GetString() : null;
                if (tool == "crop") StartCrop();
                else if (tool == "lock") StartLock();
                break;
            case "panel-size":
                _panel = true;
                var origin = _overlay.ShowPanel(msg.GetProperty("w").GetInt32(), msg.GetProperty("h").GetInt32());
                bool focused = msg.TryGetProperty("focus", out var fc) && fc.GetBoolean() && _overlay.TakeFocus();
                _overlay.Post(new { type = "panel-open", ox = origin.X, oy = origin.Y, focused });
                break;
            case "cancel":
                CancelWheel();
                break;
            case "log":
                Log.Write("web: " + (msg.TryGetProperty("text", out var x) ? x.GetString() : ""));
                break;
        }
    }

    private static StepOptions ReadOptions(JsonElement msg)
    {
        Rectangle? crop = msg.TryGetProperty("crop", out var c)
            ? new Rectangle(c.GetProperty("x").GetInt32(), c.GetProperty("y").GetInt32(), c.GetProperty("w").GetInt32(), c.GetProperty("h").GetInt32())
            : null;
        string? password = msg.TryGetProperty("password", out var pw) ? pw.GetString() : null; // never logged
        return new StepOptions(crop, password);
    }

    /// <summary>Asks the web UI for the password panel; the PDFs are locked when it answers with "run".</summary>
    private void StartLock()
    {
        if (_files.Length == 0 || _files.Any(f => FileKinds.KindOf(Path.GetExtension(f)) != "pdf"))
        {
            _overlay.Post(new { type = "result", ok = false, title = "Şifrele", detail = "Yalnız PDF şifrelenir" });
            FinishSoon();
            return;
        }
        _overlay.Post(new { type = "lock-form", name = Path.GetFileName(_files[0]), count = _files.Length });
    }

    /// <summary>
    /// Sends the web UI an upright preview of the dragged image and its real size; the web
    /// lays out the crop panel and answers with "panel-size", which grows the window.
    /// </summary>
    private void StartCrop()
    {
        if (_files.Length != 1 || !FileKinds.IsImage(Path.GetExtension(_files[0])))
        {
            _overlay.Post(new { type = "result", ok = false, title = "Kırp", detail = _files.Length > 1 ? "Tek resim kırpılır" : "Yalnız resimler kırpılır" });
            FinishSoon();
            return;
        }
        string file = _files[0];
        Task.Run(() => CropPreview(file)).ContinueWith(t => _overlay.BeginInvoke(() =>
        {
            if (!t.IsCompletedSuccessfully)
            {
                Log.Write($"crop preview failed {file}: {t.Exception?.InnerException}");
                _overlay.Post(new { type = "result", ok = false, title = "Olmadı", detail = "Resim açılamadı" });
                FinishSoon();
                return;
            }
            var (src, w, h) = t.Result;
            _overlay.Post(new { type = "crop-src", name = Path.GetFileName(file), src, w, h });
        }));
    }

    private static (string Src, int Width, int Height) CropPreview(string path)
    {
        const int MaxSide = 1400;
        using var full = FileActions.LoadOriented(path, out _);
        double k = Math.Min(1.0, MaxSide / (double)Math.Max(full.Width, full.Height));
        using var small = new Bitmap(full, Math.Max(1, (int)(full.Width * k)), Math.Max(1, (int)(full.Height * k)));
        using var ms = new MemoryStream();
        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var ps = new EncoderParameters(1);
        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        small.Save(ms, jpeg, ps);
        return ("data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray()), full.Width, full.Height);
    }

    private void RunSteps(List<string> steps, StepOptions options)
    {
        if (_files.Length == 0)
        {
            _overlay.Post(new { type = "result", ok = false, title = "Dosya yok", detail = "Yalnız dosyalar işlenir" });
            FinishSoon();
            return;
        }
        string[] files = _files;
        Log.Write($"run {string.Join(">", steps)} on {string.Join(" | ", files)}");
        Task.Run(() => FileActions.Run(files, steps, options)).ContinueWith(t =>
        {
            _overlay.BeginInvoke(() =>
            {
                var r = t.IsCompletedSuccessfully ? t.Result : new ActionResult(false, "Olmadı", "Beklenmeyen hata");
                if (r.ClipboardText is not null) Clipboard.SetText(r.ClipboardText);
                _overlay.Post(new { type = "result", ok = r.Ok, title = r.Title, detail = r.Detail, ext = r.OutputExt });
                FinishSoon();
            });
        });
    }

    private void FinishSoon()
    {
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (_, _) =>
        {
            t.Dispose();
            _dropping = false;
            CloseWheel();
        };
        t.Start();
    }

    /// <summary>Shows the wheel at screen center with a fake file, for checking the look.</summary>
    private void RunDemo(string? shotPath)
    {
        Rectangle area = Screen.PrimaryScreen!.WorkingArea;
        var center = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
        Log.Write($"demo at {center} shot={shotPath ?? "-"}");
        ShowWheel(WheelMode.Tools, center);
        _overlay.Post(new { type = "enter", files = new[] { new { name = "Tatil", ext = "jpg", kind = "img", size = "5,1 MB" } }, count = 1 });
        _overlay.Post(new { type = "radar", sens = new[] { "Konum bilgisi", "Cihaz modeli" } });

        After(900, () => _overlay.Post(new { type = "over", x = 170 + 104, y = 170 - 56 }));
        After(2400, () =>
        {
            if (shotPath is null) return;
            var b = _overlay.Bounds;
            b.Inflate(60, 60);
            using var bmp = new Bitmap(b.Width, b.Height);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(b.Location, Point.Empty, b.Size, CopyPixelOperation.SourceCopy);
            bmp.Save(shotPath, ImageFormat.Png);
            Log.Write($"demo shot saved {shotPath}");
        });
        After(4200, () =>
        {
            CloseWheel();
            if (shotPath is not null) After(400, ExitThread);
        });
    }

    /// <summary>Opens a tool panel (crop or lock) on a real file at screen center, for checking it without a drag.</summary>
    private void RunToolDemo(string tool, string file, string? shotPath)
    {
        Rectangle area = Screen.PrimaryScreen!.WorkingArea;
        var center = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
        Log.Write($"tool demo {tool} {file} shot={shotPath ?? "-"}");
        ShowWheel(WheelMode.Tools, center);
        _files = [Path.GetFullPath(file)];
        _dropping = true;
        After(500, tool == "lock" ? StartLock : StartCrop);
        if (shotPath is null) return;
        After(1800, () =>
        {
            var b = _overlay.Bounds;
            b.Inflate(40, 40);
            using var bmp = new Bitmap(b.Width, b.Height);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(b.Location, Point.Empty, b.Size, CopyPixelOperation.SourceCopy);
            bmp.Save(shotPath, ImageFormat.Png);
            Log.Write($"tool demo shot saved {shotPath}");
            CancelWheel();
            After(400, ExitThread);
        });
    }

    private static void After(int ms, Action action)
    {
        var t = new System.Windows.Forms.Timer { Interval = ms };
        t.Tick += (_, _) => { t.Dispose(); action(); };
        t.Start();
    }

    private static void OpenLogFolder()
    {
        Directory.CreateDirectory(Log.Dir);
        System.Diagnostics.Process.Start("explorer.exe", Log.Dir);
    }

    protected override void ExitThreadCore()
    {
        _hook.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _catcher.Dispose();
        _overlay.Dispose();
        base.ExitThreadCore();
    }
}

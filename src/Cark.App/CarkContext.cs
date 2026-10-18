using System.Drawing.Imaging;
using System.Text.Json;

namespace Cark.App;

/// <summary>
/// Wires the pieces together: hook summons the wheel, the catcher feeds drag events to the
/// web UI, the web UI picks steps, FileActions runs them.
/// </summary>
internal sealed partial class CarkContext : ApplicationContext
{
    private readonly OverlayForm _overlay = new();
    private readonly CatcherForm _catcher = new();
    private readonly InputHook _hook = new();
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = 350 };
    private readonly string[] _args;

    private bool _visible;
    private bool _dropping;
    private string[] _files = [];

    public CarkContext(string[] args)
    {
        _args = args;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Çarkı göster (demo)", null, (_, _) => RunDemo(null));
        menu.Items.Add("Kayıtları aç", null, (_, _) => OpenLogFolder());
        menu.Items.Add("Çıkış", null, (_, _) => ExitThread());
        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = "Çark: dosyayı sürüklerken Shift, Ctrl ya da Alt'a bas",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!_dropping) CloseWheel();
        };

        _hook.ModifierDuringDrag += (mode, pt) => _overlay.BeginInvoke(() => OnModifier(mode, pt));
        _hook.LeftButtonUp += () => _overlay.BeginInvoke(OnLeftUp);

        _catcher.DragEnter += OnDragEnter;
        _catcher.DragOver += OnDragOver;
        _catcher.DragLeave += (_, _) => _overlay.Post(new { type = "leave" });
        _catcher.DragDrop += OnDragDrop;

        _overlay.Message += OnWebMessage;
        InitClickMode();
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            await _overlay.InitAsync();
            _hook.Install();
            FlushOpenWhenReady();
            int demo = Array.IndexOf(_args, "--demo");
            if (demo >= 0)
            {
                int shot = Array.IndexOf(_args, "--shot");
                RunDemo(shot >= 0 && shot + 1 < _args.Length ? _args[shot + 1] : null);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"startup failed: {ex}");
            MessageBox.Show("Çark başlatılamadı. Ayrıntı kayıt dosyasında.", "Çark");
            ExitThread();
        }
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

    private void ShowWheel(WheelMode mode, Point pt, bool click = false)
    {
        _hideTimer.Stop();
        _dropping = false;
        _files = [];
        EndClickMode();
        _clickMode = click;
        _overlay.ShowAt(pt);
        _catcher.Cover(_overlay.Bounds);
        _catcher.Show();
        _visible = true;
        _overlay.Post(new { type = "open", mode = ModeName(mode), inputMode = click ? "click" : "drag" });
    }

    private void OnLeftUp()
    {
        if (_visible && !_clickMode) _hideTimer.Start();
    }

    private void CloseWheel()
    {
        if (!_visible) return;
        _visible = false;
        EndClickMode();
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
        SetFiles(e.Data?.GetData(DataFormats.FileDrop) as string[] ?? []);
    }

    private void SetFiles(string[] files)
    {
        _files = files;
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
        Drop();
    }

    private void Drop()
    {
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
                RunSteps(steps);
                break;
            case "cancel":
                _dropping = false;
                CloseWheel();
                break;
            case "log":
                Log.Write("web: " + (msg.TryGetProperty("text", out var x) ? x.GetString() : ""));
                break;
        }
    }

    private void RunSteps(List<string> steps)
    {
        if (_files.Length == 0)
        {
            _overlay.Post(new { type = "result", ok = false, title = "Dosya yok", detail = "Yalnız dosyalar işlenir" });
            FinishSoon();
            return;
        }
        string[] files = _files;
        Log.Write($"run {string.Join(">", steps)} on {string.Join(" | ", files)}");
        Task.Run(() => FileActions.Run(files, steps)).ContinueWith(t =>
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
        _openPipe?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _catcher.Dispose();
        _overlay.Dispose();
        base.ExitThreadCore();
    }
}

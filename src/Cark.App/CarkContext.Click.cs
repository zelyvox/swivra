namespace Cark.App;

/// <summary>
/// Click mode: "Çark ile aç" from the Explorer context menu opens the wheel at the cursor
/// with the files already loaded, and a click on a slice does what a drop does in drag mode.
/// </summary>
internal sealed partial class CarkContext
{
    private const int BatchMs = 250;   // Explorer may start one process per selected file
    private const int ClickArmMs = 250; // the left-up that closed the context menu is not a slice click

    private readonly Settings _settings = Settings.Load();
    private readonly List<string> _pendingOpen = new();
    private readonly System.Windows.Forms.Timer _openTimer = new() { Interval = BatchMs };
    private OpenPipe? _openPipe;
    private ToolStripMenuItem? _shellItem;

    private bool _clickMode;
    private long _clickArmAt;

    private void InitClickMode()
    {
        if (_settings.ShellMenu) ShellMenu.EnsureRegistered();
        _shellItem = new ToolStripMenuItem("Sağ tık menüsü") { Checked = ShellMenu.IsRegistered };
        _shellItem.Click += (_, _) => _shellItem.Checked = ShellMenu.SetEnabled(_settings, !_shellItem.Checked);
        var menu = _tray.ContextMenuStrip!;
        menu.Items.Insert(menu.Items.Count - 1, _shellItem);

        _openTimer.Tick += (_, _) => FlushOpen();
        _openPipe = new OpenPipe(SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext(), QueueOpen);
        var startup = OpenPipe.Validate(OpenPipe.PathsFromArgs(_args));
        if (startup.Length > 0) QueueOpen(startup);

        _hook.KeyDown += vk => _overlay.BeginInvoke(() => OnClickKey(vk));
        _hook.ButtonDown += pt => _overlay.BeginInvoke(() => OnButtonDown(pt));
        _catcher.MouseMove += OnCatcherMove;
        _catcher.MouseLeave += (_, _) => { if (_clickMode && !_dropping) _overlay.Post(new { type = "leave" }); };
        _catcher.MouseUp += OnCatcherUp;
    }

    private void QueueOpen(string[] files)
    {
        foreach (string f in files)
        {
            if (_pendingOpen.Count >= OpenPipe.MaxFiles) break;
            if (!_pendingOpen.Contains(f, StringComparer.OrdinalIgnoreCase)) _pendingOpen.Add(f);
        }
        _openTimer.Stop();
        _openTimer.Start();
    }

    /// <summary>Called once WebView2 is up: files that arrived during startup open now.</summary>
    private void FlushOpenWhenReady()
    {
        if (_pendingOpen.Count == 0) return;
        _openTimer.Stop();
        _openTimer.Start();
    }

    private void FlushOpen()
    {
        _openTimer.Stop();
        if (!_overlay.Ready || _pendingOpen.Count == 0) return;
        if (_visible && (!_clickMode || _dropping))
        {
            Log.Write($"open ignored: wheel busy ({_pendingOpen.Count} file(s))");
            _pendingOpen.Clear();
            return;
        }
        string[] files = _pendingOpen.ToArray();
        _pendingOpen.Clear();
        OpenForClick(files);
    }

    private void OpenForClick(string[] files)
    {
        Point pt = Cursor.Position;
        Log.Write($"open click mode at={pt} files={files.Length}");
        ShowWheel(WheelMode.Convert, pt, click: true);
        _clickArmAt = Environment.TickCount64 + ClickArmMs;
        _hook.SwallowClickKeys = true;
        SetFiles(files);
    }

    private void EndClickMode()
    {
        _clickMode = false;
        _hook.SwallowClickKeys = false;
    }

    private bool InsideWheel(Point screen)
    {
        Rectangle b = _overlay.Bounds;
        double r = b.Width / 2.0, dx = screen.X - (b.Left + r), dy = screen.Y - (b.Top + r);
        return dx * dx + dy * dy <= r * r;
    }

    private void OnClickKey(uint vk)
    {
        if (!_visible || !_clickMode || _dropping) return;
        if (vk == 0x1B)
        {
            Log.Write("click wheel closed: escape");
            CloseWheel();
            return;
        }
        WheelMode? mode = vk switch
        {
            0xA0 or 0xA1 or 0x10 => WheelMode.Convert,
            0xA2 or 0xA3 or 0x11 => WheelMode.Tools,
            0xA4 or 0xA5 or 0x12 => WheelMode.Send,
            _ => null
        };
        if (mode is not null) _overlay.Post(new { type = "mode", mode = ModeName(mode.Value) });
    }

    private void OnButtonDown(Point pt)
    {
        if (!_visible || !_clickMode || _dropping || InsideWheel(pt)) return;
        Log.Write($"click wheel closed: button down outside at {pt}");
        CloseWheel();
    }

    private void OnCatcherMove(object? sender, MouseEventArgs e)
    {
        if (!_clickMode || _dropping) return;
        var css = _overlay.ToCss(Cursor.Position);
        _overlay.Post(new { type = "over", x = css.X, y = css.Y });
    }

    private void OnCatcherUp(object? sender, MouseEventArgs e)
    {
        if (!_clickMode || !_visible || _dropping || e.Button != MouseButtons.Left) return;
        if (Environment.TickCount64 < _clickArmAt)
        {
            Log.Write("click ignored: too soon after open");
            return;
        }
        Point pt = Cursor.Position;
        if (!InsideWheel(pt)) return;
        var css = _overlay.ToCss(pt);
        _overlay.Post(new { type = "over", x = css.X, y = css.Y });
        _hook.SwallowClickKeys = false;
        Log.Write($"click at={pt}");
        Drop();
    }
}

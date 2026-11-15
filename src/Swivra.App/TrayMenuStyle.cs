using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Swivra.App;

/// <summary>
/// The tray menu's look: black, rounded, a soft highlight under the pointer and a short fade when it
/// opens. The stock menu is a light grey box that belongs to no theme and sits badly next to the wheel.
/// </summary>
internal static class TrayMenuStyle
{
    private static readonly Color Back = Color.FromArgb(10, 10, 12);
    private static readonly Color Hover = Color.FromArgb(40, 40, 46);
    private static readonly Color Ink = Color.FromArgb(238, 238, 242);
    private static readonly Color Dim = Color.FromArgb(120, 120, 128);
    private static readonly Color Line = Color.FromArgb(46, 46, 52);
    private static readonly Font MenuFont = new("Segoe UI", 10f);
    private static readonly DarkRenderer Look = new();

    private const int OpenMs = 190;
    private const int SlidePx = 10; // at 100% scale

    // Windows 10: no round corners and no border from the window manager, so the menu draws its own edge.
    private static bool _square;

    /// <summary>Styles one menu window. A submenu is its own window and needs its own call.</summary>
    public static void Apply(ToolStripDropDown menu)
    {
        menu.Renderer = Look;
        menu.BackColor = Back;
        menu.ForeColor = Ink;
        menu.Font = MenuFont;
        menu.DropShadowEnabled = false; // the classic shadow is square and would show behind the round corners
        if (menu is ToolStripDropDownMenu m)
        {
            m.ShowImageMargin = false;
            m.ShowCheckMargin = true;
        }
        foreach (ToolStripItem item in menu.Items) Roomy(item, menu);
        menu.ItemAdded += (_, e) => Roomy(e.Item, menu);
        menu.HandleCreated += (_, _) => Round(menu.Handle);

        // Opening: the menu fades in while it rises the last few pixels into place (it opens above the tray).
        // A submenu only fades: WinForms keeps it glued to its row.
        var open = new System.Windows.Forms.Timer { Interval = 8 };
        var clock = new System.Diagnostics.Stopwatch();
        Point rest = default, from = default;
        menu.Opening += (_, e) => { if (!e.Cancel && SystemInformation.UIEffectsEnabled) menu.Opacity = 0; };
        menu.Opened += (_, _) =>
        {
            if (!SystemInformation.UIEffectsEnabled) { menu.Opacity = 1; return; } // effects went off since Opening: never stay see-through
            rest = menu.Location;
            from = menu.OwnerItem is null ? new Point(rest.X, rest.Y + Scale(SlidePx, menu)) : rest;
            if (from != rest) menu.Location = from;
            clock.Restart();
            open.Start();
        };
        open.Tick += (_, _) =>
        {
            double t = Math.Min(1.0, clock.Elapsed.TotalMilliseconds / OpenMs);
            double eased = 1 - Math.Pow(1 - t, 4); // fast start, long soft landing
            menu.Opacity = Math.Min(1.0, eased * 1.15);
            if (from != rest) menu.Location = new Point(rest.X, from.Y + (int)Math.Round((rest.Y - from.Y) * eased));
            if (t >= 1) open.Stop();
        };
        // Whatever stopped it early, the menu must never stay see-through.
        menu.Closed += (_, _) => { open.Stop(); menu.Opacity = 1; };
        menu.Disposed += (_, _) => open.Dispose();
    }

    /// <summary>Pixels at 100% scale, for the screen the menu is on.</summary>
    private static int Scale(int px, Control on) => (int)Math.Round(px * on.DeviceDpi / 96.0);

    private static void Roomy(ToolStripItem? item, ToolStripDropDown menu)
    {
        if (item is ToolStripMenuItem) item.Padding = new Padding(Scale(2, menu), Scale(5, menu), Scale(2, menu), Scale(5, menu));
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;

    /// <summary>Round corners and a quiet border from the window manager. Windows 10 has neither and keeps square corners.</summary>
    private static void Round(IntPtr handle)
    {
        try
        {
            int round = DWMWCP_ROUND;
            if (DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)) != 0) _square = true;
            int border = Line.R | Line.G << 8 | Line.B << 16; // COLORREF is 0x00BBGGRR
            DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Write($"tray menu corners: {ex.Message}");
        }
    }

    [DllImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) => RoundedEdges = false;

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Back);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            if (!_square) return; // the window manager draws it
            using var pen = new Pen(Line);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            var box = new Rectangle(Point.Empty, e.Item.Size);
            using (var back = new SolidBrush(Back)) g.FillRectangle(back, box);
            if (!e.Item.Selected || !e.Item.Enabled) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            box.Inflate(-1, 0);
            using var path = Pill(box, e.ToolStrip is { } strip ? Scale(6, strip) : 6);
            using var hover = new SolidBrush(Hover);
            g.FillPath(hover, path);
            g.SmoothingMode = SmoothingMode.Default;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Ink : Dim;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item is { Enabled: false } ? Dim : Ink;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using var pen = new Pen(Line);
            int inset = e.ToolStrip is { } strip ? Scale(8, strip) : 8;
            e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset, y);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            if (r.Width <= 0 || r.Height <= 0) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(r.Width, r.Height), cx = r.Left + r.Width / 2f, cy = r.Top + r.Height / 2f;
            using var pen = new Pen(e.Item is { Enabled: false } ? Dim : Ink, Math.Max(1.6f, s / 9f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen,
            [
                new PointF(cx - s * 0.28f, cy + s * 0.02f),
                new PointF(cx - s * 0.08f, cy + s * 0.22f),
                new PointF(cx + s * 0.30f, cy - s * 0.20f),
            ]);
            g.SmoothingMode = SmoothingMode.Default;
        }

        private static GraphicsPath Pill(Rectangle r, int radius)
        {
            int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /// <summary>Colours for the few parts the base renderer still paints by itself (submenu frame, pressed states).</summary>
    private sealed class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Back;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Line;
        public override Color CheckBackground => Back;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }
}

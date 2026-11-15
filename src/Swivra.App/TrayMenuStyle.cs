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

    private const int FadeMs = 140;

    /// <summary>Styles one menu window. A submenu is its own window and needs its own call.</summary>
    public static void Apply(ToolStripDropDown menu)
    {
        menu.Renderer = Look;
        menu.BackColor = Back;
        menu.ForeColor = Ink;
        menu.Font = MenuFont;
        menu.DropShadowEnabled = false; // the classic shadow is square and would show behind the round corners
        menu.Padding = new Padding(5, 6, 5, 6);
        if (menu is ToolStripDropDownMenu m)
        {
            m.ShowImageMargin = false;
            m.ShowCheckMargin = true;
        }
        foreach (ToolStripItem item in menu.Items) Roomy(item);
        menu.ItemAdded += (_, e) => Roomy(e.Item);
        menu.HandleCreated += (_, _) => Round(menu.Handle);

        var fade = new System.Windows.Forms.Timer { Interval = 15 };
        long began = 0;
        menu.Opening += (_, e) => { if (!e.Cancel && SystemInformation.UIEffectsEnabled) menu.Opacity = 0; };
        menu.Opened += (_, _) =>
        {
            if (!SystemInformation.UIEffectsEnabled) return;
            began = Environment.TickCount64;
            fade.Start();
        };
        fade.Tick += (_, _) =>
        {
            double t = Math.Min(1.0, (Environment.TickCount64 - began) / (double)FadeMs);
            menu.Opacity = 1 - Math.Pow(1 - t, 3); // ease out
            if (t >= 1) fade.Stop();
        };
        // Whatever stopped the fade early, the menu must never stay see-through.
        menu.Closed += (_, _) => { fade.Stop(); menu.Opacity = 1; };
        menu.Disposed += (_, _) => fade.Dispose();
    }

    private static void Roomy(ToolStripItem? item)
    {
        if (item is ToolStripMenuItem) item.Padding = new Padding(2, 5, 2, 5);
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWCP_ROUND = 2;

    /// <summary>Round corners and a quiet border from the window manager. Windows 10 has neither and keeps square corners.</summary>
    private static void Round(IntPtr handle)
    {
        try
        {
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int border = Line.R | Line.G << 8 | Line.B << 16; // COLORREF is 0x00BBGGRR
            DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Write($"tray menu corners: {ex.Message}");
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) => RoundedEdges = false;

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Back);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { } // the window manager draws it

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            var box = new Rectangle(Point.Empty, e.Item.Size);
            using (var back = new SolidBrush(Back)) g.FillRectangle(back, box);
            if (!e.Item.Selected || !e.Item.Enabled) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            box.Inflate(-1, 0);
            using var path = Pill(box, 6);
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
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
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

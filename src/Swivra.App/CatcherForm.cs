namespace Swivra.App;

/// <summary>
/// A nearly invisible window laid exactly over the wheel. It is the real OLE drop target,
/// so the host sees file paths on DragEnter (needed for the radar) and controls the drop
/// effect. The WebView underneath never receives the drag.
/// </summary>
internal sealed class CatcherForm : Form
{
    public CatcherForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        Opacity = 0.01;
        AllowDrop = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    public void Cover(Rectangle bounds)
    {
        Bounds = bounds;
        Native.SetCircleRegion(Handle, bounds.Width, bounds.Height, 0);
    }
}

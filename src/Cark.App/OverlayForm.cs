using System.Drawing.Imaging;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Cark.App;

/// <summary>
/// The wheel itself: a borderless, never-activated, topmost window cut to a circle,
/// with a WebView2 drawing the wheel. The glass is a blurred snapshot of what was behind
/// the window a moment before it appeared: system acrylic ignores window regions and
/// paints a square, while the wheel only lives for the second or two of a drag.
/// </summary>
internal sealed class OverlayForm : Form
{
    public const int CssSize = 340;       // must match --size in web/wheel.css
    public const int CssGlassInset = 18;  // glass radius 152 => (340 - 304) / 2
    public const int CssBleed = 24;       // extra snapshot margin so the blur has no dark edge

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Transparent };

    public bool Ready { get; private set; }

    public event Action<JsonElement>? Message;

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(18, 22, 38);
        Controls.Add(_web);
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

    public async Task InitAsync()
    {
        // WebView2 needs a real window; park it off-screen while it boots.
        Bounds = new Rectangle(-32000, -32000, CssSize, CssSize);
        Show();

        var options = new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required");
        var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Log.Dir, "WebView2"), options);
        await _web.EnsureCoreWebView2Async(env);

        var core = _web.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        _web.AllowExternalDrop = false;

        string webDir = Path.Combine(AppContext.BaseDirectory, "web");
        core.SetVirtualHostNameToFolderMapping("cark.local", webDir, CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += (_, args) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(args.WebMessageAsJson);
                Message?.Invoke(doc.RootElement.Clone());
            }
            catch (Exception ex)
            {
                Log.Write($"bad web message: {ex.Message}");
            }
        };

        var loaded = new TaskCompletionSource<bool>();
        core.NavigationCompleted += (_, args) => loaded.TrySetResult(args.IsSuccess);
        core.Navigate("https://cark.local/index.html");
        bool ok = await loaded.Task;
        Log.Write($"webview ready ok={ok} version={core.Environment.BrowserVersionString}");

        Hide();
        Ready = ok;
    }

    /// <summary>Centers the wheel on a physical screen point and shows it without stealing focus.</summary>
    public void ShowAt(Point center)
    {
        double scale = Native.ScaleAt(center);
        int size = (int)Math.Round(CssSize * scale);
        Rectangle area = Screen.FromPoint(center).WorkingArea;
        int x = Math.Clamp(center.X - size / 2, area.Left, area.Right - size);
        int y = Math.Clamp(center.Y - size / 2, area.Top, area.Bottom - size);
        Bounds = new Rectangle(x, y, size, size);
        Native.SetCircleRegion(Handle, size, size, (int)Math.Round(CssGlassInset * scale));

        int bleed = (int)Math.Round(CssBleed * scale);
        Post(new { type = "backdrop", src = Snapshot(Rectangle.Inflate(Bounds, bleed, bleed)) });
        Show();
    }

    /// <summary>Maps a physical screen point into wheel CSS units, whatever the WebView's own zoom.</summary>
    public PointF ToCss(Point screen) =>
        new((float)((screen.X - Left) * (double)CssSize / Width), (float)((screen.Y - Top) * (double)CssSize / Height));

    public void Post(object message)
    {
        if (!Ready) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private static string Snapshot(Rectangle r)
    {
        try
        {
            using var bmp = new Bitmap(r.Width, r.Height);
            using (var g = Graphics.FromImage(bmp))
                g.CopyFromScreen(r.Location, Point.Empty, r.Size, CopyPixelOperation.SourceCopy);
            using var ms = new MemoryStream();
            var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ps = new EncoderParameters(1);
            ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
            bmp.Save(ms, jpeg, ps);
            return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch (Exception ex)
        {
            Log.Write($"snapshot failed: {ex.Message}");
            return "";
        }
    }
}

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using QRCoder;

namespace Swivra.App;

/// <summary>
/// "To phone": a tiny HTTP server on the PC's LAN address that serves only the dropped files
/// under an unguessable token path, plus the QR code that points a phone at it.
/// TcpListener instead of HttpListener: no URL ACL or admin rights needed.
/// </summary>
internal sealed class PhoneShare : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const int MaxHeadBytes = 8192, MaxConnections = 16;

    private readonly TcpListener _listener;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private readonly List<(string Name, string Path)> _files = [];
    private readonly System.Threading.Timer _expiry;
    private readonly CancellationTokenSource _cts = new();
    private int _downloads, _active, _disposed;

    /// <summary>The address to show: the file itself for one file, the index page for several.</summary>
    public string Url { get; }
    public int Downloads => _downloads;
    /// <summary>Raised (on a pool thread) after a file was sent completely, with the running total.</summary>
    public event Action<int>? Downloaded;
    /// <summary>Raised (on a pool thread) when the lifetime ran out; the server is already stopped.</summary>
    public event Action? Expired;

    private PhoneShare(IPAddress ip, IReadOnlyList<string> files)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in files)
        {
            string name = System.IO.Path.GetFileName(f), baseName = System.IO.Path.GetFileNameWithoutExtension(f), ext = System.IO.Path.GetExtension(f);
            for (int i = 2; !used.Add(name); i++) name = $"{baseName} ({i}){ext}";
            _files.Add((name, f));
        }
        _listener = new TcpListener(ip, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        string root = $"http://{ip}:{port}/{_token}/";
        Url = _files.Count == 1 ? root + Uri.EscapeDataString(_files[0].Name) : root;
        _ = AcceptLoop();
        _expiry = new System.Threading.Timer(_ => { Dispose(); Expired?.Invoke(); }, null, Lifetime, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Starts sharing; throws <see cref="InvalidOperationException"/> when the PC has no LAN address.</summary>
    public static PhoneShare Start(IReadOnlyList<string> files, IPAddress? bindOverride = null)
    {
        var ip = bindOverride ?? FindLanAddress() ?? throw new InvalidOperationException("no LAN IPv4 address");
        return new PhoneShare(ip, files);
    }

    /// <summary>
    /// The address a phone on the same Wi-Fi can reach: an up Ethernet/Wi-Fi adapter with a default
    /// gateway, skipping virtual switches (Hyper-V, VMware, VirtualBox, WSL) that have no gateway or a private-only one.
    /// </summary>
    internal static IPAddress? FindLanAddress()
    {
        IPAddress? best = null;
        int bestScore = -1;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is not (NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet)) continue;
            var props = nic.GetIPProperties();
            if (!props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
            string desc = nic.Description + " " + nic.Name;
            bool virt = new[] { "virtual", "vmware", "vethernet", "hyper-v", "vbox", "wsl", "tap", "tailscale", "vpn" }.Any(v => desc.Contains(v, StringComparison.OrdinalIgnoreCase));
            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                byte[] b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue; // link-local: no DHCP answer
                int score = (virt ? 0 : 2) + (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0);
                if (score > bestScore) { best = ua.Address; bestScore = score; }
            }
        }
        return best;
    }

    public static string QrDataUrl(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        byte[] png = new PngByteQRCode(data).GetGraphic(6, [0x0B, 0x10, 0x20, 0xFF], [0xFF, 0xFF, 0xFF, 0xFF], true);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { return; } // stopped
            if (Interlocked.Increment(ref _active) > MaxConnections) { Interlocked.Decrement(ref _active); client.Dispose(); continue; }
            _ = Task.Run(async () =>
            {
                try { await Handle(client); }
                catch (Exception ex) { Log.Write($"phone share request ended: {ex.GetType().Name}"); }
                finally { client.Dispose(); Interlocked.Decrement(ref _active); }
            });
        }
    }

    private async Task Handle(TcpClient client)
    {
        client.ReceiveTimeout = client.SendTimeout = 15000;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        cts.CancelAfter(TimeSpan.FromHours(1));
        var stream = client.GetStream();

        // Read the request head (we ignore any body and all headers).
        var head = new byte[MaxHeadBytes];
        int len = 0;
        while (len < head.Length)
        {
            using var rt = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            rt.CancelAfter(10000);
            int n = await stream.ReadAsync(head.AsMemory(len), rt.Token);
            if (n == 0) return;
            len += n;
            if (Encoding.ASCII.GetString(head, 0, len).Contains("\r\n\r\n")) break;
        }
        string[] first = Encoding.ASCII.GetString(head, 0, len).Split("\r\n")[0].Split(' ');
        if (first.Length < 2 || first[0] is not ("GET" or "HEAD")) { await Reply(stream, 404, "text/plain", "Not found"); return; }
        bool headOnly = first[0] == "HEAD";

        string target = first[1];
        int q = target.IndexOfAny(['?', '#']);
        if (q >= 0) target = target[..q];
        string prefix = "/" + _token + "/";
        if (!target.StartsWith(prefix, StringComparison.Ordinal)) { await Reply(stream, 404, "text/plain", "Not found"); return; }

        string rest;
        try { rest = Uri.UnescapeDataString(target[prefix.Length..]); }
        catch { await Reply(stream, 404, "text/plain", "Not found"); return; }

        if (rest.Length == 0) { await Reply(stream, 200, "text/html; charset=utf-8", IndexPage(), headOnly); return; }

        // Look the name up in our own list: the request never turns into a file path.
        var file = _files.FirstOrDefault(f => f.Name == rest);
        if (file.Name is null) { await Reply(stream, 404, "text/plain", "Not found"); return; }

        FileStream fs;
        try { fs = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true); }
        catch { await Reply(stream, 404, "text/plain", "Not found"); return; }
        await using (fs)
        {
            string ascii = new string(file.Name.Select(c => c is < ' ' or > '~' or '"' or '\\' or '%' ? '_' : c).ToArray());
            string h = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n" +
                       $"Content-Length: {fs.Length}\r\n" +
                       $"Content-Disposition: attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(file.Name)}\r\n" +
                       "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(h), cts.Token);
            if (headOnly) return;
            await fs.CopyToAsync(stream, 81920, cts.Token);
            await stream.FlushAsync(cts.Token);
        }
        int total = Interlocked.Increment(ref _downloads);
        Log.Write($"phone share: file sent ({total} total)");
        Downloaded?.Invoke(total);
    }

    private string IndexPage()
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
          .Append("<title>Swivra</title><style>body{font:16px system-ui,sans-serif;margin:0;padding:20px;background:#0f1530;color:#eef2ff}")
          .Append("h1{font-size:18px;margin:0 0 16px}a{display:block;padding:16px;margin:0 0 10px;border-radius:12px;background:#2b3a6e;color:#fff;text-decoration:none;word-break:break-all}</style></head><body>")
          .Append("<h1>").Append(WebUtility.HtmlEncode(Lang.T("Dosyalar", "Files"))).Append("</h1>");
        foreach (var (name, path) in _files)
            sb.Append("<a href=\"").Append(Uri.EscapeDataString(name)).Append("\">").Append(WebUtility.HtmlEncode(name))
              .Append(" <small>(").Append(FileKinds.HumanSize(new FileInfo(path).Length)).Append(")</small></a>");
        return sb.Append("</body></html>").ToString();
    }

    private static async Task Reply(NetworkStream stream, int status, string type, string body, bool headOnly = false)
    {
        byte[] b = Encoding.UTF8.GetBytes(body);
        string reason = status == 200 ? "OK" : "Not Found";
        string h = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {b.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(h));
        if (!headOnly) await stream.WriteAsync(b);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _expiry.Dispose();
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already closed */ }
        Log.Write($"phone share stopped ({_downloads} downloads)");
    }
}

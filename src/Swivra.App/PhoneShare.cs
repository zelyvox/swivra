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
/// Files are addressed by number (/token/1), so the QR stays small whatever the file names are.
/// </summary>
internal sealed class PhoneShare : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const int MaxHeadBytes = 8192, MaxConnections = 16, MaxPerClient = 4;
    private static readonly TimeSpan HeadDeadline = TimeSpan.FromSeconds(5);
    // A download that started before the panel closed may finish, but not forever: it gets ten more
    // minutes after the panel closes, and any transfer is cut when the phone takes no data for a minute.
    // A single transfer never runs longer than an hour (a big file on slow Wi-Fi while the panel is open).
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AfterClose = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan TransferCeiling = TimeSpan.FromHours(1);

    private readonly TcpListener _listener;
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private readonly List<(string Name, string Path)> _files = [];
    private readonly System.Threading.Timer _expiry;
    private readonly CancellationTokenSource _accept = new();   // new connections
    private readonly CancellationTokenSource _transfers = new(); // running downloads
    private readonly Dictionary<IPAddress, int> _perClient = new();
    private readonly List<Transfer> _running = [];
    private int _downloads, _active, _stopped, _disposed;

    /// <summary>One file on its way to a phone.</summary>
    private sealed class Transfer(string name, long total)
    {
        public string Name { get; } = name;
        public long Total { get; } = total;
        public long Sent;
    }

    /// <summary>What is being sent right now, summed over all phones: shown to the user while it runs.</summary>
    public readonly record struct Activity(int Count, long Sent, long Total, string? Name);

    /// <summary>The address to show: the file itself for one file, the index page for several.</summary>
    public string Url { get; }
    public int Downloads => _downloads;
    /// <summary>Raised (on a pool thread) after a file was sent completely, with the running total.</summary>
    public event Action<int>? Downloaded;
    /// <summary>Raised (on a pool thread) when a started download ended: the file name and whether it arrived whole.</summary>
    public event Action<string, bool>? TransferEnded;
    /// <summary>Raised (on a pool thread) when the lifetime ran out; the server no longer accepts connections.</summary>
    public event Action? Expired;

    /// <summary>A snapshot of the downloads in progress (the UI polls it a few times a second).</summary>
    public Activity Current
    {
        get
        {
            lock (_running)
            {
                if (_running.Count == 0) return default;
                long sent = 0, total = 0;
                foreach (var t in _running) { sent += Interlocked.Read(ref t.Sent); total += t.Total; }
                string? name = _running.Select(t => t.Name).Distinct().Count() == 1 ? _running[0].Name : null;
                return new Activity(_running.Count, sent, total, name);
            }
        }
    }

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
        Url = _files.Count == 1 ? root + "1" : root;
        _ = AcceptLoop();
        _expiry = new System.Threading.Timer(_ =>
        {
            // Runs on a timer thread; nothing here may throw, or the process goes down.
            try { Stop(); Expired?.Invoke(); }
            catch (Exception ex) { Log.Write($"phone share expiry: {ex.GetType().Name}"); }
        }, null, Lifetime, Timeout.InfiniteTimeSpan);
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
    /// Private (RFC 1918 / CGNAT) addresses win over public ones, which would expose the port beyond the home network.
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
                bool priv = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
                int score = (priv ? 4 : 0) + (virt ? 0 : 2) + (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0);
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
        while (!_accept.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_accept.Token); }
            catch { return; } // stopped
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
            if (!TryEnter(remote)) { client.Dispose(); continue; }
            _ = Task.Run(async () =>
            {
                try { await Handle(client); }
                catch (Exception ex) { Log.Write($"phone share request ended: {ex.GetType().Name}"); }
                finally { client.Dispose(); Leave(remote); }
            });
        }
    }

    /// <summary>Caps connections overall and per device, so one device on the LAN cannot hold every slot.</summary>
    private bool TryEnter(IPAddress remote)
    {
        lock (_perClient)
        {
            int mine = _perClient.GetValueOrDefault(remote);
            if (_active >= MaxConnections || mine >= MaxPerClient) return false;
            _perClient[remote] = mine + 1;
            _active++;
            return true;
        }
    }

    private void Leave(IPAddress remote)
    {
        lock (_perClient)
        {
            _active--;
            if (--_perClient[remote] == 0) _perClient.Remove(remote);
        }
    }

    private async Task Handle(TcpClient client)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_transfers.Token);
        cts.CancelAfter(TransferCeiling);
        var stream = client.GetStream();

        // Read the request head (we ignore any body and all headers). One deadline for the whole
        // head: a client trickling a byte at a time must not keep a slot.
        var head = new byte[MaxHeadBytes];
        int len = 0;
        using (var headCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
        {
            headCts.CancelAfter(HeadDeadline);
            while (len < head.Length)
            {
                int n = await stream.ReadAsync(head.AsMemory(len), headCts.Token);
                if (n == 0) return;
                len += n;
                string sofar = Encoding.ASCII.GetString(head, 0, len);
                if (sofar.Contains("\r\n\r\n") || sofar.Contains("\n\n")) break;
            }
        }
        string[] first = Encoding.ASCII.GetString(head, 0, len).Split('\n')[0].TrimEnd('\r').Split(' ');
        if (first.Length < 2 || first[0] is not ("GET" or "HEAD")) { await Reply(stream, 404, "text/plain", "Not found"); return; }
        bool headOnly = first[0] == "HEAD";

        string target = first[1];
        int q = target.IndexOfAny(['?', '#']);
        if (q >= 0) target = target[..q];
        string prefix = "/" + _token + "/";
        if (!target.StartsWith(prefix, StringComparison.Ordinal)) { await Reply(stream, 404, "text/plain", "Not found"); return; }

        string rest = target[prefix.Length..];
        if (rest.Length == 0) { await Reply(stream, 200, "text/html; charset=utf-8", IndexPage(), headOnly); return; }

        // Files are addressed by their 1-based number in our own list: the request never turns into a file path.
        if (!int.TryParse(rest, System.Globalization.NumberStyles.None, null, out int index) || index < 1 || index > _files.Count)
        {
            await Reply(stream, 404, "text/plain", "Not found");
            return;
        }
        var file = _files[index - 1];

        FileStream fs;
        try { fs = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, true); }
        catch { await Reply(stream, 404, "text/plain", "Not found"); return; }
        await using (fs)
        {
            long length = fs.Length;
            string ascii = new string(file.Name.Select(c => c is < ' ' or > '~' or '"' or '\\' or '%' ? '_' : c).ToArray());
            string h = "HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\n" +
                       $"Content-Length: {length}\r\n" +
                       $"Content-Disposition: attachment; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(file.Name)}\r\n" +
                       "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
            await WriteStalling(stream, Encoding.ASCII.GetBytes(h), cts.Token);
            if (headOnly) return;

            // From here on the user sees the download in the panel (or the tray, once the panel is closed).
            var transfer = new Transfer(file.Name, length);
            lock (_running) _running.Add(transfer);
            bool whole = false;
            try
            {
                // Send exactly the announced length, even if another program is writing the file meanwhile.
                var buf = new byte[81920];
                long left = length;
                while (left > 0)
                {
                    int n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, left)), cts.Token);
                    if (n == 0) return; // the file shrank: the phone sees a short download, not counted
                    await WriteStalling(stream, buf.AsMemory(0, n), cts.Token);
                    left -= n;
                    Interlocked.Add(ref transfer.Sent, n);
                }
                await stream.FlushAsync(cts.Token);
                whole = true;
            }
            finally
            {
                lock (_running) _running.Remove(transfer);
                if (whole)
                {
                    int total = Interlocked.Increment(ref _downloads);
                    Log.Write($"phone share: file sent ({total} total)");
                    Downloaded?.Invoke(total);
                }
                else Log.Write("phone share: download ended early");
                TransferEnded?.Invoke(transfer.Name, whole);
            }
        }
    }

    /// <summary>One write that gives up when the phone takes no data for <see cref="StallTimeout"/>.</summary>
    private static async Task WriteStalling(NetworkStream stream, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        await stream.WriteAsync(data, stall.Token);
    }

    private string IndexPage()
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
          .Append("<title>Swivra</title><style>body{font:16px system-ui,sans-serif;margin:0;padding:20px;background:#0f1530;color:#eef2ff}")
          .Append("h1{font-size:18px;margin:0 0 16px}a{display:block;padding:16px;margin:0 0 10px;border-radius:12px;background:#2b3a6e;color:#fff;text-decoration:none;word-break:break-all}</style></head><body>")
          .Append("<h1>").Append(WebUtility.HtmlEncode(Lang.T("Dosyalar", "Files"))).Append("</h1>");
        for (int i = 0; i < _files.Count; i++)
        {
            var (name, path) = _files[i];
            string size;
            try { size = FileKinds.HumanSize(new FileInfo(path).Length); }
            catch { continue; } // moved or deleted since the share started
            sb.Append("<a href=\"").Append(i + 1).Append("\">").Append(WebUtility.HtmlEncode(name))
              .Append(" <small>(").Append(size).Append(")</small></a>");
        }
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

    /// <summary>
    /// Stops taking new connections (panel closed, time is up). Downloads already running may
    /// finish, within <see cref="AfterClose"/> (and <see cref="StallTimeout"/> as always).
    /// </summary>
    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1) return;
        _expiry.Dispose();
        _accept.Cancel();
        try { _listener.Stop(); } catch { /* already closed */ }
        _transfers.CancelAfter(AfterClose);
        Log.Write($"phone share stopped ({_downloads} downloads)");
    }

    /// <summary>Hard stop ("stop now" in the panel or tray, app exit): running downloads are cut as well.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Stop();
        _transfers.Cancel();
    }
}

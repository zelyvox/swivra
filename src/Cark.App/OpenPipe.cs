using System.IO.Pipes;
using System.Text.Json;

namespace Cark.App;

/// <summary>
/// Single-instance hand-off for "Çark ile aç": a second Cark.exe sends its paths to the
/// running one over a per-user named pipe and exits. Messages are a UTF-8 JSON string array.
/// </summary>
internal sealed class OpenPipe : IDisposable
{
    public const int MaxFiles = 200;
    private const int MaxBytes = 1 << 20;
    private const int ConnectTimeoutMs = 1500;

    private static string Name => "Cark.Open." + Environment.UserName;

    private readonly CancellationTokenSource _cts = new();
    private readonly SynchronizationContext _ui;
    private readonly Action<string[]> _onOpen;

    /// <summary>Starts listening. <paramref name="onOpen"/> runs on <paramref name="ui"/> with validated paths.</summary>
    public OpenPipe(SynchronizationContext ui, Action<string[]> onOpen)
    {
        _ui = ui;
        _onOpen = onOpen;
        _ = AcceptLoopAsync();
    }

    /// <summary>Everything after <c>--open</c> on the command line.</summary>
    public static string[] PathsFromArgs(string[] args)
    {
        int i = Array.IndexOf(args, "--open");
        if (i < 0) return [];
        return args[(i + 1)..].Where(a => !string.IsNullOrWhiteSpace(a)).Select(a =>
        {
            try { return Path.GetFullPath(a); }
            catch { return a; }
        }).ToArray();
    }

    /// <summary>Keeps existing files with absolute paths, at most <see cref="MaxFiles"/>.</summary>
    public static string[] Validate(IEnumerable<string?> paths) => paths
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => p!)
        .Where(p =>
        {
            try { return Path.IsPathFullyQualified(p) && File.Exists(p); }
            catch { return false; }
        })
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(MaxFiles)
        .ToArray();

    public static bool Send(string[] paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(ConnectTimeoutMs);
            client.Write(JsonSerializer.SerializeToUtf8Bytes(paths));
            client.Flush();
            Log.Write($"open handed off {paths.Length} path(s)");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"open hand-off failed: {ex.Message}");
            return false;
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(Name, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                _ = HandleAsync(server);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                server?.Dispose();
                Log.Write($"open pipe error: {ex.Message}");
                try { await Task.Delay(1000, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server)
    {
        try
        {
            using (server)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(3000);
                using var ms = new MemoryStream();
                var buf = new byte[8192];
                int n;
                while ((n = await server.ReadAsync(buf, timeout.Token)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > MaxBytes) throw new InvalidDataException("message too large");
                }
                var paths = JsonSerializer.Deserialize<string?[]>(ms.ToArray()) ?? [];
                var valid = Validate(paths);
                Log.Write($"open received {paths.Length} path(s), {valid.Length} kept");
                if (valid.Length > 0) _ui.Post(_ => _onOpen(valid), null);
            }
        }
        catch (Exception ex)
        {
            Log.Write($"open message rejected: {ex.Message}");
        }
    }

    public void Dispose() => _cts.Cancel(); // the accept loop still reads the token, so it is not disposed
}

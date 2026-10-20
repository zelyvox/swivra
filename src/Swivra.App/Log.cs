namespace Swivra.App;

internal static class Log
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Swivra");

    private static readonly string FilePath = Path.Combine(Dir, "swivra.log");
    private static readonly string OldPath = Path.Combine(Dir, "swivra.old.log");
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                // One generation of history: the full log becomes swivra.old.log, replacing the previous one.
                try { if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes) File.Move(FilePath, OldPath, true); }
                catch { /* locked by another process: keep appending, try again next time */ }
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}

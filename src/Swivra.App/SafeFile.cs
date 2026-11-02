namespace Swivra.App;

/// <summary>
/// File helpers for the user's own files: they may be open in Word or Excel, marked read-only (shared
/// folders, mail attachments) or sit on a long path. Plain File.* calls fail or misbehave on each of those.
/// </summary>
internal static class SafeFile
{
    /// <summary>Reads the whole file even while another program has it open for writing.</summary>
    public static byte[] ReadAll(string path)
    {
        using var f = OpenRead(path);
        var data = new byte[f.Length];
        f.ReadExactly(data);
        return data;
    }

    public static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Copies and makes the copy writable: File.Copy keeps the read-only flag, and a read-only copy
    /// can be neither cleaned nor deleted afterwards.
    /// </summary>
    public static void CopyWritable(string source, string target)
    {
        File.Copy(source, target);
        var attrs = File.GetAttributes(target);
        if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(target, attrs & ~FileAttributes.ReadOnly);
    }

    /// <summary>Deletes a file Swivra made, read-only or not; a leftover is only logged.</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Write($"could not delete {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Work folders (%TEMP%\swivra-*) hold copies of the user's documents. Each run deletes its own;
    /// one left by a crash or a forced exit is removed at the next start, once it is an hour old.
    /// </summary>
    public static void SweepTemp()
    {
        try
        {
            foreach (string d in Directory.GetDirectories(Path.GetTempPath(), "swivra-*"))
            {
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(d) < TimeSpan.FromHours(1)) continue;
                try
                {
                    foreach (string f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                        File.SetAttributes(f, FileAttributes.Normal);
                    Directory.Delete(d, true);
                }
                catch { /* still in use: next time */ }
            }
        }
        catch (Exception ex) { Log.Write($"temp sweep failed: {ex.Message}"); }
    }
}

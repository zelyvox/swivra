using Microsoft.Win32;

namespace Cark.App;

/// <summary>
/// The classic Explorer verb "Çark ile aç" for every file type, under HKCU so no admin is
/// needed. On Windows 11 it lives under "Show more options" (Shift+right-click).
/// </summary>
internal static class ShellMenu
{
    private const string KeyPath = @"Software\Classes\*\shell\Cark";

    public static string ExePath
    {
        get
        {
            string path = Environment.ProcessPath ?? "";
            // Started as "dotnet Cark.dll": point the verb at the apphost next to it instead.
            if (Path.GetFileName(path).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                string host = Path.Combine(AppContext.BaseDirectory, "Cark.exe");
                if (File.Exists(host)) path = host;
            }
            return path;
        }
    }

    private static string Command => $"\"{ExePath}\" --open \"%1\"";

    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath + @"\command");
            return key is not null;
        }
    }

    private static bool IsCurrent
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath + @"\command");
            return key?.GetValue(null) as string == Command;
        }
    }

    public static void Register()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
        {
            key.SetValue("MUIVerb", "Çark ile aç");
            key.SetValue("Icon", $"{ExePath},0");
            key.SetValue("MultiSelectModel", "Player");
        }
        using (var cmd = Registry.CurrentUser.CreateSubKey(KeyPath + @"\command"))
            cmd.SetValue(null, Command);
        Log.Write($"shell menu registered for {ExePath}");
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
        Log.Write("shell menu removed");
    }

    /// <summary>Registers, or re-registers when the app has moved since the last time.</summary>
    public static void EnsureRegistered()
    {
        try
        {
            if (!IsCurrent) Register();
        }
        catch (Exception ex)
        {
            Log.Write($"shell menu register failed: {ex.Message}");
        }
    }

    /// <summary>Turns the verb on or off and remembers the choice. Returns the resulting state.</summary>
    public static bool SetEnabled(Settings settings, bool on)
    {
        try
        {
            if (on) Register(); else Unregister();
            settings.ShellMenu = on;
            settings.Save();
        }
        catch (Exception ex)
        {
            Log.Write($"shell menu toggle failed: {ex.Message}");
        }
        return IsRegistered;
    }
}

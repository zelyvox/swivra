using Microsoft.Win32;

namespace Swivra.App;

/// <summary>
/// "Start with Windows": a per-user entry under HKCU\...\Run, so no admin rights are needed.
/// The entry counts as on only when it points at this Swivra.exe; an entry left by a copy in
/// another folder shows as off, and turning it on points it here.
/// </summary>
internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Swivra";

    private static string Command => $"\"{Environment.ProcessPath}\"";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string v && string.Equals(v, Command, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Write($"startup read failed: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>Adds or removes the entry; false when the registry refused.</summary>
    public static bool Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Write($"start with windows {(on ? "on" : "off")}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"startup write failed: {ex.Message}");
            return false;
        }
    }
}

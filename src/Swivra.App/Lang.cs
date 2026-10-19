using System.Globalization;
using System.Text.Json;

namespace Swivra.App;

/// <summary>
/// UI language: Turkish or English. The first run follows the Windows display language;
/// a choice from the tray menu is saved and wins from then on.
/// Strings are written side by side where they are used: <c>Lang.T("Kırp", "Crop")</c>.
/// </summary>
internal static class Lang
{
    private static readonly string SettingsPath = Path.Combine(Log.Dir, "settings.json");

    public static string Current { get; private set; } = Load();

    public static bool IsTurkish => Current == "tr";

    public static string T(string tr, string en) => IsTurkish ? tr : en;

    /// <summary>Saves the choice for later runs.</summary>
    public static void Set(string lang)
    {
        Current = lang == "tr" ? "tr" : "en";
        try
        {
            Directory.CreateDirectory(Log.Dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { lang = Current }));
        }
        catch (Exception ex)
        {
            Log.Write($"settings save failed: {ex.Message}");
        }
    }

    /// <summary>For this run only (screenshots, tests); nothing is saved.</summary>
    public static void Override(string lang) => Current = lang == "tr" ? "tr" : "en";

    private static string Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                if (doc.RootElement.TryGetProperty("lang", out var l) && l.GetString() is "tr" or "en") return l.GetString()!;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"settings read failed: {ex.Message}");
        }
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";
    }
}

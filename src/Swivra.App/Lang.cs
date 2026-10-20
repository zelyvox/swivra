using System.Globalization;

namespace Swivra.App;

/// <summary>
/// UI language: Turkish or English. The first run follows the Windows display language;
/// a choice from the tray menu is saved and wins from then on.
/// Strings are written side by side where they are used: <c>Lang.T("Kırp", "Crop")</c>.
/// </summary>
internal static class Lang
{
    public static string Current { get; private set; } = Load();

    public static bool IsTurkish => Current == "tr";

    public static string T(string tr, string en) => IsTurkish ? tr : en;

    /// <summary>Saves the choice for later runs.</summary>
    public static void Set(string lang)
    {
        Current = lang == "tr" ? "tr" : "en";
        Settings.Set("lang", Current);
    }

    /// <summary>For this run only (screenshots, tests); nothing is saved.</summary>
    public static void Override(string lang) => Current = lang == "tr" ? "tr" : "en";

    private static string Load() =>
        Settings.GetString("lang") is "tr" or "en" ? Settings.GetString("lang")!
        : CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr" ? "tr" : "en";
}

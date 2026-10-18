using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cark.App;

/// <summary>User settings, kept as JSON next to the log in %LOCALAPPDATA%\Cark.</summary>
internal sealed class Settings
{
    private static readonly string FilePath = Path.Combine(Log.Dir, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    [JsonPropertyName("shellMenu")]
    public bool ShellMenu { get; set; } = true;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Write($"settings unreadable, using defaults: {ex.Message}");
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"settings save failed: {ex.Message}");
        }
    }
}

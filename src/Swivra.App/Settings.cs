using System.Text.Json.Nodes;

namespace Swivra.App;

/// <summary>
/// Small key/value store in settings.json next to the logs. Every write keeps the other keys,
/// so the language choice and the startup question do not overwrite each other.
/// </summary>
internal static class Settings
{
    private static readonly string Path_ = Path.Combine(Log.Dir, "settings.json");
    private static readonly object Gate = new();

    public static string? GetString(string key) => Read()[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    public static bool GetBool(string key) => Read()[key] is JsonValue v && v.TryGetValue(out bool b) && b;

    public static void Set(string key, JsonNode? value)
    {
        lock (Gate)
        {
            try
            {
                var root = Read();
                root[key] = value;
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllText(Path_, root.ToJsonString());
            }
            catch (Exception ex)
            {
                Log.Write($"settings save failed: {ex.Message}");
            }
        }
    }

    private static JsonObject Read()
    {
        try
        {
            if (File.Exists(Path_) && JsonNode.Parse(File.ReadAllText(Path_)) is JsonObject o) return o;
        }
        catch (Exception ex)
        {
            Log.Write($"settings read failed: {ex.Message}");
        }
        return new JsonObject();
    }
}

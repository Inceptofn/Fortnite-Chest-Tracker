using System.Text.Json;

namespace ChestCounter;

// Tracker preferences shared by the dashboard and the headless live counter. Saved next to the
// stats as tracker.json.
public sealed class TrackerSettings
{
    // Language of the Fortnite popup the live counter reads: "en" or "es". It picks the Windows
    // OCR language and the word the counter looks for under the XP popup.
    public string OcrLanguage { get; set; } = "en";

    // Optional: the exact label shown under the XP popup in your game, for when the built-in word
    // for your language is not what your client shows. Empty = use the built-in word only.
    public string CustomChestLabel { get; set; } = "";

    // Total Battle Royale matches the player says they have played. Used to estimate the chests
    // from matches no replay covers. 0 = not set.
    public int ManualTotalMatches { get; set; }
}

public static class TrackerConfig
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChestCounter", "tracker.json");

    public static TrackerSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return Normalize(JsonSerializer.Deserialize<TrackerSettings>(File.ReadAllText(Path)) ?? new());
        }
        catch { }
        return new();
    }

    public static void Save(TrackerSettings s)
    {
        try
        {
            Normalize(s);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            // Write-then-move so a reader never sees a half-written file.
            string tmp = Path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options));
            File.Move(tmp, Path, overwrite: true);
        }
        catch { }
    }

    private static TrackerSettings Normalize(TrackerSettings s)
    {
        s.OcrLanguage = string.Equals(s.OcrLanguage, "es", StringComparison.OrdinalIgnoreCase) ? "es" : "en";
        s.CustomChestLabel = (s.CustomChestLabel ?? "").Trim();
        if (s.ManualTotalMatches < 0) s.ManualTotalMatches = 0;
        return s;
    }
}

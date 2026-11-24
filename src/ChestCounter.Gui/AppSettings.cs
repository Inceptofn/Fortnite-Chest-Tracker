using System.IO;
using System.Text.Json;

namespace ChestCounter.Gui;

// Local GUI preferences (window behaviour + in-game overlay), separate from the overlay branding.
public sealed class AppSettings
{
    public bool InGameOverlay { get; set; }
    public bool MinimizeToTray { get; set; } = true;

    // Period shown on the main screen: 0 = 24 hours, 1 = 7 days, 2 = Season, 3 = All time.
    public int Filter { get; set; } = 2;
    public double? OverlayLeft { get; set; }
    public double? OverlayTop { get; set; }
}

public static class AppConfig
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChestCounter", "app.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ?? new();
        }
        catch { }
        return new();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(s, Options));
        }
        catch { }
    }
}

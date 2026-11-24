using System.Text.Json;

namespace ChestCounter;

// Streamer-editable overlay branding. Saved next to the stats; read live by the overlay.
public sealed class OverlaySettings
{
    public const string DefaultAccent = "#F3F1EC";

    public string Accent { get; set; } = DefaultAccent;       // brand/accent color (hex)
    public string Title { get; set; } = "Chest Tracker";  // header text

    // How opaque the whole overlay is (in-game card and OBS browser source), 0.2 - 1.0.
    public const double MinOpacity = 0.2;
    public double Opacity { get; set; } = 1.0;

    public static double ClampOpacity(double v) => double.IsNaN(v) ? 1.0 : Math.Clamp(v, MinOpacity, 1.0);
}

public static class OverlayConfig
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ChestCounter", "overlay.json");

    public static OverlaySettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var s = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(Path)) ?? new();
                // The old brass defaults move to the current default; a colour you picked stays.
                if (s.Accent is "#F5C147" or "#E7B23C") s.Accent = OverlaySettings.DefaultAccent;
                if (s.Title == "Chest Counter") s.Title = "Chest Tracker";
                s.Opacity = OverlaySettings.ClampOpacity(s.Opacity);
                return s;
            }
        }
        catch { }
        return new();
    }

    public static void Save(OverlaySettings s)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        // Write-then-move so the overlay server never reads a half-written file.
        string tmp = Path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options));
        File.Move(tmp, Path, overwrite: true);
    }

    // Branding as JSON for the overlay's /config.json endpoint (read fresh each request).
    public static string Json()
    {
        var s = Load();
        return JsonSerializer.Serialize(new
        {
            accent = s.Accent,
            title = string.IsNullOrWhiteSpace(s.Title) ? "Chest Tracker" : s.Title,
            opacity = OverlaySettings.ClampOpacity(s.Opacity),
            domain = ChestCounterService.Domain,
        });
    }
}

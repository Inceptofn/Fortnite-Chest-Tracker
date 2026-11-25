using System.Net.Http;
using ChestCounter;
using ChestCounter.Ocr;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

// Headless live chest counter + overlay (the GUI does the same thing with a UI).
//   chestcounter-live                 run the live counter and host/feed the overlay
//   chestcounter-live shot.png ...    OCR test mode on image file(s)

// OCR language and chest label come from the same tracker.json the dashboard writes.
var settings = TrackerConfig.Load();

if (args.Any(a => a.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
{
    var engine = LiveChestCounter.CreateEngine(settings.OcrLanguage);
    if (engine == null) { Console.WriteLine("No OCR engine."); return; }
    foreach (var path in args)
    {
        using var fs = File.OpenRead(path);
        var dec = await BitmapDecoder.CreateAsync(fs.AsRandomAccessStream());
        using var sb = await dec.GetSoftwareBitmapAsync();
        var res = await engine.RecognizeAsync(sb);
        Console.WriteLine($"{Path.GetFileName(path)}: \"{res.Text}\"");
    }
    return;
}

var counter = new LiveChestCounter(settings.OcrLanguage, settings.CustomChestLabel);
if (!counter.Available) { Console.WriteLine("No OCR language installed (Windows Settings > Time & Language)."); return; }
Console.WriteLine($"OCR language: {counter.Setup.Tag}" + (counter.Setup.FellBack ? $" ({settings.OcrLanguage} is not installed in Windows, using this instead)" : ""));

// Host the overlay if nothing else (e.g. the GUI) already has it; otherwise push counts to that server.
OverlayServer? server = new OverlayServer(7777)
{
    ConfigProvider = OverlayConfig.Json,
    StatsBuilder = live => ChestCounterService.OverlayJson(ChestCounterService.LoadStats(), live, TrackerConfig.Load().ManualTotalMatches),
};
try { server.Start(); Console.WriteLine($"Overlay:  http://localhost:{server.Port}/   (add as a Browser Source)"); }
catch { server = null; Console.WriteLine("Overlay already hosted by the Chest Counter window — pushing counts to it."); }

var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
counter.Log += Console.WriteLine;
counter.LiveChanged += n =>
{
    if (server != null) server.LiveCount = n;
    else { try { http.PostAsync($"http://localhost:7777/live?n={n?.ToString() ?? ""}", null); } catch { } }
};

counter.Start();
Console.WriteLine("Chest Counter — LIVE. Play Fortnite in borderless windowed. Ctrl+C to stop.\n");
Thread.Sleep(Timeout.Infinite);

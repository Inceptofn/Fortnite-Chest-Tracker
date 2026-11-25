using System.Drawing;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using ChestCounter;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ChestCounter.Ocr;

// Which OCR engine the live counter ended up with.
//   Available - false when Windows has no OCR language installed at all.
//   Tag       - language tag of the engine in use, for example "es-MX".
//   FellBack  - true when the requested language is not installed and another one is used instead.
public readonly record struct OcrSetup(bool Available, string Tag, bool FellBack);

// Real-time chest counter: screen-captures the center of the screen, OCRs the "+XP CHEST" popup,
// and reports how many chests you've searched this match. Anticheat-safe (external screen read only).
// Folds each finished match into the lifetime stats on disk and resets the live count.
public sealed class LiveChestCounter
{
    // Both are swapped as one when the language changes; the capture loop reads them every frame.
    private volatile OcrEngine? _engine;
    private volatile Regex _chestLabel = BuildChestLabel("en", null);
    private CancellationTokenSource? _cts;

    // `language` is "en" or "es" (anything else = "en"); `customLabel` is an optional extra word to
    // accept as the chest label (see TrackerSettings.CustomChestLabel).
    public LiveChestCounter(string? language = null, string? customLabel = null)
    {
        Setup = Configure(language, customLabel);
    }

    // False if Windows has no OCR language installed.
    public bool Available => _engine != null;

    // Which OCR engine is in use (see OcrSetup).
    public OcrSetup Setup { get; private set; }

    // Switch the OCR language and chest label while running. Returns what was actually set up.
    public OcrSetup Configure(string? language, string? customLabel)
    {
        var (engine, setup) = Resolve(language);
        _engine = engine;
        _chestLabel = BuildChestLabel(language, customLabel);
        Setup = setup;
        return setup;
    }

    // Change only the chest label (cheap: the OCR engine is left alone). Used while typing a custom label.
    public void SetChestLabel(string? language, string? customLabel) =>
        _chestLabel = BuildChestLabel(language, customLabel);

    // What Configure would give for `language`, without touching a running counter.
    public static OcrSetup Probe(string? language) => Resolve(language).Setup;

    // An OCR engine for "en" / "es", for tools that need one outside the live counter.
    public static OcrEngine? CreateEngine(string? language) => Resolve(language).Engine;

    // Windows only OCRs in languages that are installed (Settings > Time & language). Looks for the
    // requested one first (any region: es-MX, es-ES, en-US, ...); if it is not installed, falls back
    // to the user's profile languages, which is what the counter always used before, and says so.
    private static (OcrEngine? Engine, OcrSetup Setup) Resolve(string? language)
    {
        string wanted = string.Equals(language, "es", StringComparison.OrdinalIgnoreCase) ? "es" : "en";
        try
        {
            var lang = OcrEngine.AvailableRecognizerLanguages
                .FirstOrDefault(l => l.LanguageTag.StartsWith(wanted, StringComparison.OrdinalIgnoreCase));
            if (lang != null)
            {
                var engine = OcrEngine.TryCreateFromLanguage(lang);
                if (engine != null) return (engine, new OcrSetup(true, lang.LanguageTag, false));
            }
        }
        catch { }

        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine != null) return (engine, new OcrSetup(true, engine.RecognizerLanguage.LanguageTag, true));
        }
        catch { }

        return (null, new OcrSetup(false, "", false));
    }

    // A label line that is just the chest word for the game's language (CHEST in English, COFRE in
    // Spanish) or the custom label, optionally with the "X2" Fortnite appends when popups stack.
    // OCR sometimes reads the S as a 5, the O as a 0, the X as a K, or glues the two together.
    private static Regex BuildChestLabel(string? language, string? customLabel)
    {
        var words = new List<string>
        {
            string.Equals(language, "es", StringComparison.OrdinalIgnoreCase) ? @"C[O0Q]FRES?" : @"CHE[S5]T",
        };
        if (!string.IsNullOrWhiteSpace(customLabel))
        {
            var parts = customLabel.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0) words.Add(string.Join(@"\s+", parts.Select(Regex.Escape)));
        }
        string alternatives = string.Join("|", words);
        string pattern = $@"^\W*(?:{alternatives})\W*(?:[XxKk\u00d7]\s*(\d{{1,2}}))?\W*$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    // Raised when this match's searched-chest count changes (null = between matches).
    public event Action<int?>? LiveChanged;
    // Raised with human-readable log lines (match ended, chest detected, ...).
    public event Action<string>? Log;
    // Raised with the text of every XP popup the OCR reads, chest or not. Lets the UI show that
    // the screen is actually being read.
    public event Action<string>? PopupRead;

    public void Start()
    {
        if (_engine == null || _cts != null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => MatchWatchLoop(token));
        _ = Task.Run(() => CaptureLoop(token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        LiveChanged?.Invoke(null);
    }

    private volatile int _liveRaw = -1; // -1 = null (between matches)

    // Watches the Demos folder. Fortnite creates the replay file when a match starts but only makes
    // it readable when the match ends, so a file that fails to parse is tried again on the next pass.
    private async Task MatchWatchLoop(CancellationToken token)
    {
        var done = new HashSet<string>(ChestCounterService.FindReplays());
        var started = new HashSet<string>();
        while (!token.IsCancellationRequested)
        {
            foreach (var f in ChestCounterService.FindReplays())
            {
                if (done.Contains(f)) continue;
                if (started.Add(f))
                {
                    // A new match: start its live count from zero.
                    _liveRaw = 0;
                    LiveChanged?.Invoke(0);
                }

                var stats = ChestCounterService.LoadStats();
                var run = new ReplayScanner().Scan(new[] { f }, stats);
                if (run.Parsed == 0 && run.AlreadyCounted == 0) continue;   // still being written

                done.Add(f);
                ChestCounterService.Save(stats);
                _liveRaw = -1;
                LiveChanged?.Invoke(null);
                Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] match ended, replay read ({stats.AvgYou:F1} avg over {stats.Matches}); live count reset.");
            }
            try { await Task.Delay(4000, token); } catch { }
        }
    }

    // Fortnite shows XP popups at one spot, right of the crosshair: "+600 XP" with a small label
    // under it (CHEST, KNOCKED, ASSIST...). Capturing just that area, enlarged, is what makes the
    // small label readable; reading the whole screen misses it most of the time.
    private static Rectangle PopupArea(Rectangle screen) => new(
        screen.X + (int)(screen.Width * 0.58), screen.Y + (int)(screen.Height * 0.24),
        (int)(screen.Width * 0.28), (int)(screen.Height * 0.24));

    private async Task CaptureLoop(CancellationToken token)
    {
        var area = PopupArea(System.Windows.Forms.Screen.PrimaryScreen!.Bounds);

        int credited = 0;      // chests already counted from the popup currently on screen
        int emptyFrames = 0;
        while (!token.IsCancellationRequested)
        {
            int n = 0;
            try
            {
                using var shot = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(shot)) g.CopyFromScreen(area.X, area.Y, 0, 0, area.Size);
                n = await ChestsInPopup(shot);
            }
            catch { }

            if (n > credited)
            {
                int cur = _liveRaw < 0 ? 0 : _liveRaw;
                _liveRaw = cur + (n - credited);
                credited = n;
                LiveChanged?.Invoke(_liveRaw);
                Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] CHEST (this match: {_liveRaw})");
            }

            // One unreadable frame in the middle of a popup must not make the same chest count twice,
            // so the popup only counts as gone after a few empty frames in a row.
            emptyFrames = n == 0 ? emptyFrames + 1 : 0;
            if (emptyFrames >= 4) credited = 0;

            try { await Task.Delay(120, token); } catch { }
        }
    }

    private string _lastPopup = "";

    // Chests named by the XP popup in this capture of the popup area. The text is white with a
    // coloured outline, so everything that is not near-white is dropped before reading. Two
    // passes with different settings: each one misses frames the other gets.
    private async Task<int> ChestsInPopup(Bitmap area)
    {
        var a = await ReadWhiteText(area, scale: 2, minBrightness: 190);
        var b = await ReadWhiteText(area, scale: 3, minBrightness: 205);

        var longest = a.Count >= b.Count ? a : b;
        // Only report real XP popups; the area also catches kill-feed names and the like.
        if (longest.Any(l => l.Contains("XP", StringComparison.OrdinalIgnoreCase)))
        {
            string popup = string.Join(" / ", longest);
            if (popup != _lastPopup)
            {
                _lastPopup = popup;
                PopupRead?.Invoke(popup);
            }
        }
        return Math.Max(CountChests(a), CountChests(b));
    }

    private int CountChests(List<string> lines)
    {
        var label = _chestLabel;
        int chests = 0;
        foreach (var line in lines)
        {
            var m = label.Match(line);
            if (!m.Success) continue;
            chests += m.Groups[1].Success && int.TryParse(m.Groups[1].Value, out var times) ? Math.Max(1, times) : 1;
        }
        return chests;
    }

    private async Task<List<string>> ReadWhiteText(Bitmap area, int scale, int minBrightness)
    {
        var engine = _engine;   // may be swapped by Configure while we work
        if (engine == null) return new List<string>();

        using var big = new Bitmap(area.Width * scale, area.Height * scale, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(area, new Rectangle(0, 0, big.Width, big.Height), new Rectangle(0, 0, area.Width, area.Height), GraphicsUnit.Pixel);
        }

        // Near-white pixels become black text on a white page.
        var data = big.LockBits(new Rectangle(0, 0, big.Width, big.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        var pixels = new byte[data.Stride * data.Height];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            bool white = pixels[i] >= minBrightness && pixels[i + 1] >= minBrightness && pixels[i + 2] >= minBrightness;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(white ? 0 : 255);
            pixels[i + 3] = 255;
        }
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        big.UnlockBits(data);

        using var ms = new MemoryStream();
        big.Save(ms, ImageFormat.Bmp);
        ms.Position = 0;
        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        using var bitmap = await decoder.GetSoftwareBitmapAsync();
        var result = await engine.RecognizeAsync(bitmap);
        return result.Lines.Select(l => l.Text.Trim()).Where(t => t.Length > 0).ToList();
    }
}

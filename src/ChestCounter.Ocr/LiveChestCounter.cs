using System.Drawing;
using System.Drawing.Imaging;
using ChestCounter;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace ChestCounter.Ocr;

// Real-time chest counter: screen-captures the center of the screen, OCRs the "+XP CHEST" popup,
// and reports how many chests you've searched this match. Anticheat-safe (external screen read only).
// Folds each finished match into the lifetime stats on disk and resets the live count.
public sealed partial class LiveChestCounter
{
    private readonly OcrEngine? _engine = OcrEngine.TryCreateFromUserProfileLanguages();
    private CancellationTokenSource? _cts;

    // False if Windows has no OCR language installed.
    public bool Available => _engine != null;

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

    private async Task CaptureLoop(CancellationToken token)
    {
        var b = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        int cw = (int)(b.Width * 0.70), ch = (int)(b.Height * 0.55);
        int cx = b.X + (b.Width - cw) / 2, cy = b.Y + (int)(b.Height * 0.16);

        int creditedThisBurst = 0;
        while (!token.IsCancellationRequested)
        {
            int n = 0;
            try
            {
                using var bmp = new Bitmap(cw, ch, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(cx, cy, 0, 0, new Size(cw, ch));
                n = await FrameChestCount(bmp);
            }
            catch { }

            if (n > creditedThisBurst)
            {
                int cur = _liveRaw < 0 ? 0 : _liveRaw;
                _liveRaw = cur + (n - creditedThisBurst);
                creditedThisBurst = n;
                LiveChanged?.Invoke(_liveRaw);
                Log?.Invoke($"[{DateTime.Now:HH:mm:ss}] CHEST (this match: {_liveRaw})");
            }
            if (n == 0) creditedThisBurst = 0;

            try { await Task.Delay(300, token); } catch { }
        }
    }

    // "CHEST" as a whole word (so our own overlay's "Chests this match" never counts), with the
    // "X2" Fortnite appends when popups stack. OCR sometimes reads the S as a 5, the X as a K, or
    // glues the two together ("CHESTX2").
    [System.Text.RegularExpressions.GeneratedRegex(@"\bCHE[S5]T(?:\s*[XxKk\u00d7]\s*(\d{1,2}))?(?![A-Za-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ChestPopup();

    [System.Text.RegularExpressions.GeneratedRegex(@"\d\s*XP\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex XpAmount();

    private string _lastPopup = "";

    // Chests shown by the XP popups on screen right now: a "+N XP" amount with a CHEST line.
    private async Task<int> FrameChestCount(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;
        var dec = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        using var sb = await dec.GetSoftwareBitmapAsync();
        var res = await _engine!.RecognizeAsync(sb);

        var lines = res.Lines.Select(l => l.Text).ToList();
        int xp = lines.FindIndex(l => XpAmount().IsMatch(l));
        if (xp < 0) return 0;

        // The popup is the XP line and the label under it.
        string popup = string.Join(" ", lines.Skip(xp).Take(2));
        if (popup != _lastPopup)
        {
            _lastPopup = popup;
            PopupRead?.Invoke(popup);
        }

        int chests = 0;
        foreach (var line in lines)
            foreach (System.Text.RegularExpressions.Match m in ChestPopup().Matches(line))
                chests += m.Groups[1].Success && int.TryParse(m.Groups[1].Value, out var times) ? Math.Max(1, times) : 1;
        return chests;
    }
}

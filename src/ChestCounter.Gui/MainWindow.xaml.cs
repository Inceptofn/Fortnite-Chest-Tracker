using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ChestCounter;
using ChestCounter.Ocr;
using Forms = System.Windows.Forms;

namespace ChestCounter.Gui;

public partial class MainWindow : Window
{
    private sealed record BarRow(GridLength Top, GridLength Bottom, Brush Fill, string Tip, string Label);
    private sealed record Row(string Name, string Value, bool Link = false);
    private sealed record OddsRow(string Label, string Item, string Odds, Brush OddsBrush, string Luck);
    private string _oddsKey = "";

    private LifetimeStats _stats = new();
    private OverlaySettings _overlay = new();
    private AppSettings _app = new();
    private OverlayServer? _server;
    private LiveChestCounter? _live;
    private InGameOverlay? _inGame;
    private Forms.NotifyIcon? _tray;
    private bool _loadingSettings;
    private bool _scanning;
    private int? _liveCount;
    private TrackerSettings _tracker = new();
    private System.Windows.Threading.DispatcherTimer? _overlaySaveTimer;
    private readonly System.Collections.Generic.Dictionary<string, PlayerWindow> _playerWindows = new(StringComparer.Ordinal);
    private int _barCount = 30;   // bars in the main chart; fewer on a narrow window

    // Windows' "start with Windows" entry launches us with --minimized.
    private static bool StartMinimized =>
        Environment.GetCommandLineArgs().Skip(1).Any(a => a.Equals(StartupRegistration.MinimizedArg, StringComparison.OrdinalIgnoreCase));

    public MainWindow()
    {
        InitializeComponent();
        _tracker = TrackerConfig.Load();
        _stats = ChestCounterService.LoadStats();
        _app = AppConfig.Load();

        // Restore the period the player last had selected. The earlier-matches estimate only shows
        // under "All time", so without this the total looked reset on every launch.
        switch (_app.Filter)
        {
            case 0: F0.IsChecked = true; break;
            case 1: F1.IsChecked = true; break;
            case 3: F3.IsChecked = true; break;
            default: F2.IsChecked = true; break;
        }
        Render();

        _loadingSettings = true;
        _overlay = OverlayConfig.Load();
        AccentBox.Text = _overlay.Accent;
        TitleBox.Text = _overlay.Title;
        InGameOverlayCheck.IsChecked = _app.InGameOverlay;
        TrayCheck.IsChecked = _app.MinimizeToTray;
        OpacitySlider.Value = Math.Round(_overlay.Opacity * 100);
        OpacityValueText.Text = $"{OpacitySlider.Value:0}%";
        OcrEn.IsChecked = _tracker.OcrLanguage != "es";
        OcrEs.IsChecked = _tracker.OcrLanguage == "es";
        CustomLabelBox.Text = _tracker.CustomChestLabel;
        ManualTotalBox.Text = _tracker.ManualTotalMatches > 0
            ? _tracker.ManualTotalMatches.ToString(CultureInfo.InvariantCulture) : "";
        StartupRegistration.RepairIfStale();
        StartupCheck.IsChecked = StartupRegistration.IsEnabled();
        UpdateSwatch();
        RenderEstimate();
        UpdateApplyButton();
        _loadingSettings = false;

        // The opacity slider fires continuously while dragged; save the file once it settles.
        var saveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveOverlay(); };
        _overlaySaveTimer = saveTimer;

        StartOverlay();
        StartLive();   // live counter is always on
        SetupTray();
        if (_app.InGameOverlay) ShowInGame();

        // Keep stats fresh (e.g. while the live counter folds in finished matches) so the overlay updates.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => { if (!_scanning) { _stats = ChestCounterService.LoadStats(); Render(); } };
        timer.Start();

        // Treasure Hour countdown ticks every second; the odds table only rebuilds when something changes.
        var clock = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => RenderTreasureHour();
        clock.Start();
        RenderTreasureHour();

        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && _app.MinimizeToTray) Hide();
        };

        FooterText.Text = ChestCounterService.Domain;
        if (!ChestCounterService.DemosFolderExists())
            StatusText.Text = "Fortnite replay folder not found.";

        // Started by Windows: open minimized, and go to the tray once the window exists.
        if (StartMinimized)
        {
            WindowState = WindowState.Minimized;
            Loaded += (_, _) => { if (_tray != null) Hide(); };
        }
    }

    private void StartOverlay()
    {
        _server = new OverlayServer(7777)
        {
            ConfigProvider = OverlayConfig.Json,
            StatsBuilder = live => ChestCounterService.OverlayJson(_stats, live, _tracker.ManualTotalMatches),
        };
        try { _server.Start(); OverlayStatusText.Text = "The overlay is running. Add the address above as a Browser Source in OBS."; }
        catch { _server = null; OverlayStatusText.Text = "Overlay already running in another Chest Tracker window."; }
    }

    private void LiveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_live == null) StartLive(); else StopLive();
    }

    private void StartLive()
    {
        if (_live != null) return;
        var live = new LiveChestCounter(_tracker.OcrLanguage, _tracker.CustomChestLabel);
        if (!live.Available)
        {
            LiveButton.Content = "No OCR language";
            LiveButton.IsEnabled = false;
            UpdateOcrStatus();
            return;
        }
        live.LiveChanged += n => Dispatcher.Invoke(() =>
        {
            _liveCount = n;
            if (_server != null) _server.LiveCount = n;
            _inGame?.SetLive(n);
            ThisGameText.Text = n?.ToString() ?? "—";
            ThisGameSubText.Text = n == null ? "waiting for a match…" : "chests searched live";
            StatusText.Text = n == null ? "Live counter on — waiting for a match..." : $"● Live: {n} chests searched this match";
        });
        live.PopupRead += text => Dispatcher.Invoke(() =>
            StatusText.Text = $"Live counter is reading the screen. Last XP popup: \u201c{text}\u201d");
        live.Start();
        _live = live;
        LiveButton.Content = "Live counter on";
        StatusText.Text = "Live counter on — play Fortnite in borderless windowed.";
        UpdateOcrStatus();
    }

    private void StopLive()
    {
        _live?.Stop();
        _live = null;
        _liveCount = null;
        if (_server != null) _server.LiveCount = null;
        _inGame?.SetLive(null);
        LiveButton.Content = "Live counter off";
    }

    // ---- in-game overlay ----
    private void InGameOverlay_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        SetInGameOverlay(InGameOverlayCheck.IsChecked == true);
    }

    private void SetInGameOverlay(bool on)
    {
        _app.InGameOverlay = on;
        AppConfig.Save(_app);
        InGameOverlayCheck.IsChecked = on;
        if (on) ShowInGame(); else HideInGame();
    }

    private void ShowInGame()
    {
        _inGame ??= CreateInGame();
        _inGame.Show();
        _inGame.SetLive(_liveCount);
        PushInGameStats();
    }

    private InGameOverlay CreateInGame()
    {
        var o = new InGameOverlay();
        if (_app.OverlayLeft is { } l && _app.OverlayTop is { } t) { o.Left = l; o.Top = t; }
        o.SetTitle(_overlay.Title);
        o.SetAccent(_overlay.Accent);
        o.SetOpacity(_overlay.Opacity);
        o.Moved += () =>
        {
            _app.OverlayLeft = o.Left;
            _app.OverlayTop = o.Top;
            AppConfig.Save(_app);
        };
        return o;
    }

    private void HideInGame() => _inGame?.Hide();

    private void PushInGameStats()
    {
        if (_inGame == null) return;
        var recs = _stats.Records.OrderBy(r => r.Time).ToList();
        _inGame.SetStats(_stats.AvgYou, recs.Count > 0 ? recs[^1].You : null);
    }

    private void ToggleInGameFromHotkey() => SetInGameOverlay(!_app.InGameOverlay);

    // ---- tray ----
    private void SetupTray()
    {
        try
        {
            _tray = new Forms.NotifyIcon
            {
                Text = "Chest Tracker",
                Visible = true,
                Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Open Chest Tracker", null, (_, _) => RestoreFromTray());
            menu.Items.Add("Quit", null, (_, _) => Close());
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (_, _) => RestoreFromTray();
        }
        catch { _tray = null; }
    }

    private void Tray_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _app.MinimizeToTray = TrayCheck.IsChecked == true;
        AppConfig.Save(_app);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    // ---- overlay opacity ----
    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _loadingSettings) return;
        _overlay.Opacity = OverlaySettings.ClampOpacity(OpacitySlider.Value / 100.0);
        OpacityValueText.Text = $"{OpacitySlider.Value:0}%";
        _inGame?.SetOpacity(_overlay.Opacity);   // the OBS page picks it up from /config.json
        _overlaySaveTimer?.Stop();
        _overlaySaveTimer?.Start();
    }

    // ---- start with Windows ----
    private void Startup_Toggled(object sender, RoutedEventArgs e)
    {
        bool want = StartupCheck.IsChecked == true;
        if (!StartupRegistration.Set(want))
        {
            StartupCheck.IsChecked = StartupRegistration.IsEnabled();   // show what Windows really has
            StatusText.Text = "Couldn't change the Start with Windows setting.";
        }
    }

    // ---- live counter language ----
    private void OcrLang_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _loadingSettings) return;
        _tracker.OcrLanguage = OcrEs.IsChecked == true ? "es" : "en";
        TrackerConfig.Save(_tracker);
        ApplyOcrSettings();
    }

    private void CustomLabel_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _loadingSettings) return;
        _tracker.CustomChestLabel = CustomLabelBox.Text.Trim();
        TrackerConfig.Save(_tracker);
        _live?.SetChestLabel(_tracker.OcrLanguage, _tracker.CustomChestLabel);
    }

    // Applies the saved language to the running counter without restarting it, so a match in
    // progress keeps its count. If the counter never started (no OCR language was installed), try again.
    private void ApplyOcrSettings()
    {
        if (_live != null)
        {
            _live.Configure(_tracker.OcrLanguage, _tracker.CustomChestLabel);
        }
        else if (!LiveButton.IsEnabled)
        {
            LiveButton.IsEnabled = true;
            LiveButton.Content = "Live counter off";
            StartLive();
        }
        UpdateOcrStatus();
    }

    private void UpdateOcrStatus()
    {
        var setup = _live?.Setup ?? LiveChestCounter.Probe(_tracker.OcrLanguage);
        string wanted = _tracker.OcrLanguage == "es" ? "Spanish" : "English";
        if (!setup.Available)
            OcrStatusText.Text = "No OCR language is installed in Windows. Add one in Settings > Time & language > Language & region.";
        else if (setup.FellBack)
            OcrStatusText.Text = $"{wanted} OCR is not installed in Windows, so {setup.Tag} is used instead. Add {wanted} (with Optical character recognition) in Settings > Time & language.";
        else
            OcrStatusText.Text = $"Reading the screen as {wanted} ({setup.Tag}).";
        OcrStatusText.Foreground = (Brush)FindResource(setup.Available && !setup.FellBack ? "Muted" : "Gem");
    }

    // ---- earlier matches ----
    // Typing only previews the estimate in the panel below. The main counter (and the overlay)
    // change when the player presses "Update counter" (or Enter).
    private bool TryParseManualTotal(out int total)
    {
        string raw = ManualTotalBox.Text.Trim().Replace(",", "").Replace(".", "").Replace(" ", "");
        if (raw.Length == 0) { total = 0; return true; }
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out total);
    }

    private void ManualTotal_Changed(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _loadingSettings) return;
        RenderEstimate();
        UpdateApplyButton();
    }

    private void UpdateApplyButton()
    {
        ApplyTotalButton.IsEnabled = TryParseManualTotal(out int n) && n != _tracker.ManualTotalMatches;
    }

    private void ManualTotal_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (ApplyTotalButton.IsEnabled) ApplyTotal_Click(sender, e);
    }

    private void ApplyTotal_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseManualTotal(out int n)) return;
        _tracker.ManualTotalMatches = n;
        TrackerConfig.Save(_tracker);

        // The estimate only counts toward the lifetime total, so show that view.
        if (n > 0 && F3.IsChecked != true) F3.IsChecked = true;

        Render();            // refreshes the Opened total on the main screen and the in-game overlay
        UpdateApplyButton();
        StatusText.Text = n > 0
            ? $"Counter updated using {n:N0} total matches."
            : "Counter updated: manual total cleared.";
    }

    // ---- overlay branding ----
    private void Accent_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _overlay.Accent = AccentBox.Text.Trim();
        UpdateSwatch();
        _inGame?.SetAccent(_overlay.Accent);
        SaveOverlay();
    }

    private void Title_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingSettings) return;
        _overlay.Title = TitleBox.Text;
        _inGame?.SetTitle(_overlay.Title);
        SaveOverlay();
    }

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) AccentBox.Text = hex; // triggers Accent_Changed
    }

    private void SaveOverlay() { try { OverlayConfig.Save(_overlay); } catch { } }

    private void UpdateSwatch()
    {
        try { AccentSwatch.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_overlay.Accent)); }
        catch { }
    }

    // Make the native window title bar dark (Windows 10/11), and register the overlay hotkeys.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        try { int on = 1; DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); } catch { } // dark title bar

        try
        {
            RegisterHotKey(hwnd, HotkeyToggle, MOD_CONTROL | MOD_SHIFT, 0x4F); // Ctrl+Shift+O
            RegisterHotKey(hwnd, HotkeyLock, MOD_CONTROL | MOD_SHIFT, 0x4C);   // Ctrl+Shift+L
            HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        }
        catch { }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyToggle) { ToggleInGameFromHotkey(); handled = true; }
            else if (id == HotkeyLock && _inGame != null) { _inGame.SetLocked(!_inGame.Locked); handled = true; }
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        try { var h = new WindowInteropHelper(this).Handle; UnregisterHotKey(h, HotkeyToggle); UnregisterHotKey(h, HotkeyLock); } catch { }
        if (_overlaySaveTimer is { IsEnabled: true }) { _overlaySaveTimer.Stop(); SaveOverlay(); }
        _live?.Stop();
        _inGame?.Close();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnClosed(e);
    }

    private int FilterIndex() => F0.IsChecked == true ? 0 : F1.IsChecked == true ? 1 : F3.IsChecked == true ? 3 : 2;

    private DateTime? SelectedSince()
    {
        return FilterIndex() switch
        {
            0 => DateTime.UtcNow.AddDays(-1),
            1 => DateTime.UtcNow.AddDays(-7),
            2 => ChestCounterService.SeasonStartUtc,
            _ => null,
        };
    }

    private void Render()
    {
        var recs = _stats.RecordsSince(SelectedSince());   // oldest-first
        int n = recs.Count;
        long total = recs.Sum(r => (long)r.You);
        double avg = n > 0 ? (double)total / n : 0;

        // The estimate for matches older than the replays belongs to the lifetime total only.
        var est = _stats.EstimatePast(_tracker.ManualTotalMatches);
        bool withEstimate = SelectedSince() == null && est.Chests > 0;

        AvgYouText.Text = avg.ToString("F1");
        YourTotalText.Text = (total + (withEstimate ? est.Chests : 0)).ToString("N0");
        YourTotalText.ToolTip = withEstimate
            ? $"{total:N0} counted from replays + about {est.Chests:N0} estimated from {est.Missing:N0} earlier matches"
            : null;
        MatchesText.Text = withEstimate ? $"incl. ~{est.Chests:N0} est."
            : n == 1 ? "in 1 match" : $"in {n:N0} matches";
        var best = n > 0 ? recs.MaxBy(r => r.You) : null;
        BestText.Text = (best?.You ?? 0).ToString();
        BestWhenText.Text = best != null ? best.Time.ToLocalTime().ToString("MMM d") : "";

        var last10 = recs.Skip(Math.Max(0, n - 10)).ToList();
        RecentFormText.Text = last10.Count > 0 ? $"last {last10.Count}: {last10.Average(r => r.You):F1} avg" : "";

        LastGameText.Text = n > 0 ? recs[^1].You.ToString() : "\u2014";
        LastMatchText.Text = n > 0 ? recs[^1].Time.ToLocalTime().ToString("MMM d, h:mm tt") : "";

        var bars = recs.Skip(Math.Max(0, n - _barCount)).ToList();
        double max = bars.Count > 0 ? Math.Max(1, bars.Max(r => r.You)) : 1;
        var bone = (Brush)FindResource("Text");
        var gem = (Brush)FindResource("Gem");
        int top = bars.Count > 0 ? bars.Max(r => r.You) : 0;
        ChartBars.ItemsSource = bars
            .Select(r => new BarRow(
                new GridLength(Math.Max(0, max * 1.16 - r.You), GridUnitType.Star),   // headroom for the number
                new GridLength(Math.Max(r.You, max * 0.012), GridUnitType.Star),
                r.You == top && top > 0 ? gem : bone,
                $"{r.You} chests, {r.Time.ToLocalTime():MMM d, h:mm tt}",
                r.You.ToString()))
            .ToList();
        ChartMetaText.Text = bars.Count > 0
            ? $"last {bars.Count} games, averaging {bars.Average(r => r.You):F1}"
            : "";

        NoDataText.Visibility = n > 0 ? Visibility.Collapsed : Visibility.Visible;
        ChartBars.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;

        RenderSquad(recs);
        RenderEstimate();
        _oddsKey = "";   // stats changed: refresh the luck column on the next tick
        PushInGameStats();
        foreach (var w in _playerWindows.Values) w.Refresh(_stats);
    }

    // Explains the estimate for matches the replays do not cover (the "Earlier matches" panel).
    // Shows the total currently typed in the box; the main counter only follows it once applied.
    private void RenderEstimate()
    {
        if (!TryParseManualTotal(out int total)) { EstimateText.Text = "Enter a whole number of matches."; return; }
        var est = _stats.EstimatePast(total);
        if (total <= 0)
            EstimateText.Text = $"Replays so far: {est.Counted:N0} matches. Enter your total to estimate the rest.";
        else if (est.Counted == 0)
            EstimateText.Text = "Scan your replays first: the estimate needs an average to work from.";
        else if (est.Missing == 0)
            EstimateText.Text = $"The replays already cover {est.Counted:N0} matches, so there is nothing to estimate.";
        else
            EstimateText.Text = $"From replays: {est.Counted:N0} matches\n"
                + $"Not covered: {est.Missing:N0} \u00d7 {est.Avg:F1} avg \u2248 {est.Chests:N0} chests (estimated)\n"
                + $"Lifetime total: {_stats.YourChests + est.Chests:N0} chests";
        if (total != _tracker.ManualTotalMatches)
            EstimateText.Text += "\nPreview only. Press Update counter to apply it.";
    }

    // Teammate counts are a floor: a chest a mate opens far from you never reaches your replay.
    private void RenderSquad(System.Collections.Generic.List<MatchRecord> recs)
    {
        var last = recs.LastOrDefault(r => r.Mates.Count > 0);
        var squad = new System.Collections.Generic.List<Row>();
        if (last != null)
        {
            squad.Add(new Row("You", last.You.ToString()));
            squad.AddRange(last.Mates.Select(m => new Row(m.Name, "\u2265 " + m.Chests, Link: true)));
            SquadTotalText.Text = $"\u2265 {last.You + last.Mates.Sum(m => m.Chests)} together";
        }
        else SquadTotalText.Text = "";
        SquadList.ItemsSource = squad;
        SquadEmptyText.Visibility = squad.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        var mates = _stats.TopMates(SelectedSince(), 4)
            .Select(t => new Row(t.Name, $"{t.Matches} {(t.Matches == 1 ? "game" : "games")}  \u00b7  \u2265 {t.Chests:N0} chests", Link: true))
            .ToList();
        MatesList.ItemsSource = mates;
        MatesEmptyText.Visibility = mates.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

        var finds = recs.SelectMany(r => r.Prizes.Select(p => (Rec: r, Prize: p))).Reverse().ToList();
        var token = finds.FirstOrDefault(f => Treasures.IsToken(f.Prize.PrizeId));
        TokenText.Text = token.Prize == null
            ? "Pirate Shop token: not seen yet"
            : $"Pirate Shop token pulled by {Finder(token.Prize)} \u00b7 {token.Rec.Time.ToLocalTime():MMM d, h:mm tt}";
        TokenText.Foreground = (Brush)FindResource(token.Prize == null ? "Text" : "Gem");

        var prizes = finds.Where(f => !Treasures.IsToken(f.Prize.PrizeId)).Take(3)
            .Select(f => new Row(Treasures.Name(f.Prize.PrizeId), $" \u2014 {Finder(f.Prize)}, {f.Rec.Time.ToLocalTime():MMM d}"))
            .ToList();
        PrizeList.ItemsSource = prizes;
        PrizeEmptyText.Visibility = prizes.Count > 0 || token.Prize != null ? Visibility.Collapsed : Visibility.Visible;
    }

    // Banner + odds table. Called every second; the table is rebuilt only when its inputs change.
    private void RenderTreasureHour()
    {
        var now = DateTime.UtcNow;
        var hour = TreasureCatalog.CurrentOrNext(now);
        bool live = hour != null && TreasureCatalog.IsLive(hour, now);

        if (hour == null)
        {
            HourTitleText.Text = "The treasure hunt has ended";
            HourBoostText.Text = "";
            HourCountdownText.Text = "";
        }
        else
        {
            var left = (live ? hour.EndUtc : hour.StartUtc) - now;
            HourCountdownText.Text = left.TotalHours >= 24
                ? $"{(int)left.TotalDays}d {left.Hours:00}:{left.Minutes:00}:{left.Seconds:00}"
                : $"{(int)left.TotalHours:00}:{left.Minutes:00}:{left.Seconds:00}";
            HourTitleText.Text = live
                ? $"Treasure Hour is live, until {hour.EndUtc.ToLocalTime():h:mm tt}"
                : $"Next Treasure Hour: {hour.StartUtc.ToLocalTime():ddd h:mm tt}";
            HourBoostText.Text = string.Join("   ", hour.Boosts.Select(b =>
                $"{TreasureCatalog.Odds.First(o => o.Kind == b.Key).Label} \u00d7{b.Value:0.#}"));
        }
        HourBorder.Background = (Brush)FindResource(live ? "Gem" : "CardAlt");
        var onBanner = (Brush)FindResource(live ? "Ink" : "Text");
        HourTitleText.Foreground = onBanner;
        HourBoostText.Foreground = onBanner;
        HourCountdownText.Foreground = onBanner;

        var week = TreasureCatalog.WeekAt(now);
        string key = $"{live}|{week?.Number}|{_stats.Records.Count}";
        if (key == _oddsKey) return;
        _oddsKey = key;

        WeekText.Text = week != null ? $"Week {week.Number} odds per chest" : "Treasure odds per chest";
        long seasonChests = _stats.Over(ChestCounterService.SeasonStartUtc).YourChests;
        var text = (Brush)FindResource("Text");
        var gem = (Brush)FindResource("Gem");
        OddsList.ItemsSource = TreasureCatalog.Odds.Select(o =>
        {
            double boost = live && hour!.Boosts.TryGetValue(o.Kind, out var b) ? b : 1;
            string item = week != null && week.Prizes.TryGetValue(o.Kind, out var name) ? name
                : o.Kind == TreasureCatalog.Token ? "Unlocks the Pirate Shop"
                : o.Kind == TreasureCatalog.Gizmo ? "Relic Finder, Lucky Locator and friends"
                : "This week's item";
            double p = TreasureCatalog.ChanceOfAtLeastOne(o.OneIn, seasonChests);
            string luck = p >= 0.995 ? ">99%" : p < 0.001 ? "<0.1%" : p < 0.1 ? $"{p * 100:0.0}%" : $"{p * 100:0}%";
            return new OddsRow(o.Label, item, $"1 in {Math.Round(o.OneIn / boost):N0}", boost > 1 ? gem : text, luck);
        }).ToList();
    }

    private static string Finder(PrizeFind p)
    {
        // A gizmo goes to the whole squad; By is whose chest it came out of.
        if (Treasures.IsGizmo(p.PrizeId))
            return p.By switch { "" => "your squad", "you" => "your squad, from your chest", _ => $"your squad, from {p.By}'s chest" };
        return p.Mine ? "you"
            : p.Team ? (string.IsNullOrEmpty(p.By) ? "your squad" : $"{p.By} (squad)")
            : string.IsNullOrEmpty(p.By) ? "a player nearby" : p.By;
    }

    // ---- player stats ----
    private void PlayerName_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Row { Link: true } row }) OpenPlayer(row.Name);
    }

    private void OpenPlayer(string name)
    {
        if (_playerWindows.TryGetValue(name, out var open))
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        var win = new PlayerWindow(name, _stats, FilterIndex()) { Owner = this };
        win.Closed += (_, _) => _playerWindows.Remove(name);
        _playerWindows[name] = win;
        win.Show();
    }

    // ---- adaptive layout ----
    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private void SideScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
        ScrollHelper.ForwardWheel((ScrollViewer)sender, e);

    // Rearranges the dashboard for the current window size (resize, maximize, snap):
    //   wide    (>= 1000 px): dashboard on the left, sidebar on the right, filling the window height.
    //   stacked (<  1000 px): one column, sidebar below; the page scrolls vertically.
    //   narrow  (<   640 px): also a smaller hero, 2-column stats and fewer chart bars.
    // It reads the ScrollViewer's own size rather than its viewport, so a scrollbar appearing
    // cannot flip the layout back and forth.
    private void ApplyLayout()
    {
        double w = PageScroll.ActualWidth, h = PageScroll.ActualHeight;
        if (w <= 0 || h <= 0) return;

        bool wide = w >= 1000;
        bool narrow = w < 640;
        bool headerInline = w >= 900;

        Root.Margin = narrow ? new Thickness(16, 14, 16, 14) : new Thickness(32, 24, 32, 20);
        double inner = w - Root.Margin.Left - Root.Margin.Right;

        // Header: the buttons drop under the title when there is no room beside it.
        Grid.SetRow(HeaderActions, headerInline ? 0 : 1);
        HeaderActions.HorizontalAlignment = headerInline ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        HeaderActions.Margin = headerInline ? new Thickness(0, 0, 0, -6) : new Thickness(0, 12, 0, -6);
        HeaderGrid.Margin = new Thickness(0, 0, 0, headerInline ? 22 : 14);
        TitleText.FontSize = narrow ? 24 : 30;

        // Body: sidebar beside the dashboard, or underneath it.
        double left;
        if (wide)
        {
            double side = Math.Clamp(w * 0.30, 340, 460);
            SideCol.Width = new GridLength(side);
            Grid.SetColumn(SideScroll, 1); Grid.SetRow(SideScroll, 0);
            SideScroll.Margin = new Thickness(0);
            SideScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            LeftGrid.Margin = new Thickness(0, 0, 16, 0);
            ChartRow.MinHeight = 180;
            Root.Height = Math.Max(740, h - Root.Margin.Top - Root.Margin.Bottom);   // fill the window, scroll if shorter
            left = inner - 16 - side;
        }
        else
        {
            SideCol.Width = new GridLength(0);
            Grid.SetColumn(SideScroll, 0); Grid.SetRow(SideScroll, 1);
            SideScroll.Margin = new Thickness(0, 16, 0, 0);
            SideScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;   // the page scrolls instead
            LeftGrid.Margin = new Thickness(0);
            ChartRow.MinHeight = narrow ? 240 : 300;
            Root.Height = double.NaN;
            left = inner;
        }

        // Hero card.
        HeroTop.Height = narrow ? 150 : 184;
        HeroChest.Width = HeroChest.Height = narrow ? 96 : 156;
        HeroChest.Margin = narrow ? new Thickness(0, 4, 16, 0) : new Thickness(0, 4, 40, 0);
        HeroText.Margin = narrow ? new Thickness(18, 14, 116, 6) : new Thickness(30, 20, 220, 10);
        ThisGameText.FontSize = narrow ? 70 : 98;

        bool twoCol = left < 560;
        HeroStats.Columns = twoCol ? 2 : 4;
        StatB3.BorderThickness = twoCol ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        StatB4.BorderThickness = twoCol ? new Thickness(1, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        if (StatB3.Child is FrameworkElement c3) c3.Margin = twoCol ? new Thickness(30, 12, 12, 12) : new Thickness(22, 12, 12, 12);

        // Squad and "most played with": side by side, or stacked.
        if (left >= 600)
        {
            Grid.SetRow(SquadPanel, 0); Grid.SetColumn(SquadPanel, 0); Grid.SetColumnSpan(SquadPanel, 1);
            SquadPanel.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetRow(MatesPanel, 0); Grid.SetColumn(MatesPanel, 1); Grid.SetColumnSpan(MatesPanel, 1);
            MatesPanel.Margin = new Thickness(8, 0, 0, 0);
        }
        else
        {
            Grid.SetRow(SquadPanel, 0); Grid.SetColumn(SquadPanel, 0); Grid.SetColumnSpan(SquadPanel, 2);
            SquadPanel.Margin = new Thickness(0);
            Grid.SetRow(MatesPanel, 1); Grid.SetColumn(MatesPanel, 0); Grid.SetColumnSpan(MatesPanel, 2);
            MatesPanel.Margin = new Thickness(0, 16, 0, 0);
        }

        int bars = narrow ? 15 : 30;
        if (bars != _barCount)
        {
            _barCount = bars;
            Render();
        }
    }

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _app.Filter = FilterIndex();
        AppConfig.Save(_app);
        Render();
    }

    // Wipes the saved matches so a scan starts from nothing. Overlay and window settings stay.
    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Delete every saved match and start over?\n\nReplays still in Fortnite's folder can be scanned again. Matches whose replay is gone are lost.",
            "Reset stats", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK) return;

        _stats = new LifetimeStats { Version = LifetimeStats.CurrentVersion };
        ChestCounterService.Save(_stats);
        Render();
        StatusText.Text = "Stats reset. Press Scan replays to read them again.";
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs e)
    {
        var files = ChestCounterService.FindReplays();
        if (files.Count == 0) { StatusText.Text = "No .replay files found."; return; }

        ScanButton.IsEnabled = false;
        _scanning = true;
        StatusText.Text = $"Scanning 0/{files.Count}...";

        var (run, stats) = await Task.Run(() => ChestCounterService.Scan(files, (done, total, name, err) =>
        {
            if (done % 3 == 0 || done == total)
                Dispatcher.Invoke(() => StatusText.Text = $"Scanning {done}/{total}...");
        }));

        _stats = stats;
        Render();
        string skipped = run.SkippedMode > 0 ? $", {run.SkippedMode} skipped (not Battle Royale)" : "";
        StatusText.Text = $"Done — {run.NewMatches} new, {run.AlreadyCounted} already counted{skipped}.";
        ScanButton.IsEnabled = true;
        _scanning = false;
    }

    private const int WM_HOTKEY = 0x0312;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_SHIFT = 0x0004;
    private const int HotkeyToggle = 9001;
    private const int HotkeyLock = 9002;

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, int mods, int vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}

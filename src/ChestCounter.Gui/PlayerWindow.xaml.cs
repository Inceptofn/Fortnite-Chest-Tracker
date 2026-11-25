using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ChestCounter.Gui;

// Chests opened per match by one teammate, across the matches you played together.
public partial class PlayerWindow : Window
{
    private sealed record BarRow(GridLength Top, GridLength Bottom, Brush Fill, string Tip, string Label);
    private sealed record GameRow(string When, string Chests, string You);
    private sealed record PrizeRow(string Name, string Value);

    private readonly string _name;
    private LifetimeStats _stats;
    private int _barCount = 40;
    private bool _wide = true;

    public PlayerWindow(string name, LifetimeStats stats, int filterIndex)
    {
        InitializeComponent();
        _name = name;
        _stats = stats;
        Title = $"{name} \u2014 Chest Tracker";
        NameText.Text = name;
        PlayerColText.Text = name;
        PrizeTitleText.Text = $"Treasures from {name}'s chests";
        (filterIndex switch { 0 => F0, 1 => F1, 3 => F3, _ => F2 }).IsChecked = true;
        Render();
    }

    // The main window calls this whenever its stats change, so an open window stays current.
    public void Refresh(LifetimeStats stats)
    {
        _stats = stats;
        Render();
    }

    private DateTime? Since() =>
        F0.IsChecked == true ? DateTime.UtcNow.AddDays(-1)
        : F1.IsChecked == true ? DateTime.UtcNow.AddDays(-7)
        : F3.IsChecked == true ? null
        : ChestCounterService.SeasonStartUtc;

    private void Filter_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) Render();
    }

    private void Render()
    {
        var since = Since();
        var games = _stats.GamesWith(_name, since);   // oldest-first
        int n = games.Count;
        long total = games.Sum(g => (long)g.Chests);
        double avg = n > 0 ? (double)total / n : 0;
        double youAvg = n > 0 ? games.Average(g => g.You) : 0;

        GamesText.Text = n.ToString("N0");
        GamesSubText.Text = n == 1 ? "game together" : "games together";
        TotalText.Text = total.ToString("N0");
        TotalSubText.Text = "at least, from your replays";
        AvgText.Text = avg.ToString("F1");
        AvgSubText.Text = n > 0 ? $"you: {youAvg:F1} in the same games" : "";

        var best = n > 0 ? games.MaxBy(g => g.Chests) : default;
        BestText.Text = n > 0 ? best.Chests.ToString() : "0";
        BestWhenText.Text = n > 0 ? best.Time.ToLocalTime().ToString("MMM d") : "";
        LastText.Text = n > 0 ? games[^1].Chests.ToString() : "\u2014";
        LastWhenText.Text = n > 0 ? games[^1].Time.ToLocalTime().ToString("MMM d, h:mm tt") : "";

        // Bar chart, same look as the main window.
        var bars = games.Skip(Math.Max(0, n - _barCount)).ToList();
        double max = bars.Count > 0 ? Math.Max(1, bars.Max(g => g.Chests)) : 1;
        int top = bars.Count > 0 ? bars.Max(g => g.Chests) : 0;
        var bone = (Brush)FindResource("Text");
        var gem = (Brush)FindResource("Gem");
        ChartBars.ItemsSource = bars
            .Select(g => new BarRow(
                new GridLength(Math.Max(0, max * 1.16 - g.Chests), GridUnitType.Star),
                new GridLength(Math.Max(g.Chests, max * 0.012), GridUnitType.Star),
                g.Chests == top && top > 0 ? gem : bone,
                $"{_name}: {g.Chests} chests (you: {g.You}), {g.Time.ToLocalTime():MMM d, h:mm tt}",
                g.Chests.ToString()))
            .ToList();
        ChartMetaText.Text = bars.Count > 0
            ? $"last {bars.Count} {(bars.Count == 1 ? "game" : "games")}, averaging {bars.Average(g => g.Chests):F1}"
            : "";
        NoDataText.Visibility = n > 0 ? Visibility.Collapsed : Visibility.Visible;
        ChartBars.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Every game, newest first.
        GameList.ItemsSource = Enumerable.Reverse(games)
            .Select(g => new GameRow(g.Time.ToLocalTime().ToString("MMM d, h:mm tt"), g.Chests.ToString(), g.You.ToString()))
            .ToList();
        ListEmptyText.Visibility = n > 0 ? Visibility.Collapsed : Visibility.Visible;

        // Treasures that came out of this player's chests.
        var finds = _stats.RecordsSince(since)
            .SelectMany(r => r.Prizes
                .Where(p => string.Equals(p.By, _name, StringComparison.Ordinal))
                .Select(p => (Rec: r, Prize: p)))
            .Reverse().ToList();
        PrizeList.ItemsSource = finds.Take(4)
            .Select(f => new PrizeRow(Treasures.Name(f.Prize.PrizeId), $" \u2014 {f.Rec.Time.ToLocalTime():MMM d}"))
            .ToList();
        PrizeEmptyText.Visibility = finds.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- layout: rearranges the page for the window's current size ----
    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        // The ScrollViewer's own size (not its viewport), so a scrollbar appearing cannot flip the layout.
        double w = PageScroll.ActualWidth, h = PageScroll.ActualHeight;
        if (w <= 0 || h <= 0) return;

        bool narrow = w < 640;
        bool wide = w >= 900;
        bool headerStacked = w < 760;

        Root.Margin = narrow ? new Thickness(16, 14, 16, 14) : new Thickness(28, 22, 28, 20);

        // Header: the period filter drops under the name on a narrow window.
        Grid.SetRow(FilterBar, headerStacked ? 1 : 0);
        FilterBar.HorizontalAlignment = headerStacked ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        FilterBar.Margin = headerStacked ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        TitleStack.Margin = headerStacked ? new Thickness(0) : new Thickness(0, 0, 330, 0);
        NameText.FontSize = narrow ? 26 : 34;

        StatsGrid.Columns = w >= 860 ? 5 : w >= 560 ? 3 : 2;

        if (wide)
        {
            ListCol.Width = new GridLength(Math.Clamp(w * 0.30, 320, 420));
            Grid.SetColumn(ListPanel, 1); Grid.SetRow(ListPanel, 0);
            ListPanel.Margin = new Thickness(16, 0, 0, 0);
            ListScroll.MaxHeight = double.PositiveInfinity;
            ChartPanel.MinHeight = 220;
            Root.Height = Math.Max(520, h - Root.Margin.Top - Root.Margin.Bottom);   // chart and list fill the window
        }
        else
        {
            ListCol.Width = new GridLength(0);
            Grid.SetColumn(ListPanel, 0); Grid.SetRow(ListPanel, 1);
            ListPanel.Margin = new Thickness(0, 16, 0, 0);
            ListScroll.MaxHeight = 340;
            ChartPanel.MinHeight = narrow ? 240 : 300;
            Root.Height = double.NaN;   // natural height; the page scrolls
        }

        int bars = narrow ? 18 : 40;
        if (bars != _barCount || wide != _wide)
        {
            _barCount = bars;
            _wide = wide;
            Render();
        }
    }

    private void ListScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
        ScrollHelper.ForwardWheel((ScrollViewer)sender, e);

    // Dark native title bar, like the main window.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int)); } catch { }
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
}

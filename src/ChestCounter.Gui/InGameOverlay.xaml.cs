using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ChestCounter.Gui;

// Transparent, always-on-top card that sits over Fortnite (borderless windowed) and shows the
// live chest count. Drag to move; lock it to click through to the game.
public partial class InGameOverlay : Window
{
    public bool Locked { get; private set; }
    public event Action? Moved;

    public InGameOverlay()
    {
        InitializeComponent();
        var area = SystemParameters.WorkArea;
        Left = area.Left + area.Width / 2 - 130;   // top-centre by default
        Top = area.Top + 24;
    }

    public void SetLive(int? n) => LiveText.Text = n?.ToString() ?? "—";

    public void SetStats(double avg, int? last)
    {
        AvgText.Text = avg.ToString("F1");
        LastText.Text = last?.ToString() ?? "—";
    }

    public void SetTitle(string title) => TitleText.Text = string.IsNullOrWhiteSpace(title) ? "Chest Tracker" : title;

    public void SetAccent(string hex)
    {
        try { LiveText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (Locked) return;
        try { DragMove(); Moved?.Invoke(); } catch { }
    }

    // Lock = pass clicks through to the game and hide the hint line.
    public void SetLocked(bool locked)
    {
        Locked = locked;
        HintText.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex = locked ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Keep the overlay out of Alt-Tab and app switchers.
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ChestCounter.Gui;

internal static class ScrollHelper
{
    // A ScrollViewer nested inside the page's ScrollViewer swallows the mouse wheel even when it
    // has nothing left to scroll, which makes the page feel stuck. Hand the wheel to the parent
    // whenever the inner one is not scrollable or is already at its edge.
    public static void ForwardWheel(ScrollViewer inner, MouseWheelEventArgs e)
    {
        bool atEdge = inner.ScrollableHeight <= 0
            || (e.Delta > 0 && inner.VerticalOffset <= 0)
            || (e.Delta < 0 && inner.VerticalOffset >= inner.ScrollableHeight);
        if (!atEdge || inner.Parent is not UIElement parent) return;

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = inner,
        });
    }
}

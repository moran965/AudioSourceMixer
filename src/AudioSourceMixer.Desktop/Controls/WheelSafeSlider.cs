using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AudioSourceMixer.Desktop.Controls;

public class WheelSafeSlider : Slider
{
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (IsKeyboardFocusWithin)
        {
            base.OnPreviewMouseWheel(e);
            return;
        }

        var scrollViewer = FindAncestor<ScrollViewer>(this);
        if (scrollViewer is null)
        {
            base.OnPreviewMouseWheel(e);
            return;
        }

        e.Handled = true;
        scrollViewer.ScrollToVerticalOffset(Math.Max(0, scrollViewer.VerticalOffset - e.Delta));
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        for (var parent = VisualTreeHelper.GetParent(current); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is T match) return match;
        return null;
    }
}

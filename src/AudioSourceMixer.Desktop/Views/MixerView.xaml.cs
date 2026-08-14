using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using AudioSourceMixer.Desktop.ViewModels;

namespace AudioSourceMixer.Desktop.Views;

public partial class MixerView : System.Windows.Controls.UserControl
{
    public MixerView() => InitializeComponent();
    internal ItemsControl SourceItems => SourcesItemsControl;

    private void OpenHeaderMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void ToggleHiddenSourcesPopup(object sender, RoutedEventArgs e)
        => HiddenSourcesPopup.IsOpen = !HiddenSourcesPopup.IsOpen;

    private void SourcesDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(AudioSourceViewModel)) ||
            DataContext is not MainViewModel { IsManualSortMode: true })
        {
            e.Effects = System.Windows.DragDropEffects.None;
            return;
        }
        e.Effects = System.Windows.DragDropEffects.Move;
        if (FindDescendant<ScrollViewer>(SourcesItemsControl) is { } scroll)
        {
            var point = e.GetPosition(SourcesItemsControl);
            if (point.Y < 32) scroll.LineUp();
            else if (point.Y > SourcesItemsControl.ActualHeight - 32) scroll.LineDown();
        }
        e.Handled = true;
    }

    private void SourcesDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel ||
            e.Data.GetData(typeof(AudioSourceViewModel)) is not AudioSourceViewModel source ||
            FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { DataContext: AudioSourceViewModel target }) return;
        viewModel.MoveSourceBefore(source, target);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } descendant) return descendant;
        }
        return null;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Desktop.ViewModels;

namespace AudioSourceMixer.Desktop.Controls;

public partial class AudioSourceCard : System.Windows.Controls.UserControl
{
    private System.Windows.Point? _dragStart;

    public AudioSourceCard() => InitializeComponent();

    private void OutputDeviceItemClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox comboBox || comboBox.DataContext is not AudioSourceViewModel source ||
            e.OriginalSource is not DependencyObject origin ||
            ItemsControl.ContainerFromElement(comboBox, origin) is not ComboBoxItem item ||
            item.Content is not OutputDeviceInfo device) return;
        _ = source.UserSelectOutputDeviceAsync(device);
    }

    private void OutputDeviceKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not System.Windows.Controls.ComboBox comboBox ||
            comboBox.DataContext is not AudioSourceViewModel source ||
            comboBox.SelectedItem is not OutputDeviceInfo device) return;
        _ = source.UserSelectOutputDeviceAsync(device);
        comboBox.IsDropDownOpen = false;
        e.Handled = true;
    }

    private void OpenSourceMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void DragHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AudioSourceViewModel { ManualDragEnabled: true }) return;
        _dragStart = e.GetPosition(this);
        e.Handled = true;
    }

    private void DragHandleMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed ||
            DataContext is not AudioSourceViewModel { ManualDragEnabled: true } source) return;
        var current = e.GetPosition(this);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        System.Windows.DragDrop.DoDragDrop(DragHandle, source, System.Windows.DragDropEffects.Move);
        e.Handled = true;
    }

    private void DragHandleMouseUp(object sender, MouseButtonEventArgs e) => _dragStart = null;
}

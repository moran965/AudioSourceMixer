using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Desktop.ViewModels;
using AudioSourceMixer.Desktop.Views;

namespace AudioSourceMixer.Desktop.Controls;

public partial class AudioSourceCard : System.Windows.Controls.UserControl
{
    private System.Windows.Point? _dragStart;
    private System.Windows.Point? _sourceMenuAnchor;

    public AudioSourceCard() => InitializeComponent();
    internal FrameworkElement DragVisual => CardRoot;
    internal bool IsSourceMenuOpen => SourceMenuPopup.IsOpen;

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
        SourceMenuPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (SourceMenuPopup.IsOpen) _sourceMenuAnchor = SourceMenuButton.PointToScreen(new System.Windows.Point());
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    internal void CloseSourceMenu()
    {
        SourceMenuPopup.IsOpen = false;
        _sourceMenuAnchor = null;
    }

    private void SourceMenuPopupClosed(object? sender, EventArgs e) => _sourceMenuAnchor = null;

    private void AudioSourceCardLayoutUpdated(object? sender, EventArgs e)
    {
        if (!SourceMenuPopup.IsOpen || _sourceMenuAnchor is not { } anchor) return;
        var current = SourceMenuButton.PointToScreen(new System.Windows.Point());
        if (Math.Abs(current.X - anchor.X) > 1 || Math.Abs(current.Y - anchor.Y) > 1) CloseSourceMenu();
    }

    private void AudioSourceCardIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) CloseSourceMenu();
    }

    private void AudioSourceCardUnloaded(object sender, RoutedEventArgs e)
    {
        _dragStart = null;
        CloseSourceMenu();
    }

    private void DragHandleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not AudioSourceViewModel) return;
        _dragStart = e.GetPosition(CardRoot);
        e.Handled = true;
    }

    private void DragHandleMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed ||
            DataContext is not AudioSourceViewModel source) return;
        var current = e.GetPosition(CardRoot);
        if (Math.Abs(current.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _dragStart = null;
        CloseSourceMenu();
        var mixer = FindAncestor<MixerView>(this);
        if (mixer is null || !mixer.BeginSourceDrag(source, CardRoot, start)) return;
        try { _ = System.Windows.DragDrop.DoDragDrop(DragHandle, source, System.Windows.DragDropEffects.Move); }
        finally { mixer.CancelSourceDrag(); _dragStart = null; }
        e.Handled = true;
    }

    private void DragHandleQueryContinueDrag(object sender, System.Windows.QueryContinueDragEventArgs e)
    {
        if (!e.EscapePressed) return;
        FindAncestor<MixerView>(this)?.CancelSourceDrag();
        e.Action = System.Windows.DragAction.Cancel;
        e.Handled = true;
    }

    private void DragHandleMouseUp(object sender, MouseButtonEventArgs e) => _dragStart = null;
    private void DragHandleLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e) => _dragStart = null;

    private static T? FindAncestor<T>(DependencyObject origin) where T : DependencyObject
    {
        for (var current = origin; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is T match) return match;
        return null;
    }
}

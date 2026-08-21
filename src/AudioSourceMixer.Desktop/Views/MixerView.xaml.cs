using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AudioSourceMixer.Desktop.ViewModels;

namespace AudioSourceMixer.Desktop.Views;

public partial class MixerView : System.Windows.Controls.UserControl
{
    public MixerView() => InitializeComponent();
    internal ItemsControl SourceItems => SourcesItemsControl;
    internal ScrollViewer SourceScroller => SourceScrollViewer;

    private void OpenHeaderMenu(object sender, RoutedEventArgs e)
    {
        OrderMenuPopup.IsOpen = true;
    }

    private void ToggleHiddenSourcesPopup(object sender, RoutedEventArgs e)
        => HiddenSourcesPopup.IsOpen = !HiddenSourcesPopup.IsOpen;

    private void SourcesDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(AudioSourceViewModel)) || DataContext is not MainViewModel)
        {
            e.Effects = System.Windows.DragDropEffects.None;
            return;
        }
        e.Effects = System.Windows.DragDropEffects.Move;
        var point = e.GetPosition(SourceScrollViewer);
        if (point.Y < 40) SourceScrollViewer.ScrollToVerticalOffset(Math.Max(0, SourceScrollViewer.VerticalOffset - 18));
        else if (point.Y > SourceScrollViewer.ViewportHeight - 40)
            SourceScrollViewer.ScrollToVerticalOffset(SourceScrollViewer.VerticalOffset + 18);
        ShowInsertionLine(CalculateInsertionIndex(point.Y));
        e.Handled = true;
    }

    private void SourcesDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel ||
            e.Data.GetData(typeof(AudioSourceViewModel)) is not AudioSourceViewModel source) return;
        viewModel.MoveSourceToInsertionIndex(source, CalculateInsertionIndex(e.GetPosition(SourceScrollViewer).Y));
        HideInsertionLine();
        e.Handled = true;
    }

    private void SourcesDragLeave(object sender, System.Windows.DragEventArgs e) => HideInsertionLine();

    private int CalculateInsertionIndex(double pointerY)
    {
        var realized = new List<RealizedItemBounds>();
        for (var index = 0; index < SourcesItemsControl.Items.Count; index++)
        {
            if (SourcesItemsControl.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container ||
                !container.IsVisible || container.ActualHeight <= 0) continue;
            var top = container.TranslatePoint(new System.Windows.Point(0, 0), SourceScrollViewer).Y;
            realized.Add(new RealizedItemBounds(index, top, container.ActualHeight));
        }
        return SourceDropIndexCalculator.Calculate(realized, pointerY, SourcesItemsControl.Items.Count);
    }

    private void ShowInsertionLine(int insertionIndex)
    {
        var y = SourceScrollViewer.ViewportHeight;
        if (SourcesItemsControl.Items.Count > 0)
        {
            var targetIndex = Math.Min(insertionIndex, SourcesItemsControl.Items.Count - 1);
            if (SourcesItemsControl.ItemContainerGenerator.ContainerFromIndex(targetIndex) is FrameworkElement target)
            {
                y = target.TranslatePoint(new System.Windows.Point(0, 0), SourceScrollViewer).Y;
                if (insertionIndex > targetIndex) y += target.ActualHeight;
            }
        }
        InsertionLineTransform.Y = Math.Clamp(y - 1, 0, Math.Max(0, SourceScrollViewer.ActualHeight - 2));
        InsertionLine.Visibility = Visibility.Visible;
        InsertionLine.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(120)));
    }

    private void HideInsertionLine()
    {
        InsertionLine.BeginAnimation(OpacityProperty, null);
        InsertionLine.Visibility = Visibility.Collapsed;
    }

}

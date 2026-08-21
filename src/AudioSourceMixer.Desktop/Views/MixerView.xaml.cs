using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AudioSourceMixer.Desktop.Controls;
using AudioSourceMixer.Desktop.ViewModels;
using Button = System.Windows.Controls.Button;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using UserControl = System.Windows.Controls.UserControl;

namespace AudioSourceMixer.Desktop.Views;

public partial class MixerView : UserControl
{
    private const double CrossingHysteresis = 8;
    private static readonly Duration FlipDuration = new(TimeSpan.FromMilliseconds(150));
    private readonly Dictionary<FrameworkElement, TranslateTransform> _flipTransforms = [];
    private SourceDragPreviewCoordinator<AudioSourceViewModel>? _drag;
    private SessionDragAdorner? _dragAdorner;
    private AdornerLayer? _adornerLayer;
    private AudioSourceViewModel? _draggedSource;
    private Point _latestDragPoint;
    private Point _grabOffset;
    private bool _hasDragPoint;
    private int? _shownInsertionIndex;
    private TimeSpan _lastRenderingTime;
    private Window? _hostWindow;
    private Point? _hiddenPopupAnchor;
    private Point? _orderPopupAnchor;
    private int _insertionFadeStartCount;
    private int _flipAnimationStartCount;

    public MixerView() => InitializeComponent();

    internal ItemsControl SourceItems => SourcesItemsControl;
    internal ScrollViewer SourceScroller => SourceScrollViewer;
    internal Button SortButton => SortMenuButton;
    internal Button HiddenButton => HiddenSourcesButton;
    internal FrameworkElement BrowserStatusElement => BrowserStatusPanel;
    internal bool HiddenPopupIsOpen => HiddenSourcesPopup.IsOpen;
    internal bool HiddenPopupChildIsVisible => HiddenSourcesPopup.IsOpen && HiddenSourcesPopup.Child is UIElement child &&
        child.IsVisible && PresentationSource.FromVisual(child) is not null;
    internal bool HiddenPopupChildHasVisibleRoot => HiddenSourcesPopup.Child is Visual child &&
        PresentationSource.FromVisual(child) is HwndSource source && IsWindowVisible(source.Handle);
    internal bool HasActiveDragPreview => _dragAdorner is not null;
    internal Size ActiveDragPreviewSize => _dragAdorner?.PreviewSize ?? Size.Empty;
    internal int ActiveFlipTransformCount => _flipTransforms.Count;
    internal int FlipAnimationStartCount => _flipAnimationStartCount;
    internal int InsertionFadeStartCount => _insertionFadeStartCount;
    internal bool IsInsertionLineVisible => InsertionLine.Visibility == Visibility.Visible;

    private void MixerViewLoaded(object sender, RoutedEventArgs e)
    {
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
        {
            _hostWindow.Deactivated += HostWindowDeactivated;
            _hostWindow.Closing += HostWindowClosing;
            _hostWindow.LocationChanged += HostWindowLocationChanged;
            _hostWindow.StateChanged += HostWindowStateChanged;
        }
        LayoutUpdated += MixerViewLayoutUpdated;
    }

    private void MixerViewUnloaded(object sender, RoutedEventArgs e)
    {
        CancelSourceDrag();
        CloseTransientPopups();
        LayoutUpdated -= MixerViewLayoutUpdated;
        if (_hostWindow is not null)
        {
            _hostWindow.Deactivated -= HostWindowDeactivated;
            _hostWindow.Closing -= HostWindowClosing;
            _hostWindow.LocationChanged -= HostWindowLocationChanged;
            _hostWindow.StateChanged -= HostWindowStateChanged;
            _hostWindow = null;
        }
    }

    private void MixerViewIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true) return;
        CancelSourceDrag();
        CloseTransientPopups();
    }

    private void HostWindowDeactivated(object? sender, EventArgs e)
    {
        CancelSourceDrag();
        CloseTransientPopups();
    }

    private void HostWindowClosing(object? sender, CancelEventArgs e) => CloseTransientPopups();
    private void HostWindowLocationChanged(object? sender, EventArgs e) => CloseTransientPopups();
    private void HostWindowStateChanged(object? sender, EventArgs e) => CloseTransientPopups();

    private void OpenHeaderMenu(object sender, RoutedEventArgs e)
    {
        CloseHiddenSourcesPopup();
        CloseSourceMenus();
        OrderMenuPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() =>
        {
            if (OrderMenuPopup.IsOpen) _orderPopupAnchor = SortMenuButton.PointToScreen(new Point());
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OrderMenuPopupClosed(object? sender, EventArgs e) => _orderPopupAnchor = null;

    private void ToggleHiddenSourcesPopup(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || viewModel.HiddenSources.Count == 0) return;
        OrderMenuPopup.IsOpen = false;
        CloseSourceMenus();
        viewModel.IsHiddenSourcesPopupOpen = !viewModel.IsHiddenSourcesPopupOpen;
        if (viewModel.IsHiddenSourcesPopupOpen)
            Dispatcher.BeginInvoke(() => _hiddenPopupAnchor = HiddenSourcesButton.PointToScreen(new Point()),
                System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void HiddenSourcesButtonIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is false) CloseHiddenSourcesPopup();
    }

    private void MixerViewLayoutUpdated(object? sender, EventArgs e)
    {
        if (HiddenSourcesPopup.IsOpen && _hiddenPopupAnchor is { } hiddenAnchor)
        {
            if (!HiddenSourcesButton.IsVisible || HasMoved(HiddenSourcesButton, hiddenAnchor)) CloseHiddenSourcesPopup();
        }
        if (OrderMenuPopup.IsOpen && _orderPopupAnchor is { } orderAnchor)
        {
            if (!SortMenuButton.IsVisible || HasMoved(SortMenuButton, orderAnchor)) OrderMenuPopup.IsOpen = false;
        }
    }

    private static bool HasMoved(FrameworkElement target, Point anchor)
    {
        var current = target.PointToScreen(new Point());
        return Math.Abs(current.X - anchor.X) > 1 || Math.Abs(current.Y - anchor.Y) > 1;
    }

    private void HiddenSourcesPopupClosed(object? sender, EventArgs e)
    {
        _hiddenPopupAnchor = null;
        if (DataContext is MainViewModel viewModel) viewModel.IsHiddenSourcesPopupOpen = false;

        if (HiddenSourcesPopup.Child is DependencyObject child)
        {
            if (Mouse.Captured is DependencyObject captured && IsDescendant(child, captured)) Mouse.Capture(null);
            if (Keyboard.FocusedElement is DependencyObject focused && IsDescendant(child, focused)) Keyboard.ClearFocus();
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (_hostWindow?.IsActive != true || !IsVisible) return;
            if (HiddenSourcesButton.IsVisible) HiddenSourcesButton.Focus();
            else SortMenuButton.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    internal bool BeginSourceDrag(AudioSourceViewModel source, FrameworkElement card, Point grabOffset)
    {
        if (DataContext is not MainViewModel viewModel || !viewModel.Sources.Contains(source) || card.ActualWidth <= 0 || card.ActualHeight <= 0)
            return false;

        CancelSourceDrag();
        CloseTransientPopups();
        try
        {
            _drag = new SourceDragPreviewCoordinator<AudioSourceViewModel>(viewModel.Sources, source);
            viewModel.BeginSourceOrderPreview();
            _draggedSource = source;
            _grabOffset = grabOffset;
            _latestDragPoint = card.TranslatePoint(grabOffset, SourceScrollViewer);
            _hasDragPoint = true;
            _adornerLayer = AdornerLayer.GetAdornerLayer(SourceScrollViewer)
                ?? throw new InvalidOperationException("No AdornerLayer is available for the source list.");
            _dragAdorner = new SessionDragAdorner(SourceScrollViewer, card, _latestDragPoint, _grabOffset);
            _adornerLayer.Add(_dragAdorner);
            _draggedSource.SetDragPlaceholder(true);
            _lastRenderingTime = TimeSpan.Zero;
            CompositionTarget.Rendering += CompositionTargetRendering;
            ShowInsertionLine(_drag.CurrentIndex);
            return true;
        }
        catch
        {
            CancelSourceDrag();
            throw;
        }
    }

    internal void CancelSourceDrag()
    {
        if (_drag is not null && !_drag.IsCompleted) _drag.Cancel();
        if (DataContext is MainViewModel viewModel) viewModel.CancelSourceOrderPreview();
        CleanupDragVisuals();
    }

    internal void ProcessDragPointForDiagnostics(Point point)
    {
        _latestDragPoint = point;
        _hasDragPoint = true;
        ProcessDragFrame(TimeSpan.FromMilliseconds(16));
    }

    internal void CommitSourceDragForDiagnostics()
    {
        if (_drag is null) return;
        var changed = _drag.Commit();
        if (DataContext is MainViewModel viewModel) viewModel.CommitSourceOrderPreview(changed);
        CleanupDragVisuals();
    }

    private void SourcesDragEnter(object sender, DragEventArgs e) => UpdateDragPointer(e);
    private void SourcesDragOver(object sender, DragEventArgs e) => UpdateDragPointer(e);

    private void UpdateDragPointer(DragEventArgs e)
    {
        if (_drag is null || !e.Data.GetDataPresent(typeof(AudioSourceViewModel)))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        _latestDragPoint = e.GetPosition(SourceScrollViewer);
        _hasDragPoint = true;
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void SourcesDrop(object sender, DragEventArgs e)
    {
        if (_drag is null || e.Data.GetData(typeof(AudioSourceViewModel)) is not AudioSourceViewModel source ||
            !ReferenceEquals(source, _drag.Source))
        {
            e.Effects = DragDropEffects.None;
            CancelSourceDrag();
            return;
        }

        try
        {
            _latestDragPoint = e.GetPosition(SourceScrollViewer);
            _hasDragPoint = true;
            ProcessDragFrame(TimeSpan.FromMilliseconds(16));
            var changed = _drag.Commit();
            if (DataContext is MainViewModel viewModel) viewModel.CommitSourceOrderPreview(changed);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            CleanupDragVisuals();
        }
        catch
        {
            CancelSourceDrag();
            throw;
        }
    }

    private void SourcesDragLeave(object sender, DragEventArgs e)
    {
        // Keep the retained preview alive across scrollbar/window-edge transitions. DoDragDrop reports
        // cancellation to the source, whose finally block performs the deterministic rollback.
    }

    private void CompositionTargetRendering(object? sender, EventArgs e)
    {
        if (_drag is null || !_hasDragPoint) return;
        var renderingTime = e is RenderingEventArgs rendering ? rendering.RenderingTime : TimeSpan.Zero;
        var elapsed = _lastRenderingTime == TimeSpan.Zero ? TimeSpan.FromMilliseconds(16) : renderingTime - _lastRenderingTime;
        _lastRenderingTime = renderingTime;
        ProcessDragFrame(elapsed <= TimeSpan.Zero || elapsed > TimeSpan.FromMilliseconds(50)
            ? TimeSpan.FromMilliseconds(16) : elapsed);
    }

    private void ProcessDragFrame(TimeSpan elapsed)
    {
        if (_drag is null || _dragAdorner is null) return;
        if (_drag.CurrentIndex < 0)
        {
            CancelSourceDrag();
            return;
        }
        _dragAdorner.Update(_latestDragPoint, _grabOffset);
        AutoScroll(elapsed);
        SourceScrollViewer.UpdateLayout();

        var target = CalculatePreviewTarget(_latestDragPoint.Y, _drag.Source);
        if (target is not null && target.Value.Index != _drag.CurrentIndex)
        {
            var oldPositions = CaptureVisiblePositions();
            PrepareFlipForNextMove();
            if (_drag.TryMoveTo(target.Value.Index, _latestDragPoint.Y, target.Value.CrossingMidpoint, CrossingHysteresis))
            {
                SourcesItemsControl.UpdateLayout();
                AnimateLiveMove(oldPositions, _drag.Source);
            }
        }
        ShowInsertionLine(_drag.CurrentIndex);
    }

    private (int Index, double CrossingMidpoint)? CalculatePreviewTarget(double pointerY, AudioSourceViewModel source)
    {
        var currentIndex = SourcesItemsControl.Items.IndexOf(source);
        if (currentIndex < 0) return null;
        var realized = new List<(int Index, double Midpoint)>();
        for (var index = 0; index < SourcesItemsControl.Items.Count; index++)
        {
            if (index == currentIndex || SourcesItemsControl.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container ||
                !container.IsVisible || container.ActualHeight <= 0) continue;
            var top = container.TranslatePoint(new Point(), SourceScrollViewer).Y;
            realized.Add((index, top + container.ActualHeight / 2));
        }
        if (realized.Count == 0) return null;

        var hasFirstAfter = false;
        var firstAfter = default((int Index, double Midpoint));
        foreach (var item in realized)
        {
            if (pointerY >= item.Midpoint) continue;
            firstAfter = item;
            hasFirstAfter = true;
            break;
        }
        int insertionInCurrent;
        double boundary;
        if (hasFirstAfter)
        {
            insertionInCurrent = firstAfter.Index;
            boundary = firstAfter.Midpoint;
        }
        else
        {
            var last = realized[^1];
            insertionInCurrent = last.Index + 1;
            boundary = last.Midpoint;
        }

        var targetIndex = insertionInCurrent - (currentIndex < insertionInCurrent ? 1 : 0);
        targetIndex = Math.Clamp(targetIndex, 0, SourcesItemsControl.Items.Count - 1);
        if (targetIndex != currentIndex)
        {
            var crossed = realized.FirstOrDefault(item => item.Index == targetIndex);
            if (crossed != default) boundary = crossed.Midpoint;
        }
        return (targetIndex, boundary);
    }

    private Dictionary<AudioSourceViewModel, double> CaptureVisiblePositions()
    {
        var positions = new Dictionary<AudioSourceViewModel, double>();
        foreach (var source in SourcesItemsControl.Items.OfType<AudioSourceViewModel>())
        {
            if (SourcesItemsControl.ItemContainerGenerator.ContainerFromItem(source) is not FrameworkElement container || !container.IsVisible) continue;
            positions[source] = container.TranslatePoint(new Point(), SourceScrollViewer).Y;
        }
        return positions;
    }

    private void PrepareFlipForNextMove()
    {
        foreach (var (container, transform) in _flipTransforms.ToArray())
        {
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            if (ReferenceEquals(container.RenderTransform, transform)) container.RenderTransform = null;
        }
        _flipTransforms.Clear();
    }

    private void AnimateLiveMove(IReadOnlyDictionary<AudioSourceViewModel, double> oldPositions, AudioSourceViewModel dragged)
    {
        foreach (var (source, oldY) in oldPositions)
        {
            if (ReferenceEquals(source, dragged) || SourcesItemsControl.ItemContainerGenerator.ContainerFromItem(source) is not FrameworkElement container)
                continue;
            var newY = container.TranslatePoint(new Point(), SourceScrollViewer).Y;
            var delta = oldY - newY;
            if (Math.Abs(delta) < 0.5) continue;
            var transform = new TranslateTransform(0, delta);
            container.RenderTransform = transform;
            _flipTransforms[container] = transform;
            _flipAnimationStartCount++;
            var animation = new DoubleAnimation(delta, 0, FlipDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            animation.Completed += (_, _) =>
            {
                if (!ReferenceEquals(container.RenderTransform, transform)) return;
                transform.BeginAnimation(TranslateTransform.YProperty, null);
                container.RenderTransform = null;
                _flipTransforms.Remove(container);
            };
            transform.BeginAnimation(TranslateTransform.YProperty, animation, HandoffBehavior.SnapshotAndReplace);
        }
    }

    private void AutoScroll(TimeSpan elapsed)
    {
        const double edge = 52;
        var viewport = SourceScrollViewer.ViewportHeight;
        if (viewport <= 0) return;
        var direction = 0d;
        if (_latestDragPoint.Y < edge) direction = -Math.Clamp((edge - _latestDragPoint.Y) / edge, 0, 1);
        else if (_latestDragPoint.Y > viewport - edge)
            direction = Math.Clamp((_latestDragPoint.Y - (viewport - edge)) / edge, 0, 1);
        if (direction == 0) return;
        var offset = SourceScrollViewer.VerticalOffset + direction * 620 * elapsed.TotalSeconds;
        SourceScrollViewer.ScrollToVerticalOffset(Math.Clamp(offset, 0, SourceScrollViewer.ScrollableHeight));
    }

    private void ShowInsertionLine(int insertionIndex)
    {
        var y = SourceScrollViewer.ViewportHeight;
        if (insertionIndex >= 0 && insertionIndex < SourcesItemsControl.Items.Count &&
            SourcesItemsControl.ItemContainerGenerator.Status == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
        {
            try
            {
                if (SourcesItemsControl.ItemContainerGenerator.ContainerFromIndex(insertionIndex) is FrameworkElement target)
                    y = target.TranslatePoint(new Point(), SourceScrollViewer).Y;
            }
            catch (IndexOutOfRangeException)
            {
                // A virtualizing generator can briefly invalidate its realized block during a Move.
                // The next rendering frame will position the retained line from the stable generator.
            }
        }
        InsertionLineTransform.Y = Math.Clamp(y - 1, 0, Math.Max(0, SourceScrollViewer.ActualHeight - 2));

        if (InsertionLine.Visibility != Visibility.Visible)
        {
            InsertionLine.Visibility = Visibility.Visible;
            _insertionFadeStartCount++;
            InsertionLine.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(120)), HandoffBehavior.SnapshotAndReplace);
        }
        _shownInsertionIndex = insertionIndex;
    }

    private void CleanupDragVisuals()
    {
        CompositionTarget.Rendering -= CompositionTargetRendering;
        PrepareFlipForNextMove();
        if (_dragAdorner is not null && _adornerLayer is not null) _adornerLayer.Remove(_dragAdorner);
        _draggedSource?.SetDragPlaceholder(false);
        _drag = null;
        _dragAdorner = null;
        _adornerLayer = null;
        _draggedSource = null;
        _hasDragPoint = false;
        _shownInsertionIndex = null;
        _lastRenderingTime = TimeSpan.Zero;
        Mouse.Capture(null);
        InsertionLine.BeginAnimation(OpacityProperty, null);
        InsertionLine.Visibility = Visibility.Collapsed;
    }

    private void CloseTransientPopups()
    {
        OrderMenuPopup.IsOpen = false;
        _orderPopupAnchor = null;
        CloseHiddenSourcesPopup();
        CloseSourceMenus();
    }

    private void CloseHiddenSourcesPopup()
    {
        if (DataContext is MainViewModel viewModel) viewModel.IsHiddenSourcesPopupOpen = false;
        HiddenSourcesPopup.IsOpen = false;
        _hiddenPopupAnchor = null;
    }

    private void CloseSourceMenus()
    {
        foreach (var card in Descendants(SourcesItemsControl).OfType<AudioSourceCard>()) card.CloseSourceMenu();
    }

    private static bool IsDescendant(DependencyObject ancestor, DependencyObject candidate)
    {
        for (var current = candidate; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);
}

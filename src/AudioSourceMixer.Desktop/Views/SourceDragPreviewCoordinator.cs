using System.Collections.ObjectModel;
using AudioSourceMixer.Desktop.ViewModels;

namespace AudioSourceMixer.Desktop.Views;

/// <summary>
/// Owns the reversible, in-memory ordering used while a source card is being dragged.
/// Persistence is deliberately left to the caller and can only be requested once by Commit.
/// </summary>
internal sealed class SourceDragPreviewCoordinator<T> where T : class
{
    private readonly ObservableCollection<T> _items;
    private readonly T[] _originalOrder;
    private bool _completed;

    public SourceDragPreviewCoordinator(ObservableCollection<T> items, T source)
    {
        _items = items;
        Source = source;
        OriginalIndex = items.IndexOf(source);
        if (OriginalIndex < 0) throw new ArgumentException("The dragged item must belong to the collection.", nameof(source));
        _originalOrder = items.ToArray();
    }

    public T Source { get; }
    public int OriginalIndex { get; }
    public int CurrentIndex => _items.IndexOf(Source);
    public int PreviewMoveCount { get; private set; }
    public bool HasChanged => CurrentIndex != OriginalIndex;
    public bool IsCompleted => _completed;

    public bool TryMoveTo(int targetIndex, double pointerY, double crossingMidpointY, double hysteresis)
    {
        if (_completed || _items.Count == 0) return false;
        var currentIndex = CurrentIndex;
        if (currentIndex < 0) return false;
        targetIndex = Math.Clamp(targetIndex, 0, _items.Count - 1);
        if (targetIndex == currentIndex) return false;

        if (targetIndex > currentIndex && pointerY < crossingMidpointY + hysteresis) return false;
        if (targetIndex < currentIndex && pointerY > crossingMidpointY - hysteresis) return false;

        _items.Move(currentIndex, targetIndex);
        PreviewMoveCount++;
        return true;
    }

    public bool Commit(Action? saveOnce = null)
    {
        if (_completed) return false;
        _completed = true;
        if (!HasChanged) return false;
        saveOnce?.Invoke();
        return true;
    }

    public void Cancel()
    {
        if (_completed) return;
        _completed = true;

        // Restore only items which are still live. Items discovered during the drag remain at the end,
        // and an item removed by session discovery is never resurrected.
        IEqualityComparer<T> comparer = ReferenceEqualityComparer.Instance;
        var target = _originalOrder.Where(item => _items.Contains(item, comparer))
            .Concat(_items.Where(item => !_originalOrder.Contains(item, comparer))).ToArray();
        SourceCollectionReconciler.Reorder(_items, target, item => item, comparer);
    }
}

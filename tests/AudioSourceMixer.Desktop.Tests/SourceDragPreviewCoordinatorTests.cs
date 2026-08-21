using System.Collections.ObjectModel;
using AudioSourceMixer.Desktop.Views;

namespace AudioSourceMixer.Desktop.Tests;

public sealed class SourceDragPreviewCoordinatorTests
{
    [Fact]
    public void DownwardCrossingMovesOnceAndSameTargetDoesNotRepeat()
    {
        var (items, a, b, c) = Items();
        var drag = new SourceDragPreviewCoordinator<Item>(items, a);

        Assert.False(drag.TryMoveTo(1, 107, 100, 8));
        Assert.True(drag.TryMoveTo(1, 108, 100, 8));
        Assert.False(drag.TryMoveTo(1, 140, 100, 8));

        Assert.Equal([b, a, c], items);
        Assert.Equal(1, drag.PreviewMoveCount);
        Assert.Equal(0, drag.OriginalIndex);
    }

    [Fact]
    public void UpwardCrossingUsesHysteresisAndDoesNotOscillateAtBoundary()
    {
        var (items, a, b, c) = Items();
        var drag = new SourceDragPreviewCoordinator<Item>(items, c);

        Assert.False(drag.TryMoveTo(1, 93, 100, 8));
        Assert.True(drag.TryMoveTo(1, 92, 100, 8));
        Assert.False(drag.TryMoveTo(2, 107, 100, 8));

        Assert.Equal([a, c, b], items);
        Assert.Equal(1, drag.PreviewMoveCount);
    }

    [Fact]
    public void FirstLastUnequalHeightAndAutoScrollRecalculationUseLatestTarget()
    {
        var (items, a, b, c) = Items();
        var drag = new SourceDragPreviewCoordinator<Item>(items, b);

        // Midpoints are supplied by the WPF layer, so unequal/expanded card heights are naturally honored.
        Assert.True(drag.TryMoveTo(0, 41, 55, 8));
        Assert.Equal([b, a, c], items);
        // Simulates a later frame after auto-scroll exposes a new bottom midpoint.
        Assert.True(drag.TryMoveTo(2, 420, 350, 8));
        Assert.Equal([a, c, b], items);
        Assert.Equal(2, drag.PreviewMoveCount);
    }

    [Fact]
    public void CommitSavesAtMostOnce()
    {
        var (items, a, _, _) = Items();
        var drag = new SourceDragPreviewCoordinator<Item>(items, a);
        Assert.True(drag.TryMoveTo(2, 300, 200, 8));
        var saves = 0;

        Assert.True(drag.Commit(() => saves++));
        Assert.False(drag.Commit(() => saves++));

        Assert.Equal(1, saves);
    }

    [Fact]
    public void EscapeCancellationRestoresOrderInstancesAndAudioStateWithoutSaving()
    {
        var (items, a, b, c) = Items();
        var original = items.ToArray();
        var drag = new SourceDragPreviewCoordinator<Item>(items, a);
        Assert.True(drag.TryMoveTo(2, 300, 200, 8));

        drag.Cancel();

        Assert.Equal(original, items);
        Assert.Same(a, items[0]);
        Assert.Same(b, items[1]);
        Assert.Same(c, items[2]);
        Assert.Equal(37, a.Volume);
        Assert.Equal(-12, a.Balance);
        Assert.True(a.EqualizerExpanded);
    }

    [Fact]
    public void CancellationDoesNotResurrectRemovedSourceAndKeepsNewlyDiscoveredItems()
    {
        var (items, a, b, c) = Items();
        var drag = new SourceDragPreviewCoordinator<Item>(items, a);
        Assert.True(drag.TryMoveTo(2, 300, 200, 8));
        var discovered = new Item("d", 54, 2, false);
        items.Remove(a);
        items.Add(discovered);

        drag.Cancel();

        Assert.Equal([b, c, discovered], items);
        Assert.DoesNotContain(a, items);
        Assert.Same(discovered, items[2]);
    }

    private static (ObservableCollection<Item> Items, Item A, Item B, Item C) Items()
    {
        var a = new Item("a", 37, -12, true);
        var b = new Item("b", 62, 5, false);
        var c = new Item("c", 88, 0, false);
        return (new ObservableCollection<Item>([a, b, c]), a, b, c);
    }

    private sealed record Item(string Id, double Volume, double Balance, bool EqualizerExpanded);
}

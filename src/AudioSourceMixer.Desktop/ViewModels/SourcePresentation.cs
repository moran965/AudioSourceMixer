using System.Collections.ObjectModel;
using System.Windows.Input;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Core.Persistence;

namespace AudioSourceMixer.Desktop.ViewModels;

internal sealed record HiddenSourceDescriptor(
    AudioSourceSnapshot Source,
    bool IsManual,
    string Reason);

internal sealed record SourcePresentationResult(
    IReadOnlyList<AudioSourceSnapshot> Visible,
    IReadOnlyList<HiddenSourceDescriptor> Hidden);

internal static class SourcePresentationPolicy
{
    public static SourcePresentationResult Apply(
        IEnumerable<AudioSourceSnapshot> discovered,
        ApplicationSettings settings,
        IReadOnlyDictionary<AudioSourceId, long> modificationSequence,
        IReadOnlySet<string>? runtimeManuallyHidden = null,
        IReadOnlyList<string>? runtimeManualOrder = null)
    {
        var candidates = discovered
            .Where(source => settings.ShowInactiveSessions || source.State == AudioPlaybackState.Active)
            .ToArray();
        var activeEdge = candidates.Any(source => source.Kind == AudioSourceKind.EdgeTab && source.State == AudioPlaybackState.Active);
        var activeChrome = candidates.Any(source => source.Kind == AudioSourceKind.ChromeTab && source.State == AudioPlaybackState.Active);
        var manuallyHidden = (settings.ManuallyHiddenSources ?? [])
            .Select(item => item.SourceId).ToHashSet(StringComparer.Ordinal);
        if (runtimeManuallyHidden is not null) manuallyHidden.UnionWith(runtimeManuallyHidden);
        var hidden = new List<HiddenSourceDescriptor>();
        var visible = new List<AudioSourceSnapshot>(candidates.Length);

        foreach (var source in candidates)
        {
            if (manuallyHidden.Contains(source.Id.Value))
            {
                hidden.Add(new HiddenSourceDescriptor(source, true, "由你手动隐藏"));
                continue;
            }

            var aggregate = settings.HideBrowserAggregateSessions
                ? MatchBrowserAggregate(source)
                : null;
            if ((aggregate == "edge" && activeEdge) || (aggregate == "chrome" && activeChrome))
            {
                hidden.Add(new HiddenSourceDescriptor(source, false, "由浏览器增强自动隐藏"));
                continue;
            }
            visible.Add(source);
        }

        var ordered = SourceSortModes.Normalize(settings.SourceSortMode) == SourceSortModes.Manual
            ? OrderManually(visible, runtimeManualOrder ?? settings.ManualSourceOrder ?? [])
            : OrderByRecentModification(visible, modificationSequence);
        return new SourcePresentationResult(ordered, hidden
            .OrderBy(item => item.Source.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Source.Id.Value, StringComparer.Ordinal).ToArray());
    }

    internal static string? MatchBrowserAggregate(AudioSourceSnapshot source)
    {
        if (source.Kind != AudioSourceKind.WindowsSession) return null;
        string? fileName = null;
        if (!string.IsNullOrWhiteSpace(source.ExecutablePath))
        {
            try { fileName = System.IO.Path.GetFileName(source.ExecutablePath); }
            catch (ArgumentException) { return null; }
        }
        fileName = fileName?.Trim();
        if (string.Equals(fileName, "msedge.exe", StringComparison.OrdinalIgnoreCase)) return "edge";
        if (string.Equals(fileName, "chrome.exe", StringComparison.OrdinalIgnoreCase)) return "chrome";
        if (!string.IsNullOrWhiteSpace(fileName)) return null;

        var exactName = source.DisplayName.Trim();
        if (exactName.Equals("msedge", StringComparison.OrdinalIgnoreCase) ||
            exactName.Equals("msedge.exe", StringComparison.OrdinalIgnoreCase)) return "edge";
        if (exactName.Equals("chrome", StringComparison.OrdinalIgnoreCase) ||
            exactName.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase)) return "chrome";
        return null;
    }

    private static IReadOnlyList<AudioSourceSnapshot> OrderByRecentModification(
        IEnumerable<AudioSourceSnapshot> sources,
        IReadOnlyDictionary<AudioSourceId, long> modificationSequence)
        => sources.OrderByDescending(source => modificationSequence.ContainsKey(source.Id))
            .ThenByDescending(source => modificationSequence.GetValueOrDefault(source.Id))
            .ThenByDescending(source => source.Peak > 0.001f)
            .ThenByDescending(source => source.State == AudioPlaybackState.Active)
            .ThenBy(source => source.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(source => source.Id.Value, StringComparer.Ordinal)
            .ToArray();

    private static IReadOnlyList<AudioSourceSnapshot> OrderManually(
        IEnumerable<AudioSourceSnapshot> sources,
        IReadOnlyList<string> manualOrder)
    {
        var positions = manualOrder.Select((id, index) => (id, index))
            .GroupBy(item => item.id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        return sources.OrderBy(source => positions.TryGetValue(source.Id.Value, out var index) ? index : int.MaxValue)
            .ThenBy(source => source.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(source => source.Id.Value, StringComparer.Ordinal)
            .ToArray();
    }
}

internal static class SourceCollectionReconciler
{
    public static int Reorder<T, TKey>(ObservableCollection<T> collection, IReadOnlyList<TKey> targetOrder,
        Func<T, TKey> keySelector, IEqualityComparer<TKey>? comparer = null)
    {
        comparer ??= EqualityComparer<TKey>.Default;
        var moves = 0;
        for (var targetIndex = 0; targetIndex < targetOrder.Count; targetIndex++)
        {
            var currentIndex = -1;
            for (var candidate = targetIndex; candidate < collection.Count; candidate++)
            {
                if (!comparer.Equals(keySelector(collection[candidate]), targetOrder[targetIndex])) continue;
                currentIndex = candidate;
                break;
            }
            if (currentIndex < 0 || currentIndex == targetIndex) continue;
            collection.Move(currentIndex, targetIndex);
            moves++;
        }
        return moves;
    }
}

public sealed class HiddenSourceViewModel
{
    private readonly Action<AudioSourceId> _restore;

    internal HiddenSourceViewModel(HiddenSourceDescriptor descriptor, Action<AudioSourceId> restore)
    {
        Id = descriptor.Source.Id;
        DisplayName = descriptor.Source.DisplayName;
        SourceType = descriptor.Source.Kind is AudioSourceKind.ChromeTab or AudioSourceKind.EdgeTab
            ? "浏览器增强" : "Windows 应用";
        Reason = descriptor.Reason;
        CanRestore = descriptor.IsManual;
        _restore = restore;
        RestoreCommand = new RelayCommand(() => _restore(Id), () => CanRestore);
    }

    public AudioSourceId Id { get; }
    public string DisplayName { get; }
    public string SourceType { get; }
    public string Reason { get; }
    public bool CanRestore { get; }
    public ICommand RestoreCommand { get; }
}

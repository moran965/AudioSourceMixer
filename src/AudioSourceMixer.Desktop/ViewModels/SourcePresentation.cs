using System.Collections.ObjectModel;
using System.Windows.Input;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Core.Persistence;
using AudioSourceMixer.Desktop.Localization;

namespace AudioSourceMixer.Desktop.ViewModels;

internal sealed record HiddenSourceDescriptor(
    AudioSourceSnapshot Source,
    string Reason);

internal sealed record SourcePresentationResult(
    IReadOnlyList<AudioSourceSnapshot> Visible,
    IReadOnlyList<HiddenSourceDescriptor> Hidden);

internal static class SourcePresentationPolicy
{
    public static SourcePresentationResult Apply(
        IEnumerable<AudioSourceSnapshot> discovered,
        ApplicationSettings settings,
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
                hidden.Add(new HiddenSourceDescriptor(source, "Source.HiddenReason"));
                continue;
            }

            var aggregate = settings.HideBrowserAggregateSessions
                ? MatchBrowserAggregate(source)
                : null;
            if ((aggregate == "edge" && activeEdge) || (aggregate == "chrome" && activeChrome))
            {
                continue;
            }
            visible.Add(source);
        }

        var ordered = OrderManually(visible, runtimeManualOrder ?? settings.ManualSourceOrder ?? []);
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

    private static IReadOnlyList<AudioSourceSnapshot> OrderManually(
        IEnumerable<AudioSourceSnapshot> sources,
        IReadOnlyList<string> manualOrder)
    {
        var positions = manualOrder.Select((id, index) => (id, index))
            .GroupBy(item => item.id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().index, StringComparer.Ordinal);
        return sources.OrderBy(source => positions.TryGetValue(source.Id.Value, out var index) ? index : int.MaxValue)
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

public sealed class HiddenSourceViewModel : ObservableObject
{
    private readonly Action<HiddenSourceDescriptor> _restore;

    internal HiddenSourceViewModel(HiddenSourceDescriptor descriptor, Action<HiddenSourceDescriptor> restore)
    {
        Id = descriptor.Source.Id;
        DisplayName = descriptor.Source.DisplayName;
        Descriptor = descriptor;
        CanRestore = true;
        _restore = restore;
        RestoreCommand = new RelayCommand(() => _restore(Descriptor));
    }

    public AudioSourceId Id { get; }
    public string DisplayName { get; }
    public string SourceType => LocalizationService.Current[Descriptor.Source.Kind is AudioSourceKind.ChromeTab or AudioSourceKind.EdgeTab
        ? "Common.BrowserEnhanced" : "Common.WindowsApplication"];
    public string Reason => LocalizationService.Current[Descriptor.Reason];
    public bool CanRestore { get; }
    public string RestoreLabel => LocalizationService.Current["Source.RestoreDisplay"];
    internal HiddenSourceDescriptor Descriptor { get; }
    public ICommand RestoreCommand { get; }
    internal void RefreshLocalization()
    {
        Raise(nameof(SourceType));
        Raise(nameof(Reason));
        Raise(nameof(RestoreLabel));
    }
}

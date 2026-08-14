using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Core.Persistence;
using AudioSourceMixer.Desktop.ViewModels;

namespace AudioSourceMixer.Desktop.Tests;

public sealed class SourcePresentationTests
{
    [Fact]
    public void BrowserAggregateFilteringIsExactIndependentAndReversible()
    {
        var edge = Source("edge-session", "msedge", "C:\\Program Files\\Microsoft\\Edge\\Application\\msedge.exe");
        var chrome = Source("chrome-session", "chrome", "C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe");
        var electron = Source("electron-session", "Chrome Helper Notes", "C:\\Apps\\Notes\\notes.exe");
        var edgeTab = Browser("edge", 1);

        var edgeOnly = SourcePresentationPolicy.Apply([edge, chrome, electron, edgeTab], new ApplicationSettings(), EmptySequence());
        Assert.DoesNotContain(edgeOnly.Visible, item => item.Id == edge.Id);
        Assert.Contains(edgeOnly.Visible, item => item.Id == chrome.Id);
        Assert.Contains(edgeOnly.Visible, item => item.Id == electron.Id);
        Assert.Contains(edgeOnly.Hidden, item => item.Source.Id == edge.Id && !item.IsManual);

        var noEnhancedTab = SourcePresentationPolicy.Apply([edge, chrome, electron], new ApplicationSettings(), EmptySequence());
        Assert.Contains(noEnhancedTab.Visible, item => item.Id == edge.Id);
        Assert.Contains(noEnhancedTab.Visible, item => item.Id == chrome.Id);

        var disabled = SourcePresentationPolicy.Apply([edge, chrome, edgeTab],
            new ApplicationSettings(HideBrowserAggregateSessions: false), EmptySequence());
        Assert.Contains(disabled.Visible, item => item.Id == edge.Id);
    }

    [Fact]
    public void ManualHideTargetsOnlyExactSessionFromSameExecutable()
    {
        var first = Source("first-instance", "播放器一", "C:\\Player\\player.exe");
        var second = Source("second-instance", "播放器二", "C:\\Player\\player.exe");
        var settings = new ApplicationSettings(ManuallyHiddenSources:
            [new HiddenSourceSetting(first.Id.Value, AudioSourceKind.WindowsSession, DateTimeOffset.UtcNow)]);

        var result = SourcePresentationPolicy.Apply([first, second], settings, EmptySequence());

        Assert.DoesNotContain(result.Visible, item => item.Id == first.Id);
        Assert.Contains(result.Visible, item => item.Id == second.Id);
        Assert.Contains(result.Hidden, item => item.Source.Id == first.Id && item.IsManual);
    }

    [Fact]
    public void RecentAndManualOrderingHaveIndependentRules()
    {
        var a = Source("a", "A", "C:\\A.exe");
        var b = Source("b", "B", "C:\\B.exe");
        var c = Source("c", "C", "C:\\C.exe") with { Peak = 0.4f };
        var recent = SourcePresentationPolicy.Apply([a, b, c], new ApplicationSettings(),
            new Dictionary<AudioSourceId, long> { [a.Id] = 1, [b.Id] = 2 });
        Assert.Equal([b.Id, a.Id, c.Id], recent.Visible.Select(item => item.Id).ToArray());

        var manual = SourcePresentationPolicy.Apply([a, b, c],
            new ApplicationSettings(SourceSortMode: SourceSortModes.Manual,
                ManualSourceOrder: [c.Id.Value, a.Id.Value, b.Id.Value]),
            new Dictionary<AudioSourceId, long> { [b.Id] = 99 });
        Assert.Equal([c.Id, a.Id, b.Id], manual.Visible.Select(item => item.Id).ToArray());
    }

    [Fact]
    public void KeyedReorderUsesMoveAndPreservesInstances()
    {
        var a = new Item("a");
        var b = new Item("b");
        var c = new Item("c");
        var collection = new ObservableCollection<Item>([a, b, c]);
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        collection.CollectionChanged += (_, args) => actions.Add(args.Action);

        var moves = SourceCollectionReconciler.Reorder(collection, ["c", "a", "b"], item => item.Id,
            StringComparer.Ordinal);

        Assert.Equal(1, moves);
        Assert.Equal([c, a, b], collection.ToArray());
        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Move], actions);
        Assert.Same(a, collection[1]);
        Assert.Same(b, collection[2]);
    }

    [Fact]
    public void PageLogoAndIcoContainDeliveryGradeFrames()
    {
        var page = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "IconAssets", "AudioSourceMixer-page.png"));
        Assert.Equal("PNG", Encoding.ASCII.GetString(page, 1, 3));
        Assert.Equal(512, ReadBigEndianInt32(page, 16));
        Assert.Equal(512, ReadBigEndianInt32(page, 20));
        Assert.Equal(6, page[25]); // RGBA
        using (var stream = new MemoryStream(page))
        {
            var frame = new System.Windows.Media.Imaging.PngBitmapDecoder(stream,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad).Frames[0];
            var pixel = new byte[4];
            frame.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
            Assert.Equal(0, pixel[3]);
        }

        var ico = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "IconAssets", "AudioSourceMixer.ico"));
        var count = BitConverter.ToUInt16(ico, 4);
        Assert.Equal(10, count);
        var sizes = Enumerable.Range(0, count).Select(index =>
        {
            var value = ico[6 + index * 16];
            return value == 0 ? 256 : value;
        }).ToArray();
        Assert.Equal([16, 20, 24, 32, 40, 48, 64, 96, 128, 256], sizes);
    }

    private static int ReadBigEndianInt32(byte[] value, int offset)
        => (value[offset] << 24) | (value[offset + 1] << 16) | (value[offset + 2] << 8) | value[offset + 3];

    private static IReadOnlyDictionary<AudioSourceId, long> EmptySequence()
        => new Dictionary<AudioSourceId, long>();

    private static AudioSourceSnapshot Source(string instance, string name, string path)
        => new(AudioSourceId.ForWindowsSession("device", instance), AudioSourceKind.WindowsSession, name,
            "Windows 会话", 12, path, "device", "session", instance, AudioPlaybackState.Active,
            1, false, 0, 0, [1, 1], new AudioSourceCapabilities(true, true, true, 2, false, true, true),
            DateTimeOffset.UtcNow);

    private static AudioSourceSnapshot Browser(string browser, long tabId)
        => new(AudioSourceId.ForBrowserTab(browser, tabId), browser == "edge" ? AudioSourceKind.EdgeTab : AudioSourceKind.ChromeTab,
            "标签页", "https://example.test", 0, null, "browser", "origin", $"{browser}:{tabId}",
            AudioPlaybackState.Active, 1, false, 0, 0.2f, [1, 1],
            new AudioSourceCapabilities(true, true, true, 2, true, true, true), DateTimeOffset.UtcNow);

    private sealed record Item(string Id);
}

using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Desktop.ViewModels;
using AudioSourceMixer.WindowsAudio;
using ListBox = System.Windows.Controls.ListBox;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AudioSourceMixer.Desktop.Diagnostics;

internal sealed record LiveMeterSample(
    DateTimeOffset Timestamp,
    AudioSourceId SourceId,
    string DisplayName,
    uint ProcessId,
    float RawPeak,
    float SmoothedPeak,
    double UiPeakPercent,
    double ProgressBarValue,
    double TrackWidth,
    double IndicatorWidth,
    bool SourceAvailable);

internal sealed record LiveMeterReport(
    uint ProcessId,
    int RequestedDurationSeconds,
    int SampleCount,
    float MaximumRawPeak,
    float MaximumSmoothedPeak,
    double MaximumIndicatorWidth,
    bool ReturnedToZero,
    IReadOnlyList<LiveMeterSample> Samples);

internal static class LiveMeterVerifier
{
    public static async Task<LiveMeterReport> VerifyAsync(MainWindow window, MainViewModel viewModel,
        WindowsAudioService audio, uint processId, int durationSeconds, CancellationToken cancellationToken = default)
    {
        var discoveryDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
        AudioSourceViewModel? source = null;
        while (source is null && DateTimeOffset.UtcNow < discoveryDeadline)
        {
            source = viewModel.Sources.FirstOrDefault(item => item.Snapshot.ProcessId == processId);
            if (source is null) await Task.Delay(100, cancellationToken);
        }
        if (source is null) throw new InvalidOperationException($"No live WPF audio source was discovered for PID {processId}.");

        var sourceId = source.Id;
        var displayName = source.DisplayName;
        var samples = new List<LiveMeterSample>(durationSeconds * 10);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(durationSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = viewModel.Sources.FirstOrDefault(item => item.Id == sourceId);
            var raw = (await audio.GetSourcesAsync(cancellationToken)).FirstOrDefault(item => item.Id == sourceId)?.Peak ?? 0;
            var uiPeak = current?.PeakPercent ?? 0;
            var (value, trackWidth, indicatorWidth) = current is null
                ? (0d, 0d, 0d)
                : await ReadVisualMeterAsync(window, current, cancellationToken);
            samples.Add(new LiveMeterSample(DateTimeOffset.UtcNow, sourceId, displayName, processId,
                float.IsFinite(raw) ? Math.Clamp(raw, 0, 1) : 0,
                (float)Math.Clamp(uiPeak / 100, 0, 1), uiPeak, value, trackWidth, indicatorWidth,
                current is not null));
            await Task.Delay(100, cancellationToken);
        }

        var maximumRaw = samples.Max(sample => sample.RawPeak);
        var maximumSmoothed = samples.Max(sample => sample.SmoothedPeak);
        var maximumIndicator = samples.Max(sample => sample.IndicatorWidth);
        var returnedToZero = samples.TakeLast(Math.Min(5, samples.Count))
            .All(sample => sample.UiPeakPercent <= 2 && sample.ProgressBarValue <= 2 &&
                           (sample.TrackWidth <= 0 || sample.IndicatorWidth <= Math.Max(1, sample.TrackWidth * 0.02)));
        if (maximumRaw <= 0.001f)
            throw new InvalidOperationException($"PID {processId} never produced a positive raw WASAPI peak.");
        if (maximumSmoothed <= 0.001f || maximumIndicator <= 1)
            throw new InvalidOperationException($"PID {processId} produced audio, but its WPF meter never became visibly non-zero.");
        if (!returnedToZero)
            throw new InvalidOperationException($"PID {processId} WPF meter did not return to zero by the end of the probe.");

        return new LiveMeterReport(processId, durationSeconds, samples.Count, maximumRaw, maximumSmoothed,
            maximumIndicator, returnedToZero, samples);
    }

    public static async Task WriteAsync(string path, LiveMeterReport report, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    private static async Task<(double Value, double TrackWidth, double IndicatorWidth)> ReadVisualMeterAsync(
        MainWindow window, AudioSourceViewModel source, CancellationToken cancellationToken)
    {
        if (window.SourceItems is ListBox list) list.ScrollIntoView(source);
        await window.Dispatcher.InvokeAsync(() => window.SourceItems.UpdateLayout(), DispatcherPriority.Render, cancellationToken);
        var container = window.SourceItems.ItemContainerGenerator.ContainerFromItem(source);
        if (container is null) return (source.PeakPercent, 0, 0);
        var bar = Descendants(container).OfType<ProgressBar>().SingleOrDefault();
        if (bar is null) return (source.PeakPercent, 0, 0);
        bar.ApplyTemplate();
        var track = bar.Template.FindName("PART_Track", bar) as FrameworkElement;
        var indicator = bar.Template.FindName("PART_Indicator", bar) as FrameworkElement;
        return (bar.Value, track?.ActualWidth ?? 0, indicator?.ActualWidth ?? 0);
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
}

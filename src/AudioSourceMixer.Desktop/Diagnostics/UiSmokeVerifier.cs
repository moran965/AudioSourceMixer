using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Desktop.ViewModels;
using ProgressBar = System.Windows.Controls.ProgressBar;

namespace AudioSourceMixer.Desktop.Diagnostics;

internal sealed record BindingAuditEntry(
    string Target,
    string TargetProperty,
    string Source,
    string SourceProperty,
    BindingMode DeclaredMode,
    BindingMode EffectiveMode,
    bool HasPublicSetter);

internal sealed record UiSmokeResult(
    int ItemCount,
    string ContainerType,
    double PeakValue,
    IReadOnlyList<BindingAuditEntry> Bindings);

internal static class UiSmokeVerifier
{
    public const float UpdatedPeak = 0.73f;

    public static AudioSourceSnapshot CreateDiagnosticSource() => new(
        new AudioSourceId("diagnostic:ui-smoke"),
        AudioSourceKind.WindowsSession,
        "UI Smoke Test Source",
        "Deterministic in-memory source",
        0,
        null,
        "diagnostic-device",
        "diagnostic-session",
        "diagnostic-instance",
        AudioPlaybackState.Active,
        0.65f,
        false,
        0,
        0.12f,
        [1, 1],
        new AudioSourceCapabilities(true, true, true, 2, false, true, true,
            SupportsExtendedGain: false, SupportsOutputRouting: true, SupportsDeviceHotSwitch: true),
        DateTimeOffset.UtcNow,
        RequestedOutputDeviceName: OutputDeviceInfo.SystemDefault.Name,
        EffectiveOutputDeviceId: "diagnostic-device",
        EffectiveOutputDeviceName: "Diagnostic Device",
            RoutingState: AudioRoutingState.SystemDefault);

    public static async Task<UiSmokeResult> VerifyAsync(
        MainWindow window,
        AudioSourceViewModel diagnosticSource,
        CancellationToken cancellationToken = default)
    {
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle, cancellationToken);
        window.UpdateLayout();
        window.SourceItems.ApplyTemplate();
        window.SourceItems.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render, cancellationToken);

        if (window.SourceItems.Items.Count < 1)
            throw new InvalidOperationException("The UI smoke test did not receive an audio source item.");

        if (window.SourceItems is System.Windows.Controls.ListBox virtualizedList)
        {
            virtualizedList.ScrollIntoView(diagnosticSource);
            await window.Dispatcher.InvokeAsync(() => virtualizedList.UpdateLayout(), DispatcherPriority.Render, cancellationToken);
        }

        var container = window.SourceItems.ItemContainerGenerator.ContainerFromItem(diagnosticSource)
                        ?? throw new InvalidOperationException("ItemsControl did not generate a container for the diagnostic audio source.");
        if (VisualTreeHelper.GetChildrenCount(container) == 0)
            throw new InvalidOperationException("The diagnostic audio source DataTemplate was not instantiated.");

        var peakBar = Descendants(container).OfType<ProgressBar>().SingleOrDefault()
                      ?? throw new InvalidOperationException("The instantiated audio source DataTemplate does not contain its ProgressBar.");
        var peakBinding = BindingOperations.GetBinding(peakBar, ProgressBar.ValueProperty)
                          ?? throw new InvalidOperationException("Peak ProgressBar.Value has no Binding.");
        if (GetEffectiveMode(peakBinding, peakBar, ProgressBar.ValueProperty) != BindingMode.OneWay)
            throw new InvalidOperationException("PeakPercent must have an effective OneWay binding.");

        var peakNotificationObserved = false;
        diagnosticSource.PropertyChanged += OnPropertyChanged;
        try
        {
            diagnosticSource.Update(diagnosticSource.Snapshot with { Peak = UpdatedPeak, ObservedAt = DateTimeOffset.UtcNow });
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render, cancellationToken);
            window.UpdateLayout();
        }
        finally
        {
            diagnosticSource.PropertyChanged -= OnPropertyChanged;
        }

        if (!peakNotificationObserved)
            throw new InvalidOperationException("Updating the audio snapshot did not raise PropertyChanged for PeakPercent.");
        var expectedPeak = UpdatedPeak * 100d;
        if (Math.Abs(peakBar.Value - expectedPeak) > 0.001)
            throw new InvalidOperationException($"Peak ProgressBar did not update. Expected {expectedPeak}, actual {peakBar.Value}.");

        var mixerAudits = AuditBindings(window, window.DataContext)
            .Concat(AuditBindings(container, diagnosticSource)).ToArray();
        window.SettingsPage.IsSelected = true;
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle, cancellationToken);
        if (window.SettingsPage.Content is not DependencyObject settingsContent ||
            VisualTreeHelper.GetChildrenCount(settingsContent) == 0)
            throw new InvalidOperationException("The settings page was not materialized by the UI smoke test.");

        var audits = mixerAudits
            .Concat(AuditBindings(window, window.DataContext))
            .Distinct()
            .ToArray();
        var defaults = audits.Where(entry => entry.DeclaredMode == BindingMode.Default).ToArray();
        if (defaults.Length > 0)
            throw new InvalidOperationException($"Application bindings must declare their mode explicitly: {Format(defaults)}");
        var invalidReadOnly = audits.Where(entry => !entry.HasPublicSetter && entry.EffectiveMode != BindingMode.OneWay).ToArray();
        if (invalidReadOnly.Length > 0)
            throw new InvalidOperationException($"Getter-only properties must use OneWay bindings: {Format(invalidReadOnly)}");
        var invalidWrites = audits.Where(entry => entry.EffectiveMode is BindingMode.TwoWay or BindingMode.OneWayToSource && !entry.HasPublicSetter).ToArray();
        if (invalidWrites.Length > 0)
            throw new InvalidOperationException($"Writable bindings target source properties without public setters: {Format(invalidWrites)}");

        window.MixerPage.IsSelected = true;
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle, cancellationToken);

        return new UiSmokeResult(window.SourceItems.Items.Count, container.GetType().Name, peakBar.Value, audits);

        void OnPropertyChanged(object? _, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(AudioSourceViewModel.PeakPercent)) peakNotificationObserved = true;
        }
    }

    private static IReadOnlyList<BindingAuditEntry> AuditBindings(DependencyObject root, object? fallbackSource)
    {
        var entries = new List<BindingAuditEntry>();
        foreach (var target in DescendantsAndSelf(root))
        {
            var values = target.GetLocalValueEnumerator();
            while (values.MoveNext())
            {
                var property = values.Current.Property;
                var binding = BindingOperations.GetBinding(target, property);
                if (binding is null || binding.Path?.Path is not string path || path.Contains('.')) continue;
                var expression = BindingOperations.GetBindingExpression(target, property);
                var source = expression?.DataItem;
                if (source is not MainViewModel && source is not AudioSourceViewModel && target is FrameworkElement element)
                    source = element.DataContext;
                if (source is not MainViewModel && source is not AudioSourceViewModel) source = fallbackSource;
                if (source is not MainViewModel && source is not AudioSourceViewModel) continue;
                var sourceProperty = source.GetType().GetProperty(path, BindingFlags.Instance | BindingFlags.Public);
                if (sourceProperty is null) continue;
                entries.Add(new BindingAuditEntry(
                    target.GetType().Name,
                    property.Name,
                    source.GetType().Name,
                    path,
                    binding.Mode,
                    GetEffectiveMode(binding, target, property),
                    sourceProperty.SetMethod?.IsPublic == true));
            }
        }
        return entries;
    }

    private static BindingMode GetEffectiveMode(System.Windows.Data.Binding binding, DependencyObject target, DependencyProperty property)
    {
        if (binding.Mode != BindingMode.Default) return binding.Mode;
        var metadata = property.GetMetadata(target.GetType()) as FrameworkPropertyMetadata;
        return metadata?.BindsTwoWayByDefault == true ? BindingMode.TwoWay : BindingMode.OneWay;
    }

    private static IEnumerable<DependencyObject> DescendantsAndSelf(DependencyObject root)
    {
        yield return root;
        foreach (var child in Descendants(root)) yield return child;
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

    private static string Format(IEnumerable<BindingAuditEntry> entries)
        => string.Join(", ", entries.Select(entry => $"{entry.Source}.{entry.SourceProperty}->{entry.Target}.{entry.TargetProperty} ({entry.EffectiveMode})"));
}

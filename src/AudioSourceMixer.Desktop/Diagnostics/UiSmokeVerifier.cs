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
    double PeakTrackWidth,
    double PeakIndicatorWidth,
    IReadOnlyList<BindingAuditEntry> Bindings);

internal static class UiSmokeVerifier
{
    public const float UpdatedPeak = 0.73f;

    public static AudioSourceSnapshot CreateDiagnosticSource() => CreateDiagnosticSources()[0];

    public static IReadOnlyList<AudioSourceSnapshot> CreateDiagnosticSources()
    {
        var now = DateTimeOffset.UtcNow;
        var browser = new AudioSourceSnapshot(
        AudioSourceId.ForBrowserTab("edge", 22001),
        AudioSourceKind.EdgeTab,
        "Edge · 一段非常长的中文标签页标题，用于确认窄窗口、百分之二百缩放与设备授权提示都不会彼此遮挡",
        "https://music.example.test",
        0,
        null,
        "browser",
        "https://music.example.test",
        "diagnostic-browser-edge",
        AudioPlaybackState.Active,
        1.5f,
        false,
        -0.18f,
        0.12f,
        [1, 1],
        new AudioSourceCapabilities(true, true, true, 2, true, true, true,
            SupportsExtendedGain: true, SupportsOutputRouting: true, SupportsDeviceHotSwitch: true,
            SupportsEqualizer: true),
        now,
        OutputDeviceId: "diagnostic-long-device",
        OutputDeviceName: "客厅蓝牙耳机与 USB 解码器组合输出设备（用于布局诊断的超长名称）",
        ProcessingMode: AudioProcessingMode.Advanced,
        RequestedOutputDeviceId: "diagnostic-long-device",
        RequestedOutputDeviceName: "客厅蓝牙耳机与 USB 解码器组合输出设备（用于布局诊断的超长名称）",
        EffectiveOutputDeviceId: "diagnostic-original-device",
        EffectiveOutputDeviceName: "原输出设备",
        RoutingState: AudioRoutingState.PendingAuthorization,
        Effects: EqualizerCatalog.CreatePreset("bass"));

        var windows = new AudioSourceSnapshot(
            AudioSourceId.ForWindowsSession("diagnostic-device", "diagnostic-windows"),
            AudioSourceKind.WindowsSession,
            "播放器 · 普通 Windows 会话（原生音量范围 0–100%）",
            "C:\\Program Files\\Audio Player\\player.exe",
            22002,
            "C:\\Program Files\\Audio Player\\player.exe",
            "diagnostic-device",
            "diagnostic-windows-session",
            "diagnostic-windows-instance",
            AudioPlaybackState.Active,
            0.65f,
            false,
            0,
            0.33f,
            [1, 1],
            new AudioSourceCapabilities(true, true, true, 2, false, true, true,
                SupportsExtendedGain: false, SupportsOutputRouting: true, SupportsDeviceHotSwitch: true,
                SupportsEqualizer: true),
            now,
            RequestedOutputDeviceName: OutputDeviceInfo.SystemDefault.Name,
            EffectiveOutputDeviceId: "diagnostic-device",
            EffectiveOutputDeviceName: "Diagnostic Device",
            RoutingState: AudioRoutingState.SystemDefault);

        var english = browser with
        {
            Id = AudioSourceId.ForBrowserTab("chrome", 22003),
            Kind = AudioSourceKind.ChromeTab,
            DisplayName = "Chrome · An intentionally very long browser tab title for responsive layout and ellipsis verification without clipping controls",
            SourceDescription = "https://an-intentionally-long-subdomain-for-layout-testing.example.test",
            Volume = 2f,
            Balance = 0.27f,
            Peak = 0.48f,
            RoutingState = AudioRoutingState.Failed,
            RoutingError = "浏览器中的设备已变化，请重新选择并试听确认。",
            RequestedOutputDeviceName = "Professional USB Audio Interface with an exceptionally long friendly device name",
            OutputDeviceName = "Professional USB Audio Interface with an exceptionally long friendly device name",
            Effects = EqualizerCatalog.CreatePreset("vocal")
        };
        return [windows, browser, english];
    }

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

        foreach (var item in window.SourceItems.Items.Cast<object>())
        {
            if (window.SourceItems is System.Windows.Controls.ListBox list) list.ScrollIntoView(item);
            await window.Dispatcher.InvokeAsync(() => window.SourceItems.UpdateLayout(), DispatcherPriority.Render, cancellationToken);
            var itemContainer = window.SourceItems.ItemContainerGenerator.ContainerFromItem(item)
                                ?? throw new InvalidOperationException("ItemsControl did not generate every diagnostic source container.");
            if (VisualTreeHelper.GetChildrenCount(itemContainer) == 0)
                throw new InvalidOperationException("A diagnostic source DataTemplate was not instantiated.");
        }

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
        peakBar.ApplyTemplate();
        var peakTrack = peakBar.Template.FindName("PART_Track", peakBar) as FrameworkElement
                        ?? throw new InvalidOperationException("ProgressBar template does not contain PART_Track.");
        var peakIndicator = peakBar.Template.FindName("PART_Indicator", peakBar) as FrameworkElement
                            ?? throw new InvalidOperationException("ProgressBar template does not contain PART_Indicator.");
        if (peakTrack.ActualWidth <= 0)
            throw new InvalidOperationException("ProgressBar PART_Track has no measurable width.");

        await AssertIndicatorRatioAsync(0, 0, 0.02);
        await AssertIndicatorRatioAsync(0.5f, 0.5, 0.06);
        await AssertIndicatorRatioAsync(1, 1, 0.02);

        diagnosticSource.IsEqualizerExpanded = true;
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render, cancellationToken);
        var equalizerSliders = Descendants(container).OfType<Slider>()
            .Where(slider => slider.DataContext is EqualizerBandViewModel).ToArray();
        if (equalizerSliders.Length != EqualizerCatalog.Bands.Count)
            throw new InvalidOperationException($"Equalizer DataTemplate created {equalizerSliders.Length} band controls; expected {EqualizerCatalog.Bands.Count}.");

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
        var updatedRatio = peakIndicator.ActualWidth / peakTrack.ActualWidth;
        if (Math.Abs(updatedRatio - UpdatedPeak) > 0.06)
            throw new InvalidOperationException($"Peak indicator width did not update visually. Expected ratio {UpdatedPeak:F2}, actual {updatedRatio:F2}.");

        var mixerAudits = AuditBindings(window, window.DataContext)
            .Concat(AuditBindings(container, diagnosticSource)).ToArray();
        window.SelectSettingsPage();
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle, cancellationToken);
        if (VisualTreeHelper.GetChildrenCount(window.SettingsPage) == 0 ||
            !Descendants(window.SettingsPage).OfType<System.Windows.Controls.Control>()
                .Any(control => control.Focusable && control.IsTabStop))
            throw new InvalidOperationException("The settings page was not materialized or keyboard accessible.");

        window.SelectBrowserSetupPage();
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle, cancellationToken);
        if (VisualTreeHelper.GetChildrenCount(window.BrowserSetupPage) == 0 ||
            !Descendants(window.BrowserSetupPage).OfType<System.Windows.Controls.Button>().Any(button => button.Focusable && button.IsTabStop))
            throw new InvalidOperationException("The browser setup page was not materialized or keyboard accessible.");

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

        window.SelectMixerPage();
        await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle, cancellationToken);

        return new UiSmokeResult(window.SourceItems.Items.Count, container.GetType().Name, peakBar.Value,
            peakTrack.ActualWidth, peakIndicator.ActualWidth, audits);

        async Task AssertIndicatorRatioAsync(float peak, double expected, double tolerance)
        {
            diagnosticSource.UpdatePeak(peak, DateTimeOffset.UtcNow);
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.Render, cancellationToken);
            var actual = peakIndicator.ActualWidth / peakTrack.ActualWidth;
            if (Math.Abs(actual - expected) > tolerance)
                throw new InvalidOperationException($"ProgressBar visual ratio for Value={peak * 100:F0} was {actual:F3}; expected {expected:F3}±{tolerance:F3}.");
        }

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
                if (source is not MainViewModel && source is not AudioSourceViewModel && source is not EqualizerBandViewModel && target is FrameworkElement element)
                    source = element.DataContext;
                if (source is not MainViewModel && source is not AudioSourceViewModel && source is not EqualizerBandViewModel) source = fallbackSource;
                if (source is not MainViewModel && source is not AudioSourceViewModel && source is not EqualizerBandViewModel) continue;
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

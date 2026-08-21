using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AudioSourceMixer.Desktop.Localization;
using AudioSourceMixer.Desktop.ViewModels;
using IOPath = System.IO.Path;

namespace AudioSourceMixer.Desktop.Tests;

internal static class WpfUiStyleAssertions
{
    private const string ExpectedFont = "Microsoft YaHei UI, Microsoft YaHei, Segoe UI, Global User Interface";
    private static readonly string[] SettingLabelKeys =
    [
        "Settings.StartWithWindows", "Settings.StartInTray", "Settings.CloseToTray", "Settings.ShowInactive",
        "Settings.HideBrowserAggregate", "Settings.RememberProfiles", "Settings.AutoApply", "Settings.ShowTips"
    ];

    private static string[] SettingLabels => SettingLabelKeys.Select(key => LocalizationService.Current[key]).ToArray();

    public static async Task AssertAsync(App app, MainWindow window, MainViewModel viewModel)
    {
        AssertTypography(app, window);
        AssertUserVisibleTextSources();
        await AssertCheckBoxTemplateAndHitRangeAsync(app, window, viewModel);
    }

    private static void AssertTypography(App app, MainWindow window)
    {
        var applicationFont = Assert.IsType<FontFamily>(app.FindResource("ApplicationFont"));
        Assert.Equal(ExpectedFont, applicationFont.Source);
        Assert.StartsWith("Microsoft YaHei UI", applicationFont.Source, StringComparison.Ordinal);
        Assert.Equal(applicationFont.Source, window.FontFamily.Source);
        Assert.Equal("zh-CN", window.Language.IetfLanguageTag, ignoreCase: true);
        Assert.Equal(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(window));
        Assert.Equal(TextRenderingMode.ClearType, TextOptions.GetTextRenderingMode(window));

        var fontElements = Descendants(window).Where(element => element is TextBlock or Button or CheckBox or RadioButton or ComboBox)
            .OfType<FrameworkElement>().ToArray();
        Assert.NotEmpty(fontElements);
        foreach (var element in fontElements)
        {
            var source = element switch
            {
                TextBlock textBlock => textBlock.FontFamily.Source,
                Control control => control.FontFamily.Source,
                _ => throw new InvalidOperationException()
            };
            Assert.Equal(window.FontFamily.Source, source);
            var localValue = element switch
            {
                TextBlock textBlock => textBlock.ReadLocalValue(TextBlock.FontFamilyProperty),
                Control control => control.ReadLocalValue(Control.FontFamilyProperty),
                _ => DependencyProperty.UnsetValue
            };
            Assert.Same(DependencyProperty.UnsetValue, localValue);
        }

        var globallyStyledTypes = new[]
        {
            typeof(Window), typeof(UserControl), typeof(Label), typeof(RadioButton), typeof(Expander), typeof(ToolTip),
            typeof(MenuItem), typeof(ContextMenu), typeof(Button), typeof(CheckBox), typeof(ComboBox), typeof(ComboBoxItem)
        };
        foreach (var type in globallyStyledTypes)
        {
            var style = Assert.IsType<Style>(app.FindResource(type));
            Assert.True(typeof(Control).IsAssignableFrom(style.TargetType));
        }

        var preferredFamily = Fonts.SystemFontFamilies.FirstOrDefault(family =>
            family.Source.Equals("Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase));
        if (preferredFamily is null)
        {
            Assert.Contains("Microsoft YaHei", applicationFont.Source, StringComparison.Ordinal);
            Assert.Contains("Segoe UI", applicationFont.Source, StringComparison.Ordinal);
            return;
        }

        var typeface = new Typeface(preferredFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        Assert.True(typeface.TryGetGlyphTypeface(out var glyphTypeface), "Microsoft YaHei UI cannot produce a GlyphTypeface.");
        foreach (var character in "音频来源设置浏览器均衡器输出设备")
            Assert.True(glyphTypeface.CharacterToGlyphMap.ContainsKey(character), $"Microsoft YaHei UI is missing glyph U+{(int)character:X4}.");
    }

    private static void AssertUserVisibleTextSources()
    {
        var sourceRoots = new[]
        {
            IOPath.Combine(AppContext.BaseDirectory, "SourceXaml"),
            IOPath.Combine(AppContext.BaseDirectory, "SourceCode")
        };
        var sourceFiles = sourceRoots.Where(Directory.Exists)
            .SelectMany(root => Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            .Where(path => IOPath.GetExtension(path) is ".xaml" or ".cs")
            .ToArray();
        Assert.NotEmpty(sourceFiles);

        var strictUtf8 = new UTF8Encoding(false, true);
        var forbidden = new Regex("[\\uE000-\\uF8FF\\uF900-\\uFAFF\\uFFFD]", RegexOptions.CultureInvariant);
        var mojibake = new[] { "锟斤拷", "烫烫", "屯屯", "娴嬭瘯", "璁剧疆", "鏄剧ず", "鍚姩" };
        foreach (var path in sourceFiles)
        {
            var text = strictUtf8.GetString(File.ReadAllBytes(path));
            Assert.DoesNotMatch(forbidden, text);
            foreach (var fragment in mojibake) Assert.DoesNotContain(fragment, text, StringComparison.Ordinal);
        }

        var settings = strictUtf8.GetString(File.ReadAllBytes(IOPath.Combine(AppContext.BaseDirectory, "SourceXaml", "SettingsView.xaml")));
        foreach (var key in SettingLabelKeys) Assert.Contains($"Key={key}", settings, StringComparison.Ordinal);
        foreach (var key in new[] { "Settings.StartupSection", "Settings.SessionSection", "Settings.AppearanceSection", "Settings.DiagnosticsSection" })
            Assert.Contains($"Key={key}", settings, StringComparison.Ordinal);
    }

    private static async Task AssertCheckBoxTemplateAndHitRangeAsync(App app, MainWindow window, MainViewModel viewModel)
    {
        window.Width = 880;
        window.Height = 600;
        window.SelectSettingsPage();
        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

        var checkBoxes = Descendants(window.SettingsPage).OfType<CheckBox>()
            .Where(checkBox => checkBox.Content is string label && SettingLabels.Contains(label, StringComparer.Ordinal))
            .ToArray();
        Assert.Equal(SettingLabels.Length, checkBoxes.Length);
        foreach (var checkBox in checkBoxes)
        {
            checkBox.ApplyTemplate();
            checkBox.UpdateLayout();
            Assert.Equal(HorizontalAlignment.Left, checkBox.HorizontalAlignment);
            Assert.Equal(HorizontalAlignment.Left, checkBox.HorizontalContentAlignment);
            Assert.NotNull(checkBox.FocusVisualStyle);
            Assert.DoesNotContain(Descendants(checkBox), element => element is System.Windows.Shapes.Path);

            var parent = Assert.IsAssignableFrom<FrameworkElement>(checkBox.Parent);
            Assert.True(checkBox.ActualWidth < parent.ActualWidth * 0.7,
                $"'{checkBox.Content}' still occupies most of its {parent.ActualWidth:F1}px row ({checkBox.ActualWidth:F1}px)." );
            var presenter = Assert.Single(Descendants(checkBox).OfType<ContentPresenter>());
            Assert.InRange(checkBox.ActualWidth, 16 + 8 + presenter.ActualWidth - 1, 16 + 8 + presenter.ActualWidth + 1);

            if (checkBox.IsEnabled)
            {
                Assert.NotNull(checkBox.InputHitTest(new Point(8, checkBox.ActualHeight / 2)));
                Assert.NotNull(checkBox.InputHitTest(new Point(checkBox.ActualWidth - 2, checkBox.ActualHeight / 2)));
            }
            var origin = checkBox.TransformToAncestor(parent).Transform(new Point());
            var blankPoint = new Point(origin.X + checkBox.ActualWidth + 30, origin.Y + checkBox.ActualHeight / 2);
            Assert.True(blankPoint.X < parent.ActualWidth - 2, $"'{checkBox.Content}' has no right-side blank area to verify.");
            var blankHit = parent.InputHitTest(blankPoint) as DependencyObject;
            Assert.False(IsSelfOrDescendant(blankHit, checkBox), $"Right-side blank area belongs to '{checkBox.Content}'.");
        }

        var shortLabel = checkBoxes.Single(checkBox => Equals(checkBox.Content, LocalizationService.Current["Settings.RememberProfiles"]));
        var longLabel = checkBoxes.Single(checkBox => Equals(checkBox.Content, LocalizationService.Current["Settings.AutoApply"]));
        Assert.True(longLabel.ActualWidth > shortLabel.ActualWidth + 20);

        foreach (var label in new[] { "Settings.CloseToTray", "Settings.ShowInactive", "Settings.HideBrowserAggregate",
                     "Settings.RememberProfiles", "Settings.AutoApply", "Settings.ShowTips" }
                     .Select(key => LocalizationService.Current[key]))
        {
            var checkBox = checkBoxes.Single(candidate => Equals(candidate.Content, label));
            Assert.True(checkBox.IsEnabled, $"'{label}' should be enabled for the interaction regression.");
            var provider = Assert.IsAssignableFrom<IToggleProvider>(
                new CheckBoxAutomationPeer(checkBox).GetPattern(PatternInterface.Toggle));
            var original = checkBox.IsChecked;
            provider.Toggle();
            Assert.NotEqual(original, checkBox.IsChecked);
            provider.Toggle();
            Assert.Equal(original, checkBox.IsChecked);
        }

        var target = checkBoxes.Single(checkBox => Equals(checkBox.Content, LocalizationService.Current["Settings.ShowInactive"]));
        var box = Assert.IsType<Border>(target.Template.FindName("Box", target));
        var root = Assert.IsType<Grid>(target.Template.FindName("Root", target));
        var focusTemplateSetter = Assert.Single(target.FocusVisualStyle!.Setters.OfType<Setter>()
            .Where(setter => setter.Property == Control.TemplateProperty));
        var focusTemplate = Assert.IsType<ControlTemplate>(focusTemplateSetter.Value);
        var focusBorder = Assert.IsType<Border>(focusTemplate.LoadContent());
        Assert.Equal(new Thickness(-3), focusBorder.Margin);
        Assert.Same(DependencyProperty.UnsetValue, focusBorder.ReadLocalValue(FrameworkElement.WidthProperty));
        var initialEnabled = target.IsEnabled;
        var initialChecked = viewModel.ShowInactiveSessions;
        try
        {
            viewModel.ShowInactiveSessions = false;
            await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var uncheckedBounds = Bounds(box, target);
            Assert.Equal(BrushColor(app.FindResource("SurfaceBrush")), BrushColor(box.Background));

            viewModel.ShowInactiveSessions = true;
            await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            Assert.Equal(uncheckedBounds, Bounds(box, target));
            Assert.Equal(BrushColor(app.FindResource("PrimaryDarkBrush")), BrushColor(box.Background));
            Assert.Equal(BrushColor(app.FindResource("PrimaryDarkBrush")), BrushColor(box.BorderBrush));

            target.IsEnabled = false;
            target.UpdateLayout();
            Assert.Equal(BrushColor(app.FindResource("PrimaryDarkBrush")), BrushColor(box.Background));
            Assert.InRange(root.Opacity, 0.4, 0.6);
            target.IsEnabled = true;

            var peer = new CheckBoxAutomationPeer(target);
            var toggleProvider = Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle));
            var beforeAutomation = target.IsChecked;
            toggleProvider.Toggle();
            Assert.NotEqual(beforeAutomation, target.IsChecked);

            target.Focus();
            var beforeSpace = target.IsChecked;
            RaiseKey(target, Key.Space, UIElement.KeyDownEvent);
            RaiseKey(target, Key.Space, UIElement.KeyUpEvent);
            Assert.NotEqual(beforeSpace, target.IsChecked);
            Assert.True(target.ActualWidth < Assert.IsAssignableFrom<FrameworkElement>(target.Parent).ActualWidth * 0.7);
        }
        finally
        {
            target.IsEnabled = initialEnabled;
            viewModel.ShowInactiveSessions = initialChecked;
        }

        viewModel.RememberProfiles = false;
        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.False(checkBoxes.Single(checkBox => Equals(checkBox.Content, LocalizationService.Current["Settings.AutoApply"])).IsEnabled);
        viewModel.RememberProfiles = true;
        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        Assert.Equal(viewModel.StartupEnabled,
            checkBoxes.Single(checkBox => Equals(checkBox.Content, LocalizationService.Current["Settings.StartInTray"])).IsEnabled);

        window.Width = 1240;
        window.Height = 820;
        window.SelectMixerPage();
        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        var sourceList = window.SourceItems;
        sourceList.InvalidateMeasure();
        window.SourceScroller.InvalidateScrollInfo();
        await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle);

        var equalizerSource = Assert.Single(viewModel.Sources.Where(source => source.SupportsEqualizer));
        var wasExpanded = equalizerSource.IsEqualizerExpanded;
        try
        {
            equalizerSource.IsEqualizerExpanded = true;
            await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var equalizerToggle = Assert.Single(Descendants(window).OfType<CheckBox>()
                .Where(checkBox => Equals(checkBox.Content, LocalizationService.Current["Equalizer.Enable"])));
            equalizerToggle.ApplyTemplate();
            equalizerToggle.UpdateLayout();
            Assert.Equal(HorizontalAlignment.Left, equalizerToggle.HorizontalAlignment);
            Assert.DoesNotContain(Descendants(equalizerToggle), element => element is System.Windows.Shapes.Path);
            Assert.True(equalizerToggle.ActualWidth < Assert.IsAssignableFrom<FrameworkElement>(equalizerToggle.Parent).ActualWidth * 0.7);
            Assert.NotNull(equalizerToggle.InputHitTest(new Point(equalizerToggle.ActualWidth - 2, equalizerToggle.ActualHeight / 2)));
            var equalizerProvider = Assert.IsAssignableFrom<IToggleProvider>(
                new CheckBoxAutomationPeer(equalizerToggle).GetPattern(PatternInterface.Toggle));
            var original = equalizerToggle.IsChecked;
            equalizerProvider.Toggle();
            Assert.NotEqual(original, equalizerToggle.IsChecked);
            equalizerProvider.Toggle();
            Assert.Equal(original, equalizerToggle.IsChecked);
        }
        finally
        {
            equalizerSource.IsEqualizerExpanded = wasExpanded;
            await app.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
        }
    }

    private static void RaiseKey(UIElement element, Key key, RoutedEvent routedEvent)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element),
            Environment.TickCount, key) { RoutedEvent = routedEvent };
        element.RaiseEvent(args);
    }

    private static bool IsSelfOrDescendant(DependencyObject? candidate, DependencyObject ancestor)
    {
        while (candidate is not null)
        {
            if (ReferenceEquals(candidate, ancestor)) return true;
            candidate = VisualTreeHelper.GetParent(candidate);
        }
        return false;
    }

    private static System.Windows.Media.Color BrushColor(object brush)
        => Assert.IsType<SolidColorBrush>(brush).Color;

    private static Rect Bounds(FrameworkElement element, Visual ancestor)
        => element.TransformToAncestor(ancestor).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));

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

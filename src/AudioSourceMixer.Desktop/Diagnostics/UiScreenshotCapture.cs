using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AudioSourceMixer.Core.Models;
using AudioSourceMixer.Desktop.ViewModels;

namespace AudioSourceMixer.Desktop.Diagnostics;

internal static class UiScreenshotCapture
{
    public static async Task<IReadOnlyList<string>> CaptureAsync(
        MainWindow window,
        MainViewModel viewModel,
        string outputDirectory,
        CancellationToken cancellationToken = default)
    {
        outputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var files = new List<string>();
        window.Width = 1180;
        window.Height = 760;
        window.SelectMixerPage();

        var ordinary = viewModel.Sources.First(source => source.Snapshot.Kind == AudioSourceKind.WindowsSession);
        ordinary.IsEqualizerExpanded = false;
        await ShowSourceAsync(window, ordinary, cancellationToken);
        files.Add(Save(window, outputDirectory, "01-mixer-ordinary.png"));

        var browser = viewModel.Sources.First(source => source.Snapshot.Kind is AudioSourceKind.ChromeTab or AudioSourceKind.EdgeTab);
        browser.IsEqualizerExpanded = false;
        await ShowSourceAsync(window, browser, cancellationToken);
        files.Add(Save(window, outputDirectory, "02-browser-enhanced.png"));

        browser.IsEqualizerExpanded = true;
        await ShowSourceAsync(window, browser, cancellationToken);
        files.Add(Save(window, outputDirectory, "03-equalizer-expanded.png"));

        window.SelectBrowserSetupPage();
        await SettleAsync(window, cancellationToken);
        files.Add(Save(window, outputDirectory, "04-browser-setup.png"));

        window.SelectSettingsPage();
        await SettleAsync(window, cancellationToken);
        files.Add(Save(window, outputDirectory, "05-settings.png"));

        window.Width = 880;
        window.Height = 600;
        window.SelectMixerPage();
        await ShowSourceAsync(window, browser, cancellationToken);
        files.Add(Save(window, outputDirectory, "06-minimum-window.png"));
        return files;
    }

    private static async Task ShowSourceAsync(MainWindow window, AudioSourceViewModel source, CancellationToken cancellationToken)
    {
        if (window.SourceItems is System.Windows.Controls.ListBox list) list.ScrollIntoView(source);
        await SettleAsync(window, cancellationToken);
    }

    private static async Task SettleAsync(MainWindow window, CancellationToken cancellationToken)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ApplicationIdle, cancellationToken);
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render, cancellationToken);
    }

    private static string Save(Window window, string directory, string fileName)
    {
        window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        visual.InvalidateVisual();
        visual.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(visual);
        var width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            context.DrawRectangle(window.Background, null, bounds);
            context.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top }, null, bounds);
        }
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(directory, fileName);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}

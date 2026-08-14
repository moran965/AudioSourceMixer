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

        foreach (var (width, height, scale, fileName) in new[]
                 {
                     (1180d, 760d, 1d, "05-settings-1180x760-100dpi.png"),
                     (880d, 600d, 1d, "06-settings-880x600-100dpi.png"),
                     (1600d, 900d, 1d, "07-settings-1600x900-100dpi.png"),
                     (1180d, 760d, 1.25d, "08-settings-1180x760-125dpi.png"),
                     (1180d, 760d, 1.5d, "09-settings-1180x760-150dpi.png"),
                     (1180d, 760d, 2d, "10-settings-1180x760-200dpi.png")
                 })
        {
            window.Width = width;
            window.Height = height;
            window.SelectSettingsPage();
            await SettleAsync(window, cancellationToken);
            files.Add(Save(window, outputDirectory, fileName, scale));
        }

        window.Width = 880;
        window.Height = 600;
        window.SelectMixerPage();
        await ShowSourceAsync(window, browser, cancellationToken);
        files.Add(Save(window, outputDirectory, "11-minimum-window.png"));
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

    private static string Save(Window window, string directory, string fileName, double? renderScale = null)
    {
        window.UpdateLayout();
        var visual = (FrameworkElement)window.Content;
        visual.InvalidateVisual();
        visual.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(visual);
        var scaleX = renderScale ?? dpi.DpiScaleX;
        var scaleY = renderScale ?? dpi.DpiScaleY;
        var width = Math.Max(1, (int)Math.Ceiling(visual.ActualWidth * scaleX));
        var height = Math.Max(1, (int)Math.Ceiling(visual.ActualHeight * scaleY));
        var bitmap = new RenderTargetBitmap(width, height, 96 * scaleX, 96 * scaleY, PixelFormats.Pbgra32);
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

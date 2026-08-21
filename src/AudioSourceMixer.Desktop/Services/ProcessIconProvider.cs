using System.Collections.Concurrent;
using System.IO;
using DrawingIcon = System.Drawing.Icon;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioSourceMixer.Core.Models;

namespace AudioSourceMixer.Desktop.Services;

internal static class ProcessIconProvider
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<ImageSource>>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ImageSource ApplicationFallback = CreateFallback(false);
    private static readonly ImageSource BrowserFallback = CreateFallback(true);

    public static ImageSource Fallback(AudioSourceKind kind)
        => kind is AudioSourceKind.ChromeTab or AudioSourceKind.EdgeTab ? BrowserFallback : ApplicationFallback;

    public static Task<ImageSource> GetAsync(AudioSourceSnapshot snapshot)
    {
        var candidate = NormalizeIconPath(snapshot.IconPath) ?? snapshot.ExecutablePath;
        if (string.IsNullOrWhiteSpace(candidate)) return Task.FromResult(Fallback(snapshot.Kind));
        var key = $"{snapshot.Kind}:{candidate}";
        return Cache.GetOrAdd(key, _ => new Lazy<Task<ImageSource>>(
            () => Task.Run(() => Extract(candidate, snapshot.Kind)), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    internal static int CachedIconCount => Cache.Count;

    private static ImageSource Extract(string path, AudioSourceKind kind)
    {
        try
        {
            path = Environment.ExpandEnvironmentVariables(path);
            if (!File.Exists(path)) return Fallback(kind);
            using var icon = DrawingIcon.ExtractAssociatedIcon(path);
            if (icon is null) return Fallback(kind);
            var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Fallback(kind);
        }
    }

    private static string? NormalizeIconPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = value.Trim();
        if (path.StartsWith('@')) path = path[1..];
        var comma = path.LastIndexOf(',');
        if (comma > 1) path = path[..comma];
        return path.Trim(' ', '"');
    }

    private static ImageSource CreateFallback(bool browser)
    {
        var group = new DrawingGroup();
        var fill = new SolidColorBrush(browser ? System.Windows.Media.Color.FromRgb(219, 234, 254) : System.Windows.Media.Color.FromRgb(239, 246, 255));
        var stroke = new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235)), 1.6);
        group.Children.Add(new GeometryDrawing(fill, stroke, new EllipseGeometry(new System.Windows.Point(16, 16), 12, 12)));
        if (browser)
            group.Children.Add(new GeometryDrawing(null, stroke, Geometry.Parse("M 4,16 L 28,16 M 16,4 C 9,10 9,22 16,28 M 16,4 C 23,10 23,22 16,28")));
        else
            group.Children.Add(new GeometryDrawing(null, stroke, Geometry.Parse("M 10,11 L 22,11 L 22,22 L 10,22 Z M 13,8 L 19,8")));
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Point = System.Windows.Point;
using Rect = System.Windows.Rect;
using Size = System.Windows.Size;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;

namespace AudioSourceMixer.Desktop.Views;

internal sealed class SessionDragAdorner : Adorner
{
    private readonly Border _preview;
    private Point _topLeft;

    public SessionDragAdorner(FrameworkElement adornedElement, FrameworkElement card, Point pointer, Point grabOffset)
        : base(adornedElement)
    {
        if (card.ActualWidth <= 0 || card.ActualHeight <= 0)
            throw new InvalidOperationException("The source card must be laid out before creating its drag preview.");

        PreviewSize = new Size(card.ActualWidth, card.ActualHeight);
        var bitmap = CaptureOnce(card);
        _preview = new Border
        {
            Width = PreviewSize.Width,
            Height = PreviewSize.Height,
            Opacity = 0.94,
            IsHitTestVisible = false,
            Background = Brushes.Transparent,
            Effect = new DropShadowEffect
            {
                Color = Color.FromArgb(0x66, 0x0F, 0x17, 0x2A),
                BlurRadius = 20,
                ShadowDepth = 6,
                Opacity = 0.42
            },
            Child = new Image
            {
                Source = bitmap,
                Width = PreviewSize.Width,
                Height = PreviewSize.Height,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false,
                SnapsToDevicePixels = true
            }
        };
        IsHitTestVisible = false;
        Update(pointer, grabOffset);
        AddVisualChild(_preview);
    }

    public Size PreviewSize { get; }
    public Point TopLeft => _topLeft;
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? _preview : throw new ArgumentOutOfRangeException(nameof(index));

    public void Update(Point pointer, Point grabOffset)
    {
        // Keep the full-width card aligned to the list while following the pointer vertically.
        _topLeft = new Point(0, pointer.Y - grabOffset.Y);
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size constraint)
    {
        _preview.Measure(PreviewSize);
        return constraint;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _preview.Arrange(new Rect(_topLeft, PreviewSize));
        return finalSize;
    }

    private static BitmapSource CaptureOnce(FrameworkElement card)
    {
        var dpi = VisualTreeHelper.GetDpi(card);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(card.ActualWidth * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(card.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(card);
        bitmap.Freeze();
        return bitmap;
    }
}

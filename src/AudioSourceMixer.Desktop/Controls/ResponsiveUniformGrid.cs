using System.Windows;
using System.Windows.Controls.Primitives;

namespace AudioSourceMixer.Desktop.Controls;

public sealed class ResponsiveUniformGrid : UniformGrid
{
    public static readonly DependencyProperty CompactThresholdProperty = DependencyProperty.Register(
        nameof(CompactThreshold), typeof(double), typeof(ResponsiveUniformGrid),
        new FrameworkPropertyMetadata(720d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double CompactThreshold
    {
        get => (double)GetValue(CompactThresholdProperty);
        set => SetValue(CompactThresholdProperty, value);
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size constraint)
    {
        var width = double.IsFinite(constraint.Width) ? constraint.Width : ActualWidth;
        Columns = width > 0 && width < CompactThreshold ? 5 : 10;
        Rows = 0;
        return base.MeasureOverride(constraint);
    }
}

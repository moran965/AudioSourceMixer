using System.Windows;
using System.Windows.Controls;

namespace AudioSourceMixer.Desktop.Controls;

public sealed class DeterminateProgressBar : System.Windows.Controls.ProgressBar
{
    private FrameworkElement? _track;
    private FrameworkElement? _indicator;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _track = GetTemplateChild("PART_Track") as FrameworkElement;
        _indicator = GetTemplateChild("PART_Indicator") as FrameworkElement;
        UpdateIndicator();
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        UpdateIndicator();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateIndicator();
    }

    private void UpdateIndicator()
    {
        if (_track is null || _indicator is null || _track.ActualWidth <= 0) return;
        var range = Maximum - Minimum;
        var ratio = range > 0 ? Math.Clamp((Value - Minimum) / range, 0, 1) : 0;
        var width = _track.ActualWidth * ratio;
        _indicator.BeginAnimation(WidthProperty, null);
        _indicator.MinWidth = width;
        _indicator.MaxWidth = width;
        _indicator.Width = width;
        _indicator.InvalidateMeasure();
        _track.InvalidateMeasure();
        InvalidateMeasure();
        _indicator.Measure(new System.Windows.Size(width, _track.ActualHeight));
        _indicator.Arrange(new System.Windows.Rect(0, 0, width, _track.ActualHeight));
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AudioSourceMixer.Desktop.Controls;

public sealed class CenterDetentSlider : Slider
{
    private bool _detented;
    private bool _keyboardInput;
    private bool _adjusting;

    public double EnterThreshold { get; set; } = 5;
    public double ExitThreshold { get; set; } = 8;

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        _keyboardInput = true;
        try { base.OnKeyDown(e); }
        finally { _keyboardInput = false; }
    }

    protected override void OnValueChanged(double oldValue, double newValue)
    {
        base.OnValueChanged(oldValue, newValue);
        if (_adjusting || (!IsMouseCaptureWithin && !_keyboardInput)) return;
        var adjusted = CenterDetent.Apply(newValue, ref _detented, EnterThreshold, ExitThreshold);
        if (Math.Abs(adjusted - newValue) < double.Epsilon) return;
        _adjusting = true;
        try { SetCurrentValue(ValueProperty, adjusted); }
        finally { _adjusting = false; }
    }
}

public static class CenterDetent
{
    public static double Apply(double value, ref bool detented, double enterThreshold = 5, double exitThreshold = 8)
    {
        if (enterThreshold < 0 || exitThreshold <= enterThreshold)
            throw new ArgumentOutOfRangeException(nameof(exitThreshold));
        if (detented)
        {
            if (Math.Abs(value) < exitThreshold) return 0;
            detented = false;
            return value;
        }
        if (Math.Abs(value) <= enterThreshold)
        {
            detented = true;
            return 0;
        }
        return value;
    }
}

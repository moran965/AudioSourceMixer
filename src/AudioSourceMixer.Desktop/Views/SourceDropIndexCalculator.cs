namespace AudioSourceMixer.Desktop.Views;

internal readonly record struct RealizedItemBounds(int Index, double Top, double Height)
{
    public double Midpoint => Top + Height / 2;
    public double Bottom => Top + Height;
}

internal static class SourceDropIndexCalculator
{
    public static int Calculate(IReadOnlyList<RealizedItemBounds> realizedItems, double pointerY, int itemCount)
    {
        if (itemCount <= 0) return 0;
        foreach (var item in realizedItems.OrderBy(item => item.Index))
            if (pointerY < item.Midpoint) return Math.Clamp(item.Index, 0, itemCount);
        return realizedItems.Count == 0
            ? itemCount
            : Math.Clamp(realizedItems.Max(item => item.Index) + 1, 0, itemCount);
    }
}

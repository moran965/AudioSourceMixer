using AudioSourceMixer.Desktop.Views;

namespace AudioSourceMixer.Desktop.Tests;

public sealed class SourceDropIndexCalculatorTests
{
    private static readonly RealizedItemBounds[] UnequalCards =
    [
        new(0, 10, 100),
        new(1, 120, 260),
        new(2, 390, 80)
    ];

    [Theory]
    [InlineData(0, 0)]
    [InlineData(59, 0)]
    [InlineData(60, 1)]
    [InlineData(115, 1)]
    [InlineData(249, 1)]
    [InlineData(250, 2)]
    [InlineData(430, 3)]
    [InlineData(1000, 3)]
    public void PointerAndCardMidpointsProduceStableInsertionIndex(double pointerY, int expected)
        => Assert.Equal(expected, SourceDropIndexCalculator.Calculate(UnequalCards, pointerY, 3));

    [Fact]
    public void VirtualizedRangeUsesRealItemIndicesAndSupportsListEnd()
    {
        RealizedItemBounds[] realized = [new(7, 0, 90), new(8, 100, 110)];
        Assert.Equal(7, SourceDropIndexCalculator.Calculate(realized, 20, 12));
        Assert.Equal(8, SourceDropIndexCalculator.Calculate(realized, 70, 12));
        Assert.Equal(9, SourceDropIndexCalculator.Calculate(realized, 300, 12));
        Assert.Equal(12, SourceDropIndexCalculator.Calculate([], 300, 12));
    }
}

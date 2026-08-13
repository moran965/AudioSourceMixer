using AudioSourceMixer.Core.Infrastructure;

namespace AudioSourceMixer.Core.Tests;

public sealed class AsyncDebouncerTests
{
    [Fact]
    public async Task OnlyLatestScheduledActionRuns()
    {
        using var debouncer = new AsyncDebouncer(TimeSpan.FromMilliseconds(30));
        var observed = 0;
        debouncer.Schedule(_ => { observed = 1; return Task.CompletedTask; });
        debouncer.Schedule(_ => { observed = 2; return Task.CompletedTask; });
        await Task.Delay(100);
        Assert.Equal(2, observed);
    }

    [Fact]
    public async Task CancelPendingWaitsForRunningActionAndPreventsDelayedWrite()
    {
        await using var debouncer = new AsyncDebouncer(TimeSpan.FromMilliseconds(20));
        var writes = 0;
        debouncer.Schedule(async token =>
        {
            await Task.Delay(200, token);
            Interlocked.Increment(ref writes);
        });
        await Task.Delay(40);
        await debouncer.CancelPendingAsync();
        await Task.Delay(220);
        Assert.Equal(0, writes);
    }
}

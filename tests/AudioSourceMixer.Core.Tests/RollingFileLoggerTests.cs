using AudioSourceMixer.Core.Infrastructure;

namespace AudioSourceMixer.Core.Tests;

public sealed class RollingFileLoggerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AudioSourceMixer.LoggerTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ErrorWritesCompleteExceptionIncludingInnerExceptionAndStack()
    {
        var logger = new RollingFileLogger(_directory);
        try
        {
            ThrowNestedException();
        }
        catch (Exception exception)
        {
            logger.Error("startup failed", exception);
        }

        var log = File.ReadAllText(Path.Combine(_directory, "AudioSourceMixer.log"));
        Assert.Contains("System.InvalidOperationException: outer failure", log);
        Assert.Contains("System.ArgumentException: inner failure", log);
        Assert.Contains(nameof(ThrowNestedException), log);
    }

    private static void ThrowNestedException()
    {
        try { throw new ArgumentException("inner failure"); }
        catch (Exception exception) { throw new InvalidOperationException("outer failure", exception); }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

namespace AudioSourceMixer.Core.Infrastructure;

public sealed class RollingFileLogger
{
    private readonly string _path;
    private readonly long _maximumBytes;
    private readonly object _gate = new();

    public RollingFileLogger(string directory, long maximumBytes = 1_048_576)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "AudioSourceMixer.log");
        _maximumBytes = maximumBytes;
    }

    public void Info(string message) => Write("INFO", message);
    public void Error(string message, Exception? exception = null)
        => Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");

    private void Write(string level, string message)
    {
        lock (_gate)
        {
            if (File.Exists(_path) && new FileInfo(_path).Length >= _maximumBytes)
            {
                File.Move(_path, _path + ".1", true);
            }
            File.AppendAllText(_path, $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
        }
    }
}

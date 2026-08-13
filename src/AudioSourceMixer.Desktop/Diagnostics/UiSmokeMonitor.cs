using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Windows.Data;

namespace AudioSourceMixer.Desktop.Diagnostics;

internal sealed class UiSmokeMonitor : IDisposable
{
    private readonly ConcurrentQueue<string> _faults = new();
    private readonly BindingTraceListener _bindingListener = new();
    private readonly SourceLevels _previousBindingLevel;

    public UiSmokeMonitor()
    {
        var source = PresentationTraceSources.DataBindingSource;
        _previousBindingLevel = source.Switch.Level;
        source.Switch.Level = SourceLevels.Error;
        source.Listeners.Add(_bindingListener);
    }

    public IReadOnlyList<string> Faults
        => _faults.Concat(_bindingListener.Messages.Select(message => $"WPF data binding: {message}")).ToArray();

    public void Record(string category, Exception exception)
        => _faults.Enqueue($"{category}:{Environment.NewLine}{exception}");

    public void ThrowIfFailed()
    {
        var failures = Faults;
        if (failures.Count > 0)
            throw new InvalidOperationException($"UI smoke test captured {failures.Count} failure(s):{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    public void Dispose()
    {
        var source = PresentationTraceSources.DataBindingSource;
        source.Listeners.Remove(_bindingListener);
        source.Switch.Level = _previousBindingLevel;
        _bindingListener.Dispose();
    }

    private sealed class BindingTraceListener : TraceListener
    {
        private readonly ConcurrentQueue<string> _messages = new();
        private readonly StringBuilder _partial = new();
        private readonly object _gate = new();

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_gate)
                {
                    var messages = _messages.ToList();
                    if (_partial.Length > 0) messages.Add(_partial.ToString());
                    return messages.Where(message => !string.IsNullOrWhiteSpace(message)).Distinct().ToArray();
                }
            }
        }

        public override void Write(string? message)
        {
            if (string.IsNullOrEmpty(message)) return;
            lock (_gate) _partial.Append(message);
        }

        public override void WriteLine(string? message)
        {
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(message)) _partial.Append(message);
                if (_partial.Length == 0) return;
                _messages.Enqueue(_partial.ToString());
                _partial.Clear();
            }
        }
    }
}

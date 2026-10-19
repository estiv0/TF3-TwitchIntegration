using System.Collections.Concurrent;

namespace Tf3Bridge.Ui;

/// <summary>Collects log lines from any thread; the window drains them on its own timer.</summary>
public sealed class UiLogProvider : ILoggerProvider
{
    private const int MaxQueued = 2000;
    private readonly ConcurrentQueue<LogLine> _queue = new();

    public bool TryDequeue(out LogLine line) => _queue.TryDequeue(out line!);

    public ILogger CreateLogger(string categoryName) => new UiLogger(this);

    public void Dispose() { }

    private void Add(LogLine line)
    {
        _queue.Enqueue(line);
        while (_queue.Count > MaxQueued) _queue.TryDequeue(out _); // window minimized for a long time - don't grow forever
    }

    private sealed class UiLogger(UiLogProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var text = formatter(state, exception);
            if (exception is not null) text += $" ({exception.GetType().Name}: {exception.Message})";
            provider.Add(new LogLine(DateTime.Now, logLevel, text));
        }
    }
}

public readonly record struct LogLine(DateTime Time, LogLevel Level, string Text)
{
    public override string ToString()
    {
        var level = Level switch
        {
            LogLevel.Warning => "UWAGA ",
            LogLevel.Error or LogLevel.Critical => Loc.T("ERROR ", "BŁĄD  "),
            _ => "",
        };
        return $"{Time:HH:mm:ss}  {level}{Text}";
    }
}

using Microsoft.Extensions.Logging;

namespace Pbs.Service;

public sealed class RollingFileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly object _gate = new();
    private bool _disposed;
    private bool _writeFailed;

    public ILogger CreateLogger(string categoryName) => new RollingFileLogger(this, categoryName);

    public void Dispose()
    {
        _disposed = true;
    }

    private void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        // File logging is best-effort: an unwritable log directory (or any IO error) must never
        // propagate into the logging pipeline and take the service down. On the first failure we
        // latch off file logging; the console/Event Log sinks keep working.
        if (_disposed || _writeFailed)
        {
            return;
        }

        var line = $"{DateTimeOffset.UtcNow:O} [{level}] {category} {eventId.Id}: {message}";
        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        try
        {
            var path = Path.Combine(directory, $"service-{DateTime.UtcNow:yyyyMMdd}.log");
            lock (_gate)
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _writeFailed = true;
            Console.Error.WriteLine($"pbs-client-win: file logging to '{directory}' disabled ({ex.Message}); continuing with console/Event Log only.");
        }
    }

    private sealed class RollingFileLogger(RollingFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
        }
    }
}

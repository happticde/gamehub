using Microsoft.Extensions.Logging;

namespace GameHub.Tests.Helpers;

internal sealed record RecordedLog(
    LogLevel Level,
    string Message,
    Exception? Exception,
    IReadOnlyList<KeyValuePair<string, object?>> Properties);

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<RecordedLog> Records { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.ToArray()
            : [];
        Records.Add(new RecordedLog(logLevel, formatter(state, exception), exception, properties));
    }
}

internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly RecordingLogger<object> _logger = new();

    public List<RecordedLog> Records => _logger.Records;

    public ILogger CreateLogger(string categoryName) => _logger;

    public void Dispose() { }
}

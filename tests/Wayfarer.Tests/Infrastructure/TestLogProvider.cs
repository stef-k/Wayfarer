using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Captures structured server log output for focused diagnostics assertions.</summary>
public sealed class TestLogProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<TestLogEntry> _entries = new();

    /// <summary>Gets captured log entries in emission order.</summary>
    public IReadOnlyCollection<TestLogEntry> Entries => _entries.ToArray();

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new TestLogger(categoryName, _entries);

    /// <inheritdoc />
    public void Dispose() { }

    /// <summary>Represents one captured server log entry, including its stable identifier and structured fields.</summary>
    public sealed record TestLogEntry(
        LogLevel Level,
        string Category,
        EventId EventId,
        IReadOnlyDictionary<string, object?> Fields,
        string Message,
        Exception? Exception);

    /// <summary>Writes entries to the owning provider without filtering them out.</summary>
    private sealed class TestLogger(string categoryName, ConcurrentQueue<TestLogEntry> entries) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = state is IEnumerable<KeyValuePair<string, object?>> structuredState
                ? structuredState
                    .Where(field => field.Key != "{OriginalFormat}")
                    .ToDictionary(field => field.Key, field => field.Value)
                : new Dictionary<string, object?>();

            entries.Enqueue(new TestLogEntry(
                logLevel,
                categoryName,
                eventId,
                fields,
                formatter(state, exception),
                exception));
        }
    }
}

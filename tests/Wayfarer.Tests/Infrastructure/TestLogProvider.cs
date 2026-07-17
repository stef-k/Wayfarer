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

    /// <summary>Represents one captured server log entry.</summary>
    public sealed record TestLogEntry(LogLevel Level, string Category, string Message, Exception? Exception);

    /// <summary>Writes entries to the owning provider without filtering them out.</summary>
    private sealed class TestLogger(string categoryName, ConcurrentQueue<TestLogEntry> entries) : ILogger
    {
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new TestLogEntry(logLevel, categoryName, formatter(state, exception), exception));
    }
}

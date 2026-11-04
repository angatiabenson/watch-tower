using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace WatchTower.Tests.Hosting;

/// <summary>One formatted log entry captured by <see cref="RecordingLoggerProvider"/>.</summary>
internal sealed record LogEntry(string Category, LogLevel Level, string Message);

/// <summary>Captures every log entry of every category, for assertions on log output.</summary>
internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public IEnumerable<LogEntry> At(LogLevel level) => Entries.Where(e => e.Level == level);

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

    /// <summary>A typed logger writing into this provider.</summary>
    public ILogger<T> CreateLogger<T>() => new TypedLogger<T>(CreateLogger(typeof(T).FullName!));

    public void Dispose() { }

    private sealed class Logger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(new LogEntry(category, logLevel, formatter(state, exception)));
    }

    private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
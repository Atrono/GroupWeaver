using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.Extensions.Logging;

namespace GroupWeaver.App.Tests.Diagnostics;

/// <summary>
/// In-memory <see cref="ILoggerFactory"/> for the ADR-037 event-contract tests: captures every
/// structured log call (category, level, <see cref="EventId.Name"/>, the state's key/value pairs,
/// exception, formatted message) so tests can assert the EVENT vocabulary — never file I/O. This is
/// the "test ILoggerFactory capturing events in-memory" seam the WP1 test plan names; it is
/// deliberately dumb (no filtering, no formatting policy) so it can never mask a product bug.
/// </summary>
internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    private readonly object _gate = new();
    private readonly List<CapturedLogEntry> _entries = new();

    /// <summary>A point-in-time copy of everything captured so far (thread-safe).</summary>
    public IReadOnlyList<CapturedLogEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    /// <summary>The captured entries whose <see cref="EventId.Name"/> equals
    /// <paramref name="eventName"/>, in capture order.</summary>
    public IReadOnlyList<CapturedLogEntry> EntriesNamed(string eventName) =>
        Entries.Where(e => e.EventName == eventName).ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private void Add(CapturedLogEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    private sealed class CapturingLogger(CapturingLoggerFactory owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            // Mirror the sink's extraction: the structured pairs minus the template itself.
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IReadOnlyList<KeyValuePair<string, object?>> pairs)
            {
                foreach (var pair in pairs)
                {
                    if (pair.Key != "{OriginalFormat}")
                    {
                        fields[pair.Key] = pair.Value;
                    }
                }
            }

            owner.Add(new CapturedLogEntry(
                category, logLevel, eventId.Name ?? "", fields, exception, formatter(state, exception)));
        }
    }
}

/// <summary>One captured structured log call — the projection the event-contract tests assert on.</summary>
internal sealed record CapturedLogEntry(
    string Category,
    LogLevel Level,
    string EventName,
    IReadOnlyDictionary<string, object?> Fields,
    Exception? Exception,
    string Message);

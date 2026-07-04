using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace GroupWeaver.App.Diagnostics;

/// <summary>
/// The hand-rolled JSONL file sink (ADR-037 D1/D3): one strict-STJ object per line to
/// <c>%APPDATA%\GroupWeaver\logs\gw-&lt;utcstamp&gt;-&lt;pid&gt;.jsonl</c> (directory overridable via
/// <c>GROUPWEAVER_LOG_DIR</c> — the E2E harness seam), fixed leading fields
/// <c>ts, lvl, cat, evt, sid</c>, then the event's structured payload, then an optional
/// <c>ex {type, msgScrubbed, stack}</c>. A bounded channel decouples callers from disk: overflow
/// DROPS (counted; one <c>LogBackpressureDropped</c> Warn when the writer catches up), a single
/// background task writes, flushing every ~500 ms and immediately on Warning+. Files roll at
/// 5 MB to <c>-part2</c>/<c>-part3</c>…; startup prunes retention to 10 files / 20 MB.
///
/// <para><b>NEVER-THROW everywhere:</b> a logging failure must never surface as an app failure —
/// creation failure yields <c>null</c> (<see cref="TryCreate"/>), a mid-run write failure turns
/// the sink into a silent discard. The encoder is the STRICT default <see cref="JavaScriptEncoder"/>
/// (never a relaxed one — the recurring security-finding class, ADR-032/ADR-037 Security notes).
/// The sink only WRITES — nothing reads logs back into the app; no network, no AD.</para>
///
/// <para>Implements <see cref="ILoggerFactory"/> directly (a one-provider process — pulling in the
/// full <c>Microsoft.Extensions.Logging</c> factory stack for one sink contradicts ADR-037 D1's
/// single-dependency decision); <see cref="ILoggerFactory.AddProvider"/> is a no-op.</para>
/// </summary>
public sealed class FileLogSink : ILoggerFactory, ILoggerProvider
{
    internal const long DefaultMaxFileBytes = 5 * 1024 * 1024;
    internal const int DefaultRetainFiles = 10;
    internal const long DefaultRetainTotalBytes = 20 * 1024 * 1024;
    internal const int DefaultChannelCapacity = 4096;

    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Payload keys that would collide with the fixed leading fields are skipped.</summary>
    private static readonly HashSet<string> ReservedKeys =
        new(["ts", "lvl", "cat", "evt", "sid", "msg", "ex"], StringComparer.OrdinalIgnoreCase);

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        // The STRICT default encoder, explicit so a future edit cannot silently relax it.
        Encoder = JavaScriptEncoder.Default,
    };

    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Channel<LogEvent> _channel;
    private readonly Task _writerTask;
    private readonly string _directory;
    private readonly string _baseFileName;
    private readonly LogLevel _minLevel;
    private readonly long _maxFileBytes;
    private readonly string _sid;
    private readonly ArrayBufferWriter<byte> _buffer = new(4096);
    private FileStream? _stream;
    private long _bytesWritten;
    private int _part = 1;
    private int _dropped;
    private volatile bool _disposed;

    /// <summary>Creates the production sink, or <c>null</c> when the log directory/file cannot be
    /// created (never throws — ADR-037 D3). Prunes retention before opening the new file.</summary>
    public static FileLogSink? TryCreate(LogLevel minLevel, Session session)
    {
        try
        {
            var directory = ResolveLogDirectory();
            Directory.CreateDirectory(directory);
            PruneOldLogs(directory, DefaultRetainFiles, DefaultRetainTotalBytes);
            return new FileLogSink(
                directory, minLevel, session, DefaultMaxFileBytes, DefaultChannelCapacity);
        }
        catch
        {
            return null; // never-throw: the app runs unlogged rather than not at all.
        }
    }

    /// <summary>Test seam (caps injectable — roll/retention tests); throws on I/O failure, which
    /// only <see cref="TryCreate"/> swallows.</summary>
    internal FileLogSink(
        string directory, LogLevel minLevel, Session session, long maxFileBytes, int channelCapacity)
    {
        _directory = directory;
        _minLevel = minLevel;
        _sid = session.Sid;
        _maxFileBytes = maxFileBytes;
        _baseFileName =
            $"gw-{session.StartedUtc.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}-{Environment.ProcessId}";
        CurrentLogFilePath = Path.Combine(directory, _baseFileName + ".jsonl");
        _stream = new FileStream(CurrentLogFilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _channel = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(channelCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait, // TryWrite fails when full => counted drop
        });
        _writerTask = Task.Run(WriteLoopAsync);
    }

    /// <summary>The file currently being written (advances on a 5 MB roll) — the crash marker's
    /// <c>logFile</c> field.</summary>
    public string CurrentLogFilePath { get; private set; }

    /// <summary>The logs directory: <c>GROUPWEAVER_LOG_DIR</c> when set (the harness seam), else
    /// <c>%APPDATA%\GroupWeaver\logs</c> — the repo-wide user-persistence convention (ADR-008).</summary>
    public static string ResolveLogDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("GROUPWEAVER_LOG_DIR");
        return string.IsNullOrWhiteSpace(overrideDir)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GroupWeaver",
                "logs")
            : overrideDir;
    }

    /// <summary>Startup retention prune (ADR-037 D3): keeps the newest <paramref name="retainFiles"/>
    /// log files and at most <paramref name="retainTotalBytes"/> total; never throws, per-file
    /// best-effort. Crash markers are NOT pruned (D7: the next start reports them).</summary>
    internal static void PruneOldLogs(string directory, int retainFiles, long retainTotalBytes)
    {
        try
        {
            var files = new DirectoryInfo(directory)
                .GetFiles("gw-*.jsonl")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
            long totalBytes = 0;
            var kept = 0;
            foreach (var file in files)
            {
                totalBytes += file.Length;
                kept++;
                if (kept <= retainFiles && totalBytes <= retainTotalBytes)
                {
                    continue;
                }

                try
                {
                    file.Delete();
                }
                catch
                {
                    // Best-effort per file (locked by a tail -f, ...): retention is advisory.
                }
            }
        }
        catch
        {
            // Never-throw: an unreadable directory just skips the prune.
        }
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, static (category, sink) => new FileLogger(sink, category), this);

    /// <summary>No-op: this sink IS the one provider (see the class remarks).</summary>
    public void AddProvider(ILoggerProvider provider)
    {
    }

    /// <summary>Completes the channel and waits (bounded, 2 s — the crash-flush deadline, ADR-037
    /// D7) for the writer to drain + flush + close. Idempotent, never throws.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _channel.Writer.TryComplete();
            _writerTask.Wait(ShutdownTimeout);
        }
        catch
        {
            // Never-throw: a stuck writer is abandoned; process exit closes the handle.
        }
    }

    private bool IsEnabled(LogLevel level) =>
        level != LogLevel.None && level >= _minLevel && !_disposed;

    private void Enqueue(in LogEvent logEvent)
    {
        if (!_channel.Writer.TryWrite(logEvent))
        {
            Interlocked.Increment(ref _dropped); // bounded channel full: drop, count, report later
        }
    }

    // ---------- background writer (single reader) ----------
    // WP2 (#241): both breadcrumbs honored — the renderer's per-chunk/per-message Trace call
    // sites use LoggerMessage source-gen (CytoscapeGraphRenderer), and the cadence flush now
    // also enforces its ~500 ms deadline at drain time (lastFlush below), so continuous Trace
    // traffic can no longer starve it (the WhenAny race alone never fired without a quiet gap).

    private async Task WriteLoopAsync()
    {
        try
        {
            var reader = _channel.Reader;
            Task<bool>? pendingWait = null;
            var needsFlush = false;
            var lastFlush = Environment.TickCount64;
            while (true)
            {
                var waitTask = pendingWait ?? reader.WaitToReadAsync().AsTask();
                pendingWait = null;
                if (needsFlush)
                {
                    // Cadence flush (~500 ms, ADR-037 D3): wait bounded, flush, keep waiting.
                    var winner = await Task.WhenAny(waitTask, Task.Delay(FlushInterval))
                        .ConfigureAwait(false);
                    if (winner != waitTask)
                    {
                        TryFlush();
                        needsFlush = false;
                        lastFlush = Environment.TickCount64;
                        pendingWait = waitTask;
                        continue;
                    }
                }

                if (!await waitTask.ConfigureAwait(false))
                {
                    break; // channel completed (Dispose)
                }

                var urgent = false;
                while (reader.TryRead(out var logEvent))
                {
                    WriteLine(logEvent);
                    urgent |= logEvent.Level >= LogLevel.Warning;
                }

                // Caught up: report drops ONCE per backpressure episode (ADR-037 D3).
                var dropped = Interlocked.Exchange(ref _dropped, 0);
                if (dropped > 0)
                {
                    WriteLine(new LogEvent(
                        DateTimeOffset.UtcNow, LogLevel.Warning, "App.Diagnostics",
                        "LogBackpressureDropped",
                        [new KeyValuePair<string, object?>("dropped", dropped)],
                        Msg: null, ExType: null, ExMessage: null, ExStack: null));
                    urgent = true;
                }

                // WP2 (#241): urgent (Warning+) flushes immediately; otherwise the ~500 ms
                // cadence deadline is ALSO checked here at drain time — under continuous
                // (Trace-volume) traffic the reader always wins the WhenAny race above, so
                // without this check the cadence flush would starve until the next quiet gap.
                if (urgent || Environment.TickCount64 - lastFlush >= (long)FlushInterval.TotalMilliseconds)
                {
                    TryFlush();
                    needsFlush = false;
                    lastFlush = Environment.TickCount64;
                }
                else
                {
                    needsFlush = true;
                }
            }

            TryFlush();
        }
        catch
        {
            // NEVER-THROW: a dead writer must not fault the app; Enqueue keeps discarding.
        }
        finally
        {
            try
            {
                _stream?.Dispose();
            }
            catch
            {
                // Never-throw teardown.
            }

            _stream = null;
        }
    }

    private void TryFlush()
    {
        try
        {
            _stream?.Flush();
        }
        catch
        {
            DisableWriter();
        }
    }

    private void WriteLine(in LogEvent logEvent)
    {
        if (_stream is null)
        {
            return; // broken sink: silent discard (never-throw)
        }

        try
        {
            _buffer.Clear();
            using (var json = new Utf8JsonWriter(_buffer, WriterOptions))
            {
                json.WriteStartObject();
                json.WriteString("ts", logEvent.Ts.UtcDateTime); // ISO 8601, trailing Z
                json.WriteString("lvl", LevelName(logEvent.Level));
                json.WriteString("cat", logEvent.Category);
                json.WriteString("evt", logEvent.Evt);
                json.WriteString("sid", _sid);
                foreach (var field in logEvent.Fields)
                {
                    if (!ReservedKeys.Contains(field.Key))
                    {
                        WriteField(json, field.Key, field.Value);
                    }
                }

                if (logEvent.Msg is not null)
                {
                    json.WriteString("msg", logEvent.Msg);
                }

                if (logEvent.ExType is not null)
                {
                    json.WriteStartObject("ex");
                    json.WriteString("type", logEvent.ExType);
                    json.WriteString("msgScrubbed", logEvent.ExMessage);
                    json.WriteString("stack", logEvent.ExStack);
                    json.WriteEndObject();
                }

                json.WriteEndObject();
            }

            _stream.Write(_buffer.WrittenSpan);
            _stream.WriteByte((byte)'\n');
            _bytesWritten += _buffer.WrittenCount + 1;
            if (_bytesWritten >= _maxFileBytes)
            {
                Roll();
            }
        }
        catch
        {
            DisableWriter();
        }
    }

    /// <summary>5 MB roll (ADR-037 D3): close the current file, continue in
    /// <c>&lt;base&gt;-part2.jsonl</c>, <c>-part3</c>, … Failure disables via the caller's catch.
    /// <see cref="CurrentLogFilePath"/> is published only AFTER the new stream exists — a failed
    /// roll must never leave the crash marker's <c>logFile</c> naming a never-created file.</summary>
    private void Roll()
    {
        _stream!.Flush();
        _stream.Dispose();
        _stream = null; // a throw below leaves a clean "disabled" state, never a disposed stream
        _part++;
        var path = Path.Combine(_directory, $"{_baseFileName}-part{_part}.jsonl");
        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        CurrentLogFilePath = path;
        _bytesWritten = 0;
    }

    /// <summary>A failed write/flush turns the sink into a silent discard for the rest of the
    /// process — never an app failure, never a retry storm.</summary>
    private void DisableWriter()
    {
        try
        {
            _stream?.Dispose();
        }
        catch
        {
            // Never-throw teardown.
        }

        _stream = null;
    }

    private static void WriteField(Utf8JsonWriter json, string name, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNull(name);
                break;
            case bool b:
                json.WriteBoolean(name, b);
                break;
            case byte or sbyte or short or ushort or int or uint or long:
                json.WriteNumber(name, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ulong ul:
                json.WriteNumber(name, ul);
                break;
            case float f:
                json.WriteNumber(name, f);
                break;
            case double d:
                json.WriteNumber(name, d);
                break;
            case decimal m:
                json.WriteNumber(name, m);
                break;
            case DateTime dt:
                json.WriteString(name, dt);
                break;
            case DateTimeOffset dto:
                json.WriteString(name, dto);
                break;
            case string s:
                json.WriteString(name, s);
                break;
            case IEnumerable<string?> strings:
                json.WriteStartArray(name);
                foreach (var item in strings)
                {
                    json.WriteStringValue(item);
                }

                json.WriteEndArray();
                break;
            default:
                json.WriteString(name, Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
                break;
        }
    }

    /// <summary>Pinned wire names for <c>lvl</c> (the E2E triager's grep contract).</summary>
    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "trace",
        LogLevel.Debug => "debug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "error",
        _ => "critical",
    };

    /// <summary>One fully-materialized line, captured on the CALLER thread (values must not
    /// change under the writer).</summary>
    private readonly record struct LogEvent(
        DateTimeOffset Ts,
        LogLevel Level,
        string Category,
        string Evt,
        KeyValuePair<string, object?>[] Fields,
        string? Msg,
        string? ExType,
        string? ExMessage,
        string? ExStack);

    /// <summary>Per-category logger: extracts the stable <c>evt</c> name from
    /// <see cref="EventId.Name"/> (the ADR-037 D4 machine contract; a nameless event falls back to
    /// <c>evt="Message"</c> carrying the scrubbed formatted text), materializes the structured
    /// state pairs as the payload, and enqueues. Never throws.</summary>
    private sealed class FileLogger(FileLogSink sink, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => sink.IsEnabled(logLevel);

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

            try
            {
                string evt;
                string? msg;
                if (!string.IsNullOrEmpty(eventId.Name))
                {
                    evt = eventId.Name;
                    msg = null; // structured fields carry the data; the template is transport
                }
                else
                {
                    evt = "Message";
                    msg = Redactor.Scrub(formatter(state, exception));
                }

                sink.Enqueue(new LogEvent(
                    DateTimeOffset.UtcNow, logLevel, category, evt, ExtractFields(state), msg,
                    exception?.GetType().FullName,
                    exception is null ? null : Redactor.Scrub(exception.Message),
                    exception?.StackTrace));
            }
            catch
            {
                // NEVER-THROW (ADR-037 D3): a logging failure must never become an app failure.
            }
        }

        private static KeyValuePair<string, object?>[] ExtractFields<TState>(TState state)
        {
            if (state is not IReadOnlyList<KeyValuePair<string, object?>> values || values.Count == 0)
            {
                return [];
            }

            var fields = new List<KeyValuePair<string, object?>>(values.Count);
            foreach (var pair in values)
            {
                if (pair.Key != "{OriginalFormat}")
                {
                    fields.Add(pair);
                }
            }

            return [.. fields];
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}

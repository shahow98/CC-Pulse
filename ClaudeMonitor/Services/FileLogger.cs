using System;
using System.IO;
using System.Threading;

namespace ClaudeMonitor.Services;

/// <summary>
/// A minimal thread-safe file logger that writes to
/// <c>~/.cc-pulse/logs/cc-pulse-YYYY-MM-DD.log</c>. Intended for runtime
/// diagnosis of the Phase 2 Hook/Transcript fusion state machine: anomalies,
/// state transitions, and tailer/reconciler events that are otherwise only
/// visible via <c>Debug.WriteLine</c> (which requires an attached debugger).
///
/// Each line is timestamped (local time, with UTC offset) and tagged with a
/// level. The log file is rolled daily by date in the filename, so no
/// runtime rotation is needed. Writes are serialized by a lock; the file is
/// opened/closed per write (append) so a crash never leaves a half-written
/// buffer unflushed and the file is always readable while CC-Pulse runs.
///
/// All methods are no-throw: logging must never break the host. If the log
/// directory cannot be created or written, calls silently no-op.
/// </summary>
public static class FileLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cc-pulse", "logs");

    private static readonly object _lock = new();
    private static bool _disabled;

    /// <summary>
    /// Log an informational message. Prefer <see cref="Warn"/> or
    /// <see cref="Error"/> for anomalies so they stand out when grepping.
    /// </summary>
    public static void Info(string message) => Write("INFO", message);

    /// <summary>Log a warning (e.g. a recoverable anomaly or unexpected but handled state).</summary>
    public static void Warn(string message) => Write("WARN", message);

    /// <summary>Log an error (e.g. an exception caught in a background path).</summary>
    public static void Error(string message) => Write("ERROR", message);

    /// <summary>
    /// Log a state-machine anomaly with the <c>ANOMALY</c> tag so the
    /// anomaly stream can be grepped independently of other noise:
    /// <c>grep ANOMALY cc-pulse-*.log</c>.
    /// </summary>
    public static void Anomaly(string message) => Write("ANOMALY", message);

    private static void Write(string level, string message)
    {
        if (_disabled) return;
        try
        {
            // Local time with UTC offset — easier to correlate with hook
            // events and transcript timestamps the user observes in real time.
            var now = DateTime.Now;
            var line = $"{now:yyyy-MM-dd HH:mm:ss.fff}{now:zzz} [{level}] {message}{Environment.NewLine}";

            lock (_lock)
            {
                Directory.CreateDirectory(LogDir);
                var path = Path.Combine(LogDir, $"cc-pulse-{now:yyyy-MM-dd}.log");
                File.AppendAllText(path, line);
            }
        }
        catch
        {
            // If logging itself fails (disk full, permissions), disable so we
            // do not spam exceptions on every subsequent call. The host keeps
            // running; Debug.WriteLine still fires for an attached debugger.
            _disabled = true;
        }
    }
}

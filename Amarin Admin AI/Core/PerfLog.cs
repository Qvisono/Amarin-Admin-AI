using System.Diagnostics;
using System.Globalization;

namespace Amarin.Core;

/// <summary>
/// Opt-in timings for AMARIN_PERF_LOG. Never writes to the Spectre REPL.
/// Enabled when the env var is 1/true/yes/on. Logs to stderr and
/// %LOCALAPPDATA%\AmarinAdminAI\perf.log.
/// </summary>
internal static class PerfLog
{
    private static readonly object Gate = new();
    private static readonly bool Enabled;
    private static readonly string? FilePath;

    static PerfLog()
    {
        var value = Environment.GetEnvironmentVariable("AMARIN_PERF_LOG");
        Enabled = value is "1" or "true" or "TRUE" or "yes" or "YES" or "on" or "ON";
        if (!Enabled)
        {
            return;
        }

        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AmarinAdminAI");
            Directory.CreateDirectory(dir);
            FilePath = Path.Combine(dir, "perf.log");
        }
        catch
        {
            FilePath = null;
        }
    }

    public static bool IsEnabled => Enabled;

    public static void Write(string message)
    {
        if (!Enabled)
        {
            return;
        }

        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}");

        lock (Gate)
        {
            try
            {
                Console.Error.WriteLine(line);
            }
            catch
            {
                // ignore stderr failures
            }

            if (FilePath is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch
            {
                // ignore file failures
            }
        }
    }

    public static IDisposable Measure(string name)
    {
        if (!Enabled)
        {
            return NoopDisposable.Instance;
        }

        return new TimerScope(name);
    }

    private sealed class TimerScope : IDisposable
    {
        private readonly string _name;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _disposed;

        public TimerScope(string name) => _name = name;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Write($"{_name}_ms={_stopwatch.ElapsedMilliseconds}");
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}

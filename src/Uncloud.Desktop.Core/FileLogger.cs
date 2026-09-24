using Microsoft.Extensions.Logging;

namespace Uncloud.Desktop;

/// <summary>
/// The app's log, in its own folder: a menu-bar app has no console, and "it stopped syncing"
/// needs something to look at. Trimmed when it grows, so it never fills a disk.
/// </summary>
public sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    private const long Limit = 4 * 1024 * 1024;
    private readonly Lock _lock = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string category, LogLevel level, string message, Exception? error)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length > Limit)
                    File.Move(path, path + ".1", overwrite: true);
                File.AppendAllText(path,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level,-11} {category}: {message}{(error is null ? "" : Environment.NewLine + error)}{Environment.NewLine}");
            }
            catch (IOException) { }
        }
    }

    public void Dispose() { }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}

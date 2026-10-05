using MadModStudio.Core.Security;
using Microsoft.Extensions.Logging;

namespace MadModStudio.App.Services;

/// <summary>Minimal rolling-by-day file logger. Messages pass through the secret redactor.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly object _gate = new();

    public FileLoggerProvider(string directory)
    {
        _dir = directory;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    internal void Write(string line)
    {
        lock (_gate)
        {
            try { File.AppendAllText(Path.Combine(_dir, $"app-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine); }
            catch (IOException) { }
        }
    }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _p;
        private readonly string _cat;
        public FileLogger(FileLoggerProvider p, string cat) { _p = p; _cat = cat; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = SecretRedactor.Redact(formatter(state, exception));
            if (exception != null) msg += " | " + SecretRedactor.Redact(exception.ToString());
            _p.Write($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_cat}: {msg}");
        }
    }
}

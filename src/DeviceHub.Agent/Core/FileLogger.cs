using Microsoft.Extensions.Logging;

namespace DeviceHub.Agent.Core;

/// <summary>
/// 最小文件日志：Agent 由 App 以隐藏窗口启动时没有控制台，日志写到 logs\agent.log（超过 5MB 轮换一次）。
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly StreamWriter _writer;
    private readonly object _lock = new();

    public FileLoggerProvider(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "agent.log");
        if (File.Exists(path) && new FileInfo(path).Length > 5 * 1024 * 1024)
            File.Move(path, Path.Combine(dir, "agent.old.log"), overwrite: true);
        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
    }

    public ILogger CreateLogger(string category) => new FileLogger(this, category);

    private void Write(string line) { lock (_lock) _writer.WriteLine(line); }

    public void Dispose() { lock (_lock) _writer.Dispose(); }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;

        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
        {
            if (!IsEnabled(level)) return;
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level,-11} {category}: {fmt(state, ex)}";
            if (ex is not null) line += Environment.NewLine + ex;
            owner.Write(line);
        }
    }
}

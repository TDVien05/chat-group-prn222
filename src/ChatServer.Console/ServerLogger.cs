using System.Text;
using ChatClient.Infrastructure.Logging;

namespace ChatServer.Console;

/// <summary>
/// Ghi log ra console (có màu) và file server.log.
/// </summary>
public sealed class ServerLogger : IServerLogger, IDisposable
{
    private readonly StreamWriter? _fileWriter;
    private readonly object _lock = new();

    public ServerLogger(string? logFilePath = null)
    {
        if (logFilePath is not null)
        {
            var dir = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            _fileWriter = new StreamWriter(logFilePath, append: true, Encoding.UTF8)
            {
                AutoFlush = true
            };

            // Ghi header phân cách mỗi lần server khởi động
            var separator = $"{'=',60}";
            _fileWriter.WriteLine(separator);
            _fileWriter.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] [INIT] Server logger started.");
            _fileWriter.WriteLine(separator);
        }
    }

    public void LogInfo(string message)    => Write("INFO ", message, ConsoleColor.Cyan);
    public void LogWarning(string message) => Write("WARN ", message, ConsoleColor.Yellow);

    public void LogError(string message, Exception? ex = null)
    {
        if (ex is null)
        {
            Write("ERROR", message, ConsoleColor.Red);
        }
        else
        {
            Write("ERROR", $"{message} | {ex.GetType().Name}: {ex.Message}", ConsoleColor.Red);
            if (ex.StackTrace is not null)
                WriteRaw($"       StackTrace: {ex.StackTrace.Split('\n').FirstOrDefault()?.Trim()}");
        }
    }

    private void Write(string level, string message, ConsoleColor color)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] [{level}] {message}";
        WriteRaw(line, color);
    }

    private void WriteRaw(string line, ConsoleColor color = ConsoleColor.Gray)
    {
        lock (_lock)
        {
            var prev = System.Console.ForegroundColor;
            System.Console.ForegroundColor = color;
            System.Console.WriteLine(line);
            System.Console.ForegroundColor = prev;

            _fileWriter?.WriteLine(line);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _fileWriter?.Flush();
            _fileWriter?.Dispose();
        }
    }
}

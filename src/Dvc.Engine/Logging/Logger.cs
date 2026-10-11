namespace Dvc.Engine;

/// <summary>引擎轻量日志抽象（避免引擎工程依赖 AspNetCore Logging，保持 AOT 精简）。</summary>
public interface ILogger
{
    void LogInfo(string message);

    void LogWarn(string message);

    void LogError(string message, Exception? ex = null);
}

/// <summary>控制台日志实现（默认）。</summary>
public sealed class ConsoleLogger : ILogger
{
    private static void Write(string level, string message, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] [{level}] {message}");
        Console.ForegroundColor = prev;
    }

    public void LogInfo(string message) => Write("INFO", message, ConsoleColor.Gray);

    public void LogWarn(string message) => Write("WARN", message, ConsoleColor.Yellow);

    public void LogError(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}\n{ex}", ConsoleColor.Red);
}

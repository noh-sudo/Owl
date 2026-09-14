namespace OwlServer.Utils;

public enum LogLevel
{
    Info,
    Warn,
    Error
}

/// <summary>
/// Minimal console logger matching the [LEVEL] tag format from the dev plan (section 27).
/// Thread-safe: Console.WriteLine is not inherently atomic under interleaving, so we lock.
/// </summary>
public static class Logger
{
    private static readonly object SyncRoot = new();

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Error(string message, Exception ex) => Write(LogLevel.Error, $"{message} :: {ex.GetType().Name}: {ex.Message}");

    private static void Write(LogLevel level, string message)
    {
        var tag = level switch
        {
            LogLevel.Info => "INFO",
            LogLevel.Warn => "WARN",
            LogLevel.Error => "ERROR",
            _ => "INFO"
        };

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{tag}] {message}";

        lock (SyncRoot)
        {
            var prevColor = Console.ForegroundColor;
            Console.ForegroundColor = level switch
            {
                LogLevel.Warn => ConsoleColor.Yellow,
                LogLevel.Error => ConsoleColor.Red,
                _ => prevColor
            };
            Console.WriteLine(line);
            Console.ForegroundColor = prevColor;
        }
    }
}

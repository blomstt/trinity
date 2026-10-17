using System.Diagnostics;

namespace Trinity;

/// <summary>Engine and game logging to the console, with the time since start and a category.</summary>
public static class Log
{
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static readonly Lock Gate = new();

    /// <summary>Every line logged: level, category, message. Raised on the logging thread.</summary>
    public static event Action<string, string, string>? Written;

    public static void Info(string category, string message) => Write("info", category, message, ConsoleColor.Gray);

    public static void Warn(string category, string message) => Write("warn", category, message, ConsoleColor.Yellow);

    public static void Error(string category, string message) => Write("error", category, message, ConsoleColor.Red);

    static void Write(string level, string category, string message, ConsoleColor color)
    {
        lock (Gate)
        {
            Console.ForegroundColor = color;
            Console.Error.WriteLine($"{Clock.Elapsed.TotalSeconds,8:F3} {level,-5} {category}: {message}");
            Console.ResetColor();
        }
        Written?.Invoke(level, category, message);
    }
}

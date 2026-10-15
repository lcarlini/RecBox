using System.IO;
using System.Text;

namespace RecBox.Services;

/// <summary>Small file logger: %AppData%\RecBox\logs\recbox-yyyyMMdd.log (last 14 days kept).</summary>
public static class AppLog
{
    private static readonly object Lock = new();
    private static bool _cleaned;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RecBox", "logs");

    public static string CurrentLogFile => Path.Combine(LogDirectory, $"recbox-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(LogDirectory);
                if (!_cleaned)
                {
                    _cleaned = true;
                    Cleanup();
                }

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(CurrentLogFile, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never break recording.
        }
    }

    private static void Cleanup()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-14);
            foreach (var file in Directory.GetFiles(LogDirectory, "recbox-*.log"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // ignore
        }
    }
}

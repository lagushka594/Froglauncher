using System.Text;

namespace FrogLauncher.Core;

/// <summary>Простое файловое логирование (logs/launcher.log, ротация на 2 МБ).</summary>
public static class Log
{
    private static readonly object Lock = new();

    public static void Info(string msg) => Write("INFO ", msg);
    public static void Warn(string msg) => Write("WARN ", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg}\n{ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                var file = Path.Combine(AppPaths.Logs, "launcher.log");
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    File.Move(file, Path.Combine(AppPaths.Logs, "launcher.old.log"), true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { /* логирование не должно ронять приложение */ }
    }
}

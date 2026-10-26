using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using FrogLauncher.Core;
using FrogLauncher.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace FrogLauncher.Services;

/// <summary>
/// URL-схема froglauncher:// в HKCU (без прав администратора).
/// Возврат после оплаты: froglauncher://billing/success?sub_id=...
/// </summary>
public static class UrlScheme
{
    public const string Scheme = "froglauncher";

    public static void Register(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Scheme}");
        key.SetValue("", "URL:FrogLauncher Protocol");
        key.SetValue("URL Protocol", "");
        using (var icon = key.CreateSubKey("DefaultIcon")) icon.SetValue("", $"\"{exePath}\",0");
        using var cmd = key.CreateSubKey(@"shell\open\command");
        cmd.SetValue("", $"\"{exePath}\" \"%1\"");
    }

    /// <summary>Принимаем только наши ссылки разумной длины — аргументы приходят извне.</summary>
    public static bool TryParse(string? arg, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrEmpty(arg) || arg.Length > 2048) return false;
        if (!Uri.TryCreate(arg, UriKind.Absolute, out var u) || u.Scheme != Scheme || string.IsNullOrEmpty(u.Host)) return false;
        uri = u;
        return true;
    }

    public static string? Query(Uri uri, string name) =>
        uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && p[0] == name)
            .Select(p => Uri.UnescapeDataString(p[1]))
            .FirstOrDefault();
}

/// <summary>
/// Один экземпляр лаунчера. Второй запуск (например, по ссылке froglauncher:// из браузера)
/// передаёт аргументы первому через именованный канал, доступный только текущему пользователю.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\FrogLauncher.SingleInstance";
    private const string PipeName = "FrogLauncher.DeepLinks";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    public bool IsFirst { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out var created);
        IsFirst = created;
    }

    public static bool SendToFirst(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            var bytes = Encoding.UTF8.GetBytes(message);
            client.Write(bytes);
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return false;
        }
    }

    public void Listen(Action<string> onMessage)
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(_cts.Token);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var text = await reader.ReadToEndAsync(_cts.Token);
                    if (text.Length is > 0 and <= 2048) onMessage(text);
                }
                catch (OperationCanceledException) { break; }
                catch (IOException) { /* клиент оборвался — ждём следующего */ }
            }
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        _mutex.Dispose();
    }
}

public enum ToastKind { Success, Info, Warning, Error }

public sealed record Toast(string Message, ToastKind Kind)
{
    public string Icon => Kind switch
    {
        ToastKind.Success => "/Assets/ui/frog_happy.png",
        ToastKind.Warning => "/Assets/ui/frog_surprise.png",
        ToastKind.Error => "/Assets/ui/frog_sad.png",
        _ => "/Assets/ui/frog_idle.png",
    };
}

/// <summary>Всплывающие уведомления внутри окна лаунчера.</summary>
public sealed class ToastService
{
    public ObservableCollection<Toast> Items { get; } = new();

    public void Show(string message, ToastKind kind = ToastKind.Info, int seconds = 4)
    {
        Application.Current.Dispatcher.Invoke(async () =>
        {
            var t = new Toast(message, kind);
            Items.Add(t);
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            Items.Remove(t);
        });
    }
}

/// <summary>settings.json — пользовательские настройки без секретов.</summary>
public sealed class SettingsService(ILogger<SettingsService> log)
{
    private LauncherSettings? _settings;

    public LauncherSettings Current => _settings ??= Load();

    private LauncherSettings Load()
    {
        try
        {
            if (File.Exists(LauncherPaths.SettingsFile))
                return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(LauncherPaths.SettingsFile), Json.Options) ?? new();
        }
        catch (JsonException ex)
        {
            log.LogWarning(ex, "settings.json повреждён, используем значения по умолчанию");
        }
        return new LauncherSettings();
    }

    public void Save() => File.WriteAllText(LauncherPaths.SettingsFile, JsonSerializer.Serialize(Current, Json.Options));
}

public static class Shell
{
    /// <summary>Открыть ссылку/папку в системе. Только http(s) и локальные пути.</summary>
    public static void Open(string target)
    {
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(target))
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
    }
}

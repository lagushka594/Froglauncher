using System.Diagnostics;
using System.Reflection;
using FrogLauncher.Core;
using Microsoft.Win32;

namespace FrogLauncherSetup;

/// <summary>Сама установка: распаковка лаунчера, ярлыки (без «.exe» на рабочем столе), запись в «Установка и удаление программ».</summary>
public static class InstallerCore
{
    public const string AppName = "FrogLauncher";
    public const string Version = "1.0.0";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FrogLauncher";

    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);

    public static bool HasPayload => Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.FrogLauncher.exe") != null;

    /// <summary>Всегда ставим в отдельную папку FrogLauncher — чтобы удаление не затронуло чужие файлы.</summary>
    public static string Normalize(string dir)
    {
        var full = Path.GetFullPath(dir.Trim().Trim('"'));
        if (!Path.GetFileName(full.TrimEnd('\\', '/')).Equals(AppName, StringComparison.OrdinalIgnoreCase))
            full = Path.Combine(full, AppName);
        return full;
    }

    public static async Task<string> InstallAsync(string dir, bool desktop, bool startMenu, IProgress<(double percent, string text)> progress, CancellationToken ct)
    {
        dir = Normalize(dir);
        var exe = Path.Combine(dir, AppName + ".exe");

        // нельзя перезаписать запущенный лаунчер
        foreach (var p in Process.GetProcessesByName(AppName))
        {
            try { if (string.Equals(p.MainModule?.FileName, exe, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("FrogLauncher сейчас запущен. Закройте его и повторите установку."); }
            catch (InvalidOperationException) { throw; }
            catch { /* нет доступа к чужому процессу — игнорируем */ }
        }

        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("Payload.FrogLauncher.exe")
            ?? throw new InvalidOperationException("В установщике нет лаунчера (он собран без Payload). Соберите через build.bat.");

        progress.Report((0, "Создаю папку…"));
        Directory.CreateDirectory(dir);

        // 1) распаковка с прогрессом
        var tmp = exe + ".tmp";
        long total = payload.Length, done = 0;
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[256 * 1024];
            int n;
            while ((n = await payload.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                progress.Report((done * 85.0 / total, $"Копирую файлы… {done / 1024 / 1024} / {total / 1024 / 1024} МБ"));
            }
        }
        File.Move(tmp, exe, true);

        // 2) ярлыки: у .lnk Windows никогда не показывает расширение → просто «FrogLauncher»
        progress.Report((90, "Создаю ярлыки…"));
        if (desktop) Shortcut.Create(Path.Combine(Shortcut.Desktop, AppName + ".lnk"), exe, null, dir, exe, "FrogLauncher — лаунчер Minecraft");
        if (startMenu) Shortcut.Create(Path.Combine(Shortcut.StartMenu, AppName + ".lnk"), exe, null, dir, exe, "FrogLauncher — лаунчер Minecraft");

        // 3) «Приложения и возможности»
        progress.Report((96, "Регистрирую в Windows…"));
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", Version);
            key.SetValue("Publisher", "FrogLauncher");
            key.SetValue("InstallLocation", dir);
            key.SetValue("DisplayIcon", exe);
            key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(total / 1024), RegistryValueKind.DWord);
        }

        progress.Report((100, "Готово!"));
        return exe;
    }
}

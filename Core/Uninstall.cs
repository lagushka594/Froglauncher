using System.Diagnostics;
using System.Windows;
using Microsoft.Win32;

namespace FrogLauncher.Core;

/// <summary>Удаление: FrogLauncher.exe --uninstall (эту команду пишет установщик в «Приложения и возможности»).</summary>
public static class Uninstall
{
    public static void Run()
    {
        var exe = Environment.ProcessPath!;
        var dir = Path.GetDirectoryName(exe)!;

        if (!Dialogs.Confirm("Удалить FrogLauncher с этого компьютера?", danger: true)) return;
        bool data = Dialogs.Confirm("Удалить также данные лаунчера (скачанные версии, миры, моды, настройки)?\n\n«Нет» — оставить их.", danger: true);

        try { File.Delete(Path.Combine(Shortcut.Desktop, "FrogLauncher.lnk")); } catch { }
        try { File.Delete(Path.Combine(Shortcut.StartMenu, "FrogLauncher.lnk")); } catch { }
        try { Autostart.Set(false); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\FrogLauncher", false); } catch { }

        // exe нельзя удалить, пока он работает — это сделает отдельный cmd через пару секунд.
        // rmdir без /s: папка удалится, только если в ней больше ничего нет (чужие файлы не трогаем).
        var cmd = $"ping 127.0.0.1 -n 3 >nul & del /f /q \"{exe}\" & rmdir \"{dir}\"";
        if (data) cmd += $" & rmdir /s /q \"{AppPaths.Data}\"";
        var psi = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(cmd);
        Process.Start(psi);
    }
}

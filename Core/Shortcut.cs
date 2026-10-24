using Microsoft.Win32;

namespace FrogLauncher.Core;

/// <summary>Создание .lnk-ярлыков (Windows показывает ярлыки без расширения: «FrogLauncher», а не «FrogLauncher.exe»).</summary>
public static class Shortcut
{
    public static void Create(string lnkPath, string target, string? args = null, string? workDir = null, string? icon = null, string? description = null)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell недоступен.");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic lnk = shell.CreateShortcut(lnkPath);
        lnk.TargetPath = target;
        if (args != null) lnk.Arguments = args;
        lnk.WorkingDirectory = workDir ?? Path.GetDirectoryName(target) ?? "";
        lnk.IconLocation = (icon ?? target) + ",0";
        if (description != null) lnk.Description = description;
        lnk.Save();
    }

    public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    public static string StartMenu => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
}

/// <summary>Запуск лаунчера вместе с Windows (HKCU\...\Run).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is string exe) key.SetValue("FrogLauncher", $"\"{exe}\"");
        else key.DeleteValue("FrogLauncher", false);
    }
}

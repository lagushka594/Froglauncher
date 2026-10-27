using FrogLauncher.Core;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Win32;

namespace FrogLauncher.Services;

public sealed record GpuInfo(string Name, long VramBytes, bool Integrated);

public sealed record HardwareInfo(
    long TotalRamBytes, long FreeRamBytes, int Cores, string CpuName, int CpuMhz,
    IReadOnlyList<GpuInfo> Gpus, int ScreenWidth, int ScreenHeight)
{
    public double TotalRamGb => TotalRamBytes / 1024.0 / 1024 / 1024;

    /// <summary>Видеокарта, на которой будет игра: дискретная, если она есть.</summary>
    public GpuInfo? MainGpu => Gpus.OrderBy(g => g.Integrated).ThenByDescending(g => g.VramBytes).FirstOrDefault();

    public bool HasDiscreteAndIntegrated => Gpus.Any(g => g.Integrated) && Gpus.Any(g => !g.Integrated);
}

public enum PresetKind { Performance, Balanced, Quality }

/// <summary>Пресет «Подобрать под мой ПК».</summary>
public sealed record SettingsPreset(
    PresetKind Kind, string Title, string ForWhom, int Graphics, int RenderDistance, int SimulationDistance,
    int MaxFps, bool VSync, int RamMb, string Clouds, int Particles, bool Shaders, string Summary)
{
    public string Color => Kind switch { PresetKind.Performance => "#22C55E", PresetKind.Balanced => "#FACC15", _ => "#EF4444" };
}

/// <summary>Железо ПК без сторонних библиотек: память — Win32, видеокарты — реестр драйверов дисплея.</summary>
public static class HardwareService
{
    private const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static HardwareInfo Scan()
    {
        var mem = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        GlobalMemoryStatusEx(ref mem);

        string cpu = "?";
        int mhz = 0;
        using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
        {
            cpu = (k?.GetValue("ProcessorNameString") as string)?.Trim() ?? "?";
            mhz = k?.GetValue("~MHz") is int m ? m : 0;
        }

        return new HardwareInfo((long)mem.TotalPhys, (long)mem.AvailPhys, Environment.ProcessorCount, cpu, mhz, ScanGpus(),
            (int)SystemParameters.PrimaryScreenWidth, (int)SystemParameters.PrimaryScreenHeight);
    }

    private static List<GpuInfo> ScanGpus()
    {
        var result = new List<GpuInfo>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
            foreach (var sub in cls?.GetSubKeyNames().Where(n => n.All(char.IsDigit)) ?? Enumerable.Empty<string>())
            {
                using var k = cls!.OpenSubKey(sub);
                if (k?.GetValue("DriverDesc") is not string name) continue;
                var vram = k.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long l => l,
                    byte[] b when b.Length >= 8 => BitConverter.ToInt64(b, 0),
                    byte[] b when b.Length >= 4 => BitConverter.ToUInt32(b, 0),
                    int i => (uint)i,
                    _ => 0L,
                };
                if (vram == 0 && k.GetValue("HardwareInformation.MemorySize") is byte[] mb && mb.Length >= 4) vram = BitConverter.ToUInt32(mb, 0);
                if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) || name.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new GpuInfo(name, vram, IsIntegrated(name)));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { }
        return result.DistinctBy(g => g.Name).ToList();
    }

    internal static bool IsIntegrated(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("intel") && !n.Contains("arc")) return true;            // UHD/HD/Iris Xe
        if (n.Contains("radeon") && !n.Contains("rx") && !n.Contains("pro")) return true; // Radeon(TM) Graphics, Vega 8
        return n.Contains("microsoft basic");
    }

    /// <summary>Рекомендуемая RAM для Minecraft по объёму памяти ПК.</summary>
    public static int RecommendedRamMb(double totalGb) => totalGb switch
    {
        < 6 => 2048,
        < 12 => 3072,
        < 20 => 4096,
        < 40 => 6144,
        _ => 8192,
    };

    /// <summary>Класс ПК: интегрированная графика или мало памяти — слабый; мощная дискретная + 16 ГБ — сильный.</summary>
    public static PresetKind Classify(HardwareInfo hw)
    {
        var gpu = hw.MainGpu;
        var vramGb = (gpu?.VramBytes ?? 0) / 1024.0 / 1024 / 1024;
        if (gpu is null || gpu.Integrated || hw.TotalRamGb < 8 || vramGb is > 0 and < 2) return PresetKind.Performance;
        if (vramGb >= 6 && hw.TotalRamGb >= 16 && hw.Cores >= 8) return PresetKind.Quality;
        return PresetKind.Balanced;
    }

    public static IReadOnlyList<SettingsPreset> Presets(HardwareInfo hw)
    {
        var maxRam = MaxSafeRamMb(hw);
        int Ram(int want) => Math.Min(want, maxRam);
        return new[]
        {
            new SettingsPreset(PresetKind.Performance, L.T("Производительность"), L.T("Слабые ПК и ноутбуки со встроенной графикой"),
                0, 8, 6, 120, false, Ram(2048), "fast", 1, false,
                L.T("Быстрая графика, 8 чанков, лимит 120 FPS, 2 ГБ RAM, флаги Aikar")),
            new SettingsPreset(PresetKind.Balanced, L.T("Сбалансированный"), L.T("Средние ПК — рекомендуем по умолчанию"),
                1, 12, 10, 144, true, Ram(4096), "true", 0, false,
                L.T("Красивая графика, 12 чанков, V-Sync, 4 ГБ RAM")),
            new SettingsPreset(PresetKind.Quality, L.T("Качество"), L.T("Мощные ПК с дискретной видеокартой от 6 ГБ"),
                2, 24, 12, McValues.UnlimitedFps, false, Ram(8192), "true", 0, true,
                L.T("Fabulous, 24 чанка, шейдеры (если стоит Iris), 8 ГБ RAM")),
        };
    }

    /// <summary>Больше этого Java не выделить без зависаний: оставляем 2 ГБ системе (минимум 1 ГБ игре).</summary>
    public static int MaxSafeRamMb(HardwareInfo hw) =>
        Math.Max(1024, (int)(hw.TotalRamBytes / 1024 / 1024) - 2048);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

/// <summary>Предпочтение видеокарты Windows (Настройки → Дисплей → Графика) для javaw.exe этой версии.</summary>
public static class GpuPreference
{
    private const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>true — «Высокая производительность» (дискретная), false — убрать предпочтение.</summary>
    public static void Apply(string javaExe, bool discrete)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        if (discrete) k.SetValue(javaExe, "GpuPreference=2;");
        else if (k.GetValue(javaExe) is not null) k.DeleteValue(javaExe);
    }
}

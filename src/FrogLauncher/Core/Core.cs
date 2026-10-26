using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FrogLauncher.Core;

/// <summary>Настройки лаунчера (appsettings.json рядом с exe + переменные FROGLAUNCHER_*).</summary>
public sealed class LauncherOptions
{
    public const string Section = "Launcher";
    public string ApiBaseUrl { get; set; } = "https://api.froglauncher.example/";
    /// <summary>Client ID приложения Azure (Microsoft Entra) для входа через Microsoft.</summary>
    public string MicrosoftClientId { get; set; } = "577bb56b-e06f-47d3-98cb-ebb21dacf055";
    /// <summary>Application ID из Discord Developer Portal для Rich Presence.</summary>
    public string DiscordApplicationId { get; set; } = "1564103277010886746";
    public string GitHubUrl { get; set; } = "https://github.com/lagushka594/Froglauncher";
    public string OfferUrl { get; set; } = "https://froglauncher.example/offer";
    public string RefundPolicyUrl { get; set; } = "https://froglauncher.example/refund";

    /// <summary>
    /// discord — покупка через тикет на Discord-сервере (тариф выдаёт сотрудник в FrogNews);
    /// checkout — онлайн-оплата YooKassa/Paddle через /billing/checkout.
    /// </summary>
    public string PurchaseMode { get; set; } = "discord";

    /// <summary>
    /// Откуда брать тариф: discord — по ролям на вашем Discord-сервере (без своего backend);
    /// server — с Frog.Api (/entitlements).
    /// </summary>
    public string SubscriptionSource { get; set; } = "discord";
    /// <summary>ID вашего Discord-сервера (ПКМ по серверу → «Копировать ID сервера»).</summary>
    public string DiscordGuildId { get; set; } = "";
    /// <summary>ID роли → слаг тарифа (froglet, frog_plus, frog_pro, frog_legend, admin).</summary>
    public Dictionary<string, string> DiscordRoles { get; set; } = new();
    /// <summary>Порт для возврата из браузера: http://127.0.0.1:{порт}/callback (добавить в OAuth2 → Redirects).</summary>
    public int DiscordRedirectPort { get; set; } = 53134;

    public bool UsesDiscordRoles => SubscriptionSource != "server";
    public string DiscordPurchaseUrl { get; set; } = "https://discord.gg/u6zJZ7BzU5";
}

/// <summary>Все пути лаунчера. Данные — в %AppData%\FrogLauncher, права администратора не нужны.</summary>
public static class LauncherPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrogLauncher");

    public static string Game => Path.Combine(Root, "game");
    public static string Versions => Path.Combine(Game, "versions");
    public static string Libraries => Path.Combine(Game, "libraries");
    public static string Assets => Path.Combine(Game, "assets");
    public static string Runtimes => Path.Combine(Root, "runtime");
    public static string Instances => Path.Combine(Root, "instances");
    public static string Cache => Path.Combine(Root, "cache");
    public static string Logs => Path.Combine(Root, "logs");

    public static string SessionFile => Path.Combine(Root, "session.dat");
    public static string AccountsFile => Path.Combine(Root, "accounts.dat");
    public static string EntitlementsFile => Path.Combine(Root, "entitlements.dat");
    public static string DiscordFile => Path.Combine(Root, "discord.dat");
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string VersionJson(string id) => Path.Combine(Versions, id, id + ".json");
    public static string VersionJar(string id) => Path.Combine(Versions, id, id + ".jar");

    public static void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Versions, Libraries, Assets, Runtimes, Instances, Cache, Logs })
            Directory.CreateDirectory(dir);
    }
}

/// <summary>
/// Файлы, зашифрованные DPAPI (CurrentUser): расшифровать их может только этот пользователь Windows.
/// Используется для сессии, игровых аккаунтов и кэша подписки.
/// </summary>
public static class ProtectedStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FrogLauncher.v1.🐸");

    public static void Save<T>(string path, T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, Json.Options);
        var data = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>null — файла нет или он повреждён/от другого пользователя.</summary>
    public static T? Load<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<T>(json, Json.Options);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException)
        {
            return null;
        }
    }

    public static void Delete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}

/// <summary>tiers.json, вшитый в лаунчер (тот же файл, что на сервере): названия и состав тарифов офлайн.</summary>
public static class EmbeddedTiers
{
    public static Frog.Shared.TierCatalog Load()
    {
        using var s = typeof(EmbeddedTiers).Assembly.GetManifestResourceStream("FrogLauncher.tiers.json")
                      ?? throw new InvalidOperationException("В сборке нет tiers.json");
        using var r = new StreamReader(s);
        return Frog.Shared.TierCatalog.Parse(r.ReadToEnd());
    }
}

public static class Json
{
    /// <summary>Для собственных файлов лаунчера.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Для JSON Mojang/Fabric/Forge — регистр имён не важен.</summary>
    public static readonly JsonSerializerOptions Mojang = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}

public static class Hashing
{
    public static async Task<string> Sha1FileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA1.HashDataAsync(fs, ct)).ToLowerInvariant();
    }

    public static async Task<string> Sha256FileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
    }
}

/// <summary>Форматирование размеров/скорости/ETA для UI.</summary>
public static class Fmt
{
    public static string Bytes(double b) => b switch
    {
        >= 1 << 30 => $"{b / (1 << 30):0.0} ГБ",
        >= 1 << 20 => $"{b / (1 << 20):0.0} МБ",
        >= 1 << 10 => $"{b / (1 << 10):0} КБ",
        _ => $"{b:0} Б",
    };

    public static string Eta(TimeSpan? t) => t switch
    {
        null => "—",
        { TotalHours: >= 1 } v => $"{(int)v.TotalHours} ч {v.Minutes} мин",
        { TotalMinutes: >= 1 } v => $"{v.Minutes} мин {v.Seconds} с",
        var v => $"{v.Value.Seconds} с",
    };
}

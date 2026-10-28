using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RibbixLauncher.Core;

/// <summary>Настройки лаунчера (appsettings.json рядом с exe + переменные RIBBIX_*).</summary>
public sealed class LauncherOptions
{
    public const string Section = "Launcher";
    public string ApiBaseUrl { get; set; } = "https://api.ribbix.example/";
    /// <summary>Client ID приложения Azure (Microsoft Entra) для входа через Microsoft.</summary>
    public string MicrosoftClientId { get; set; } = "577bb56b-e06f-47d3-98cb-ebb21dacf055";
    /// <summary>Application ID из Discord Developer Portal для Rich Presence.</summary>
    public string DiscordApplicationId { get; set; } = "1564103277010886746";
    public string GitHubUrl { get; set; } = "https://github.com/lagushka594/Ribbix_Studio";
    public string OfferUrl { get; set; } = "https://ribbix.example/offer";
    public string RefundPolicyUrl { get; set; } = "https://ribbix.example/refund";

    /// <summary>
    /// discord — покупка через тикет на Discord-сервере (тариф выдаёт сотрудник в RibbixNews);
    /// checkout — онлайн-оплата YooKassa/Paddle через /billing/checkout.
    /// </summary>
    public string PurchaseMode { get; set; } = "discord";

    /// <summary>
    /// Откуда брать тариф: discord — по ролям на вашем Discord-сервере (без своего backend);
    /// server — с Ribbix.Api (/entitlements).
    /// </summary>
    public string SubscriptionSource { get; set; } = "discord";
    /// <summary>ID вашего Discord-сервера (ПКМ по серверу → «Копировать ID сервера»).</summary>
    public string DiscordGuildId { get; set; } = "";
    /// <summary>ID роли → слаг тарифа (ribbix_lite, ribbix_plus, ribbix_pro, ribbix_legend, admin).</summary>
    public Dictionary<string, string> DiscordRoles { get; set; } = new();
    /// <summary>Порт для возврата из браузера: http://127.0.0.1:{порт}/callback (добавить в OAuth2 → Redirects).</summary>
    public int DiscordRedirectPort { get; set; } = 53134;

    public bool UsesDiscordRoles => SubscriptionSource != "server";

    /// <summary>Список серверов (обновляется коммитом в репозиторий, без нового релиза).</summary>
    public string ServerListUrl { get; set; } = "https://raw.githubusercontent.com/lagushka594/Ribbix_Studio/main/servers.json";
    public string PromoListUrl { get; set; } = "https://raw.githubusercontent.com/lagushka594/Ribbix_Studio/main/promos.json";
    /// <summary>Репозиторий с релизами для автообновления (owner/name).</summary>
    public string UpdateRepository { get; set; } = "lagushka594/Ribbix_Studio";
    /// <summary>Новогодний плейлист (кнопка на главной в праздники).</summary>
    public string NewYearPlaylistUrl { get; set; } = "https://www.youtube.com/results?search_query=minecraft+christmas+music";
    public string DiscordPurchaseUrl { get; set; } = "https://discord.gg/u6zJZ7BzU5";
}

/// <summary>Все пути лаунчера. Данные — в %AppData%\RibbixLauncher, права администратора не нужны.</summary>
public static class LauncherPaths
{
    public static string Root { get; } = ResolveRoot(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>Папка данных до переименования (FrogLauncher, до 2.6).</summary>
    public const string LegacyFolder = "FrogLauncher";

    /// <summary>
    /// Первый запуск после переименования: %AppData%\FrogLauncher переезжает в %AppData%\RibbixLauncher
    /// (версии, миры, аккаунты, статистика), абсолютные пути в instance.json и settings.json правятся.
    /// Если старая папка занята (старый лаунчер ещё запущен) — работаем в ней, переезд — при следующем запуске.
    /// </summary>
    internal static string ResolveRoot(string appData)
    {
        var root = Path.Combine(appData, "RibbixLauncher");
        var legacy = Path.Combine(appData, LegacyFolder);
        if (Directory.Exists(root) || !Directory.Exists(legacy)) return root;
        try
        {
            Directory.Move(legacy, root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return legacy;
        }
        RewritePaths(root, legacy);
        return root;
    }

    internal static void RewritePaths(string root, string legacy)
    {
        var files = new List<string> { Path.Combine(root, "settings.json") };
        var instances = Path.Combine(root, "instances");
        if (Directory.Exists(instances))
            files.AddRange(Directory.GetDirectories(instances).Select(d => Path.Combine(d, "instance.json")));
        // В JSON обратные слэши экранированы.
        var (from, to) = (legacy.Replace("\\", "\\\\"), root.Replace("\\", "\\\\"));
        foreach (var f in files.Where(File.Exists))
        {
            try
            {
                var text = File.ReadAllText(f);
                if (text.Contains(from, StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(f, text.Replace(from, to, StringComparison.OrdinalIgnoreCase));
            }
            catch (IOException) { }
        }
    }

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
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RibbixLauncher.v1.🐸");

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
    public static Ribbix.Shared.TierCatalog Load()
    {
        using var s = typeof(EmbeddedTiers).Assembly.GetManifestResourceStream("RibbixLauncher.tiers.json")
                      ?? throw new InvalidOperationException(L.T("В сборке нет tiers.json"));
        using var r = new StreamReader(s);
        return Ribbix.Shared.TierCatalog.Parse(r.ReadToEnd());
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
        >= 1 << 30 => L.T($"{b / (1 << 30):0.0} ГБ"),
        >= 1 << 20 => L.T($"{b / (1 << 20):0.0} МБ"),
        >= 1 << 10 => L.T($"{b / (1 << 10):0} КБ"),
        _ => L.T($"{b:0} Б"),
    };

    public static string Eta(TimeSpan? t) => t switch
    {
        null => "—",
        { TotalHours: >= 1 } v => L.T($"{(int)v.TotalHours} ч {v.Minutes} мин"),
        { TotalMinutes: >= 1 } v => L.T($"{v.Minutes} мин {v.Seconds} с"),
        var v => L.T($"{v.Value.Seconds} с"),
    };
}

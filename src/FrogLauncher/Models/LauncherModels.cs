using System.Text.Json.Serialization;

namespace FrogLauncher.Models;

public enum LoaderType { Vanilla, Fabric, Quilt, Forge, NeoForge }

/// <summary>instances/{id}/instance.json — изолированный инстанс со своей папкой игры.</summary>
public sealed class Instance
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string GameVersion { get; set; } = "";
    public LoaderType Loader { get; set; } = LoaderType.Vanilla;
    public string? LoaderVersion { get; set; }
    /// <summary>ID версии для запуска (после установки модлоадера, например fabric-loader-0.16.5-1.21.1).</summary>
    public string? LaunchVersionId { get; set; }

    public int RamMb { get; set; } = 4096;
    public string JvmArgs { get; set; } = "";
    /// <summary>Автонастройка JVM (фича jvm_autotune, Frog+ и выше).</summary>
    public bool JvmAutotune { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool Fullscreen { get; set; }
    public bool Demo { get; set; }
    /// <summary>Ручной выбор Java; null — автоподбор под версию игры.</summary>
    public string? JavaPath { get; set; }

    /// <summary>Имена jar-файлов в папке mods (обновляется при сохранении).</summary>
    public List<string> Mods { get; set; } = new();

    /// <summary>Иконка (для версий, созданных из сборки Modrinth).</summary>
    public string? IconUrl { get; set; }

    /// <summary>Моды, установленные из каталога Modrinth (для названий, иконок и зависимостей).</summary>
    public List<InstalledMod> InstalledMods { get; set; } = new();

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastPlayedAt { get; set; }

    [JsonIgnore] public string Directory { get; set; } = "";
    [JsonIgnore] public string GameDirectory => System.IO.Path.Combine(Directory, ".minecraft");

    [JsonIgnore]
    public string DisplayVersion => Loader == LoaderType.Vanilla
        ? GameVersion
        : $"{GameVersion} · {Loader}{(LoaderVersion is null ? "" : " " + LoaderVersion)}";
}

/// <summary>Мод из Modrinth, установленный в версию.</summary>
public sealed class InstalledMod
{
    public string ProjectId { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Title { get; set; } = "";
    public string? IconUrl { get; set; }
    /// <summary>Поставлен автоматически как обязательная зависимость другого мода.</summary>
    public bool IsDependency { get; set; }
}

public enum AccountType { Offline, Microsoft }

/// <summary>Игровой аккаунт. Хранится только в accounts.dat (DPAPI).</summary>
public sealed class GameAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public AccountType Type { get; set; }
    public string Name { get; set; } = "";
    /// <summary>UUID без дефисов.</summary>
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "0";
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    /// <summary>Refresh-токен Microsoft — ротируется при каждом обновлении.</summary>
    public string? MsRefreshToken { get; set; }
    public string? Xuid { get; set; }

    [JsonIgnore] public string TypeLabel => Type == AccountType.Microsoft ? "Microsoft" : "Оффлайн";
}

public sealed class AccountsFile
{
    public List<GameAccount> Accounts { get; set; } = new();
    public Guid? SelectedId { get; set; }
}

/// <summary>Пользовательские настройки (settings.json, без секретов).</summary>
public sealed class LauncherSettings
{
    public string? SelectedInstanceId { get; set; }
    public string Theme { get; set; } = "swamp";
    public bool CloseOnLaunch { get; set; }
    public int DefaultRamMb { get; set; } = 4096;
    /// <summary>Показывать активность в Discord («Играет в FrogLauncher»).</summary>
    public bool DiscordPresence { get; set; } = true;
}

public sealed record JavaRuntime(string JavaExe, int Major, string Version, string Source)
{
    public string Display => $"Java {Major} ({Version}) — {Source}";
}

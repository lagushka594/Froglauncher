using RibbixLauncher.Core;
using System.Text.Json.Serialization;

namespace RibbixLauncher.Models;

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

    /// <summary>Максимум памяти Java (-Xmx).</summary>
    public int RamMb { get; set; } = 4096;
    /// <summary>Стартовая память Java (-Xms); null — min(RamMb, 1024).</summary>
    public int? RamMinMb { get; set; }
    /// <summary>Сборщик мусора: g1 (по умолчанию) | zgc (Java 17+) | parallel.</summary>
    public string Gc { get; set; } = "g1";
    /// <summary>Приоритет процесса игры: normal | abovenormal | high.</summary>
    public string Priority { get; set; } = "normal";
    /// <summary>Просить Windows запускать игру на дискретной видеокарте (ноутбуки с двумя GPU).</summary>
    public bool PreferDiscreteGpu { get; set; }
    public string JvmArgs { get; set; } = "";
    /// <summary>Автонастройка JVM (фича jvm_autotune, Ribbix+ и выше).</summary>
    public bool JvmAutotune { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public bool Fullscreen { get; set; }
    public bool Demo { get; set; }
    /// <summary>Ручной выбор Java; null — автоподбор под версию игры.</summary>
    public string? JavaPath { get; set; }

    /// <summary>Имена jar-файлов в папке mods (обновляется при сохранении).</summary>
    public List<string> Mods { get; set; } = new();

    /// <summary>Ключ приглашения «Играть с другом»: повторная ссылка на тот же набор открывает эту версию.</summary>
    public string? ShareKey { get; set; }

    /// <summary>Общая сборка (у друга): gist хоста и файлы, которые пришли из неё (относительно .minecraft).</summary>
    public string? SharedGist { get; set; }
    public List<string> SharedFiles { get; set; } = new();
    /// <summary>Общая сборка (у хоста): свой gist и ключ последней опубликованной версии.</summary>
    public string? HostGist { get; set; }
    public string? HostShareKey { get; set; }

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

    [JsonIgnore] public string TypeLabel => Type == AccountType.Microsoft ? "Microsoft" : L.T("Оффлайн");
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
    /// <summary>"auto" — язык системы; иначе ru, en, es, de, pt-BR. Меняется после перезапуска.</summary>
    public string Language { get; set; } = "auto";
    public bool CloseOnLaunch { get; set; }
    public int DefaultRamMb { get; set; } = 4096;
    /// <summary>Показывать активность в Discord («Играет в RibbixLauncher»).</summary>
    public bool DiscordPresence { get; set; } = true;

    /// <summary>Крестик сворачивает лаунчер в трей (выход — через меню трея или «Выход»).</summary>
    public bool MinimizeToTray { get; set; } = true;
    public bool TrayHintShown { get; set; }
    /// <summary>Напоминать о времени в игре каждые N часов (0 — выкл).</summary>
    public int PlaytimeReminderHours { get; set; } = 2;
    /// <summary>Лепестки, листья, снег и прочие сезонные эффекты (v2.4).</summary>
    public bool SeasonEffects { get; set; } = true;
    /// <summary>Звук сезона при запуске.</summary>
    public bool StartupSound { get; set; } = true;
    /// <summary>Один раз переключаем «Болото» по умолчанию на сезонную тему.</summary>
    public bool SeasonsIntroduced { get; set; }
    /// <summary>Последний релиз Minecraft, о котором уже сообщили.</summary>
    public string? LastSeenMcRelease { get; set; }

    /// <summary>Онбординг первого запуска пройден или пропущен.</summary>
    public bool OnboardingDone { get; set; }

    /// <summary>Канал обновлений: stable — для всех, beta — Ribbix+ и выше.</summary>
    public string UpdateChannel { get; set; } = "stable";
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Пропущенная игроком версия — о ней больше не напоминаем.</summary>
    public string? SkippedUpdate { get; set; }

    /// <summary>Избранные серверы (адреса).</summary>
    public List<string> FavoriteServers { get; set; } = new();
    /// <summary>Серверы, добавленные игроком.</summary>
    public List<RibbixLauncher.Services.ServerEntry> CustomServers { get; set; } = new();
    public string? LastServer { get; set; }

    /// <summary>Переназначенные горячие клавиши: действие → «Ctrl+Enter».</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();
}

public sealed record JavaRuntime(string JavaExe, int Major, string Version, string Source)
{
    public string Display => $"Java {Major} ({Version}) — {Source}";
}

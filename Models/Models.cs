using System.Text.Json.Serialization;
using FrogLauncher.Core;

namespace FrogLauncher.Models;

public enum VersionKind { Release, Snapshot, Old }

public sealed class McVersion : ObservableObject
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "release";
    public DateTime ReleaseTime { get; set; }
    public string Url { get; set; } = "";
    public string Sha1 { get; set; } = "";

    public VersionKind Kind => Type switch
    {
        "release" => VersionKind.Release,
        "snapshot" => VersionKind.Snapshot,
        _ => VersionKind.Old
    };

    public string TypeText => Type switch
    {
        "release" => "release", "snapshot" => "snapshot", "old_beta" => "beta", "old_alpha" => "alpha", _ => Type
    };

    public string DateText => ReleaseTime.ToString("dd.MM.yyyy");

    private string _size = "—";
    public string SizeText { get => _size; set => Set(ref _size, value); }
}

public sealed class Instance
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Новая версия";
    public string McVersion { get; set; } = "";
    /// <summary>Id версии для запуска (ванильная или fabric-loader-… / forge-…).</summary>
    public string LaunchVersionId { get; set; } = "";
    public string Loader { get; set; } = "Vanilla";
    public string LoaderVersion { get; set; } = "";
    public int MinRamMb { get; set; } = 1024;
    public int MaxRamMb { get; set; } = 4096;
    public string JvmArgs { get; set; } = "-XX:+UseG1GC -XX:+UnlockExperimentalVMOptions";
    public int Width { get; set; } = 854;
    public int Height { get; set; } = 480;
    public bool Fullscreen { get; set; }
    public bool Demo { get; set; }
    public string? JavaPath { get; set; }
    /// <summary>Автоматически зайти на сервер при запуске (host[:port]).</summary>
    public string ServerAddress { get; set; } = "";
    /// <summary>Команды перед запуском / после выхода из игры (cmd.exe /c …).</summary>
    public string PreLaunchCommand { get; set; } = "";
    public string PostExitCommand { get; set; } = "";
    public long PlaySeconds { get; set; }
    public DateTime? LastPlayed { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;

    [JsonIgnore] public string Dir => Path.Combine(AppPaths.Instances, Id);
    /// <summary>Изолированная папка .minecraft инстанса.</summary>
    [JsonIgnore] public string GameDir => Path.Combine(Dir, ".minecraft");
    public override string ToString() => Display;
    [JsonIgnore] public string PlayTimeText
    {
        get
        {
            var t = TimeSpan.FromSeconds(PlaySeconds);
            var s = PlaySeconds < 60 ? "—" : t.TotalHours >= 1 ? $"{(int)t.TotalHours} ч {t.Minutes} мин" : $"{t.Minutes} мин";
            return LastPlayed is DateTime d ? $"{s}  •  {d:dd.MM.yyyy}" : s;
        }
    }
    [JsonIgnore] public string Display => $"{Name}  •  {McVersion}" + (Loader == "Vanilla" ? "" : $"  •  {Loader}");
}

public sealed class Account
{
    public string Type { get; set; } = "offline"; // offline | microsoft
    public string Username { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string AccessToken { get; set; } = "0";
    public string MsRefreshToken { get; set; } = "";
    public DateTime ExpiresAt { get; set; } = DateTime.MaxValue;

    [JsonIgnore] public bool IsMicrosoft => Type == "microsoft";
}

public sealed class AppSettings
{
    public string Language { get; set; } = "ru";
    public string Theme { get; set; } = "Green";
    public string? GameRoot { get; set; }
    public string? Proxy { get; set; }
    public string? LastInstanceId { get; set; }
    public string MicrosoftClientId { get; set; } = "";
    public string UpdateRepo { get; set; } = "";
    public string CurseForgeApiKey { get; set; } = "";
    public string NewsUrl { get; set; } = "";
    /// <summary>Скрывать лаунчер после запуска игры.</summary>
    public bool HideOnLaunch { get; set; } = true;
    /// <summary>Вместо скрытия полностью закрывать лаунчер (откроется заново после выхода из игры).</summary>
    public bool CloseInsteadOfHide { get; set; }
    /// <summary>Открывать лаунчер снова, когда игра закрылась.</summary>
    public bool ReopenAfterExit { get; set; } = true;
    public bool ShowGameLog { get; set; } = true;
    public bool AutoStart { get; set; }
    public int DefaultMaxRamMb { get; set; } = 4096;
    // привилегии
    public string CustomBackground { get; set; } = "";
    public bool AutoBackupWorlds { get; set; }
    public bool AutoUpdateMods { get; set; }
    public bool FastDownloads { get; set; }
    public bool BetaUpdates { get; set; }
    public string CustomPresence { get; set; } = "";
}

public sealed class JavaInfo
{
    public string Home { get; set; } = "";
    public string Exe => Path.Combine(Home, "bin", "java.exe");
    public int Major { get; set; }
    public string Version { get; set; } = "";
    public string Vendor { get; set; } = "";
    public bool Compatible { get; set; }
    public string CompatText => Compatible ? "✔" : "✖";
    public string Display => $"Java {Major}  ({Version})  —  {Vendor}";
}

public sealed class NewsItem
{
    public string Title { get; set; } = "";
    public string Date { get; set; } = "";
    public string Type { get; set; } = "";
    public string Short { get; set; } = "";
}

public sealed class ServerStatus
{
    public string Name { get; set; } = "";
    public bool Ok { get; set; }
}

public sealed class DownloadProgress
{
    public long BytesDone { get; set; }
    public long BytesTotal { get; set; }
    public int FilesDone { get; set; }
    public int FilesTotal { get; set; }
    public double SpeedBytes { get; set; }
    public TimeSpan? Eta { get; set; }
    public double Percent => BytesTotal > 0 ? Math.Min(100, BytesDone * 100.0 / BytesTotal)
                           : FilesTotal > 0 ? FilesDone * 100.0 / FilesTotal : 0;
    public string Stage { get; set; } = "";
}

namespace FrogLauncher.Core;

/// <summary>Пути лаунчера. Data — служебные файлы, GameRoot — игровые файлы (можно сменить в настройках).</summary>
public static class AppPaths
{
    public static string Data { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".froglauncher");

    public static string Logs => Path.Combine(Data, "logs");
    public static string Cache => Path.Combine(Data, "cache");
    public static string JavaRuntimes => Path.Combine(Data, "java");
    public static string SettingsFile => Path.Combine(Data, "settings.json");
    public static string AccountFile => Path.Combine(Data, "account.dat");

    private static string? _gameRoot;
    public static string GameRoot
    {
        get => _gameRoot ?? Path.Combine(Data, "game");
        set => _gameRoot = string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static string Versions => Path.Combine(GameRoot, "versions");
    public static string Libraries => Path.Combine(GameRoot, "libraries");
    public static string Assets => Path.Combine(GameRoot, "assets");
    public static string Instances => Path.Combine(GameRoot, "instances");

    public static void EnsureAll()
    {
        foreach (var d in new[] { Data, Logs, Cache, JavaRuntimes, GameRoot, Versions, Libraries, Assets, Instances })
            Directory.CreateDirectory(d);
    }
}

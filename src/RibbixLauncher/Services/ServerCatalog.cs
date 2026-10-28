using System.IO;
using System.Net.Http;
using System.Text.Json;
using RibbixLauncher.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RibbixLauncher.Services;

public sealed class ServerEntry
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public string Region { get; set; } = "";
    public List<string> Tags { get; set; } = new();
    public string Description { get; set; } = "";
    /// <summary>Добавлен игроком (не из общего списка).</summary>
    public bool Custom { get; set; }
}

public sealed class ServerListFile
{
    public int Format { get; set; }
    public List<ServerEntry> Servers { get; set; } = new();
}

/// <summary>
/// Список серверов: servers.json из репозитория на GitHub (обновляется без нового релиза),
/// без сети — последняя сохранённая копия, а при первом запуске — копия, вшитая в лаунчер.
/// Свои серверы и избранное игрока — в settings.json.
/// </summary>
public sealed class ServerCatalog(IHttpClientFactory httpFactory, IOptions<LauncherOptions> options, ILogger<ServerCatalog> log)
{
    private static string CacheFile => Path.Combine(LauncherPaths.Cache, "servers.json");

    public async Task<IReadOnlyList<ServerEntry>> LoadAsync(CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient(DownloadService.HttpClientName);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var json = await http.GetStringAsync(options.Value.ServerListUrl, cts.Token);
            var list = Parse(json);
            if (list.Count > 0)
            {
                Directory.CreateDirectory(LauncherPaths.Cache);
                await File.WriteAllTextAsync(CacheFile, json, ct);
                return list;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogInformation("Список серверов с GitHub недоступен ({Message}), берём сохранённый", ex.Message);
        }

        try
        {
            if (File.Exists(CacheFile) && Parse(await File.ReadAllTextAsync(CacheFile, ct)) is { Count: > 0 } cached) return cached;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }

        using var s = typeof(ServerCatalog).Assembly.GetManifestResourceStream("RibbixLauncher.servers.json")!;
        using var r = new StreamReader(s);
        return Parse(await r.ReadToEndAsync(ct));
    }

    internal static List<ServerEntry> Parse(string json) =>
        JsonSerializer.Deserialize<ServerListFile>(json, Json.Mojang)?.Servers
            .Where(e => !string.IsNullOrWhiteSpace(e.Address) && e.Address.Length < 256)
            .ToList() ?? new List<ServerEntry>();
}

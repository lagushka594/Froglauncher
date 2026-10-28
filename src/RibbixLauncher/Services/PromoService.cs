using System.IO;
using System.Net.Http;
using System.Text.Json;
using RibbixLauncher.Core;
using Ribbix.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RibbixLauncher.Services;

/// <summary>
/// Акции и промокоды: promos.json из репозитория на GitHub (меняется без нового релиза),
/// без сети — сохранённая копия, при первом запуске — вшитая. Цена со скидкой — только для показа и текста заказа;
/// в режиме своего сервера сумму к оплате всё равно считает API по тому же файлу.
/// </summary>
public sealed class PromoService(IHttpClientFactory httpFactory, IOptions<LauncherOptions> options, ILogger<PromoService> log)
{
    private static string CacheFile => Path.Combine(LauncherPaths.Cache, "promos.json");

    private PromoCatalog? _catalog;

    /// <summary>Каталог без ожидания сети: кэш или вшитая копия.</summary>
    public PromoCatalog Catalog => _catalog ??= LoadLocal();

    public async Task<PromoCatalog> RefreshAsync(CancellationToken ct)
    {
        try
        {
            var http = httpFactory.CreateClient(DownloadService.HttpClientName);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var json = await http.GetStringAsync(options.Value.PromoListUrl, cts.Token);
            var catalog = PromoCatalog.Parse(json);
            Directory.CreateDirectory(LauncherPaths.Cache);
            await File.WriteAllTextAsync(CacheFile, json, ct);
            return _catalog = catalog;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogInformation("Акции с GitHub недоступны ({Message}), берём сохранённые", ex.Message);
            return Catalog;
        }
    }

    private static PromoCatalog LoadLocal()
    {
        try
        {
            if (File.Exists(CacheFile)) return PromoCatalog.Parse(File.ReadAllText(CacheFile));
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }

        using var s = typeof(PromoService).Assembly.GetManifestResourceStream("RibbixLauncher.promos.json")!;
        using var r = new StreamReader(s);
        return PromoCatalog.Parse(r.ReadToEnd());
    }
}

using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FrogLauncher.Core;
using FrogLauncher.Models;

namespace FrogLauncher.Services;

/// <summary>Новости (патч-ноуты Mojang), статус серверов, обновления лаунчера.</summary>
public sealed class InfoService
{
    /// <summary>Запись новости лаунчера на двух языках (файл news.json публикует NewsPublisher).</summary>
    public sealed class LauncherNewsEntry
    {
        public string Id { get; set; } = "";
        public string Date { get; set; } = "";
        public string TitleRu { get; set; } = "";
        public string TextRu { get; set; } = "";
        public string TitleEn { get; set; } = "";
        public string TextEn { get; set; } = "";
    }

    private static string NewsCache => Path.Combine(AppPaths.Data, "news-cache.json");

    /// <summary>Скачивает news.json (с «cache-buster», чтобы новое появлялось сразу); при ошибке берёт кэш.</summary>
    public async Task<List<LauncherNewsEntry>> GetLauncherNewsAsync(string url, CancellationToken ct)
    {
        string? text = null;
        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                var u = url + (url.Contains('?') ? "&" : "?") + "cb=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var bytes = await Http.Client.GetByteArrayAsync(u, ct);
                text = System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
                File.WriteAllText(NewsCache, text, new System.Text.UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn("Новости лаунчера: " + ex.Message); }
        }
        if (text == null && File.Exists(NewsCache)) text = File.ReadAllText(NewsCache, System.Text.Encoding.UTF8);

        var list = new List<LauncherNewsEntry>();
        if (text != null)
        {
            try
            {
                var json = JsonNode.Parse(text)!;
                foreach (var n in json["news"]!.AsArray())
                    list.Add(new LauncherNewsEntry
                    {
                        Id = (string?)n!["id"] ?? "", Date = (string?)n["date"] ?? "",
                        TitleRu = (string?)n["ru"]?["title"] ?? "", TextRu = (string?)n["ru"]?["text"] ?? "",
                        TitleEn = (string?)n["en"]?["title"] ?? "", TextEn = (string?)n["en"]?["text"] ?? ""
                    });
            }
            catch (Exception ex) { Log.Warn("news.json повреждён: " + ex.Message); }
        }
        if (list.Count == 0)
            list.Add(new LauncherNewsEntry
            {
                Id = "welcome", Date = DateTime.Now.ToString("yyyy-MM-dd"),
                TitleRu = "Добро пожаловать в FrogLauncher! 🐸", TextRu = "Здесь будут появляться новости лаунчера.",
                TitleEn = "Welcome to FrogLauncher! 🐸", TextEn = "Launcher news will appear here."
            });
        return list;
    }

    public async Task<List<NewsItem>> GetNewsAsync(CancellationToken ct)
    {
        var json = JsonNode.Parse(await Http.GetStringAsync(ApiConfig.NewsJson, ct))!;
        var list = new List<NewsItem>();
        foreach (var e in json["entries"]!.AsArray().Take(15))
        {
            DateTime.TryParse((string?)e!["date"], out var d);
            list.Add(new NewsItem
            {
                Title = (string?)e["title"] ?? "Minecraft",
                Type = (string?)e["type"] ?? "",
                Date = d == default ? "" : d.ToString("dd.MM.yyyy"),
                Short = (string?)e["shortText"] ?? ""
            });
        }
        return list;
    }

    public async Task<List<ServerStatus>> GetStatusAsync(CancellationToken ct)
    {
        var tasks = ApiConfig.StatusHosts.Select(async url =>
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(6));
                using var r = await Http.Client.GetAsync(url, cts.Token);
                return new ServerStatus { Name = new Uri(url).Host, Ok = (int)r.StatusCode < 500 };
            }
            catch { return new ServerStatus { Name = new Uri(url).Host, Ok = false }; }
        });
        return (await Task.WhenAll(tasks)).ToList();
    }

    /// <summary>Возвращает (есть ли новая версия, тег, ссылка) по GitHub Releases.</summary>
    public async Task<(bool newer, string tag, string url)> CheckUpdateAsync(string repo, CancellationToken ct, bool includePre = false)
    {
        if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/'))
            throw new InvalidOperationException("Укажите репозиторий обновлений в формате owner/repo (Настройки).");
        JsonNode json;
        if (includePre)   // бета-канал: берём самый свежий релиз, включая пре-релизы
        {
            var arr = JsonNode.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{repo.Trim()}/releases?per_page=5", ct))!.AsArray();
            if (arr.Count == 0) return (false, "", "");
            json = arr[0]!;
        }
        else json = JsonNode.Parse(await Http.GetStringAsync(string.Format(ApiConfig.GithubLatest, repo.Trim()), ct))!;
        var tag = ((string?)json["tag_name"] ?? "").TrimStart('v', 'V');
        var url = (string?)json["html_url"] ?? "";
        bool newer = Version.TryParse(tag, out var remote) && Version.TryParse(ApiConfig.LauncherVersion, out var local) && remote > local;
        return (newer, tag, url);
    }
}

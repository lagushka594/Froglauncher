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
                text = await Http.GetStringAsync(u, ct);
                File.WriteAllText(NewsCache, text);
            }
            catch (Exception ex) { Log.Warn("Новости лаунчера: " + ex.Message); }
        }
        if (text == null && File.Exists(NewsCache)) text = File.ReadAllText(NewsCache);

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
    public async Task<(bool newer, string tag, string url)> CheckUpdateAsync(string repo, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(repo) || !repo.Contains('/'))
            throw new InvalidOperationException("Укажите репозиторий обновлений в формате owner/repo (Настройки).");
        var json = JsonNode.Parse(await Http.GetStringAsync(string.Format(ApiConfig.GithubLatest, repo.Trim()), ct))!;
        var tag = ((string?)json["tag_name"] ?? "").TrimStart('v', 'V');
        var url = (string?)json["html_url"] ?? "";
        bool newer = Version.TryParse(tag, out var remote) && Version.TryParse(ApiConfig.LauncherVersion, out var local) && remote > local;
        return (newer, tag, url);
    }
}

/// <summary>Загрузка скина (только лицензия).</summary>
public sealed class SkinService
{
    public async Task UploadAsync(Account acc, string pngPath, bool slim, CancellationToken ct)
    {
        if (!acc.IsMicrosoft) throw new InvalidOperationException("Скины доступны только для аккаунта Microsoft.");
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(slim ? "slim" : "classic"), "variant");
        var file = new ByteArrayContent(await File.ReadAllBytesAsync(pngPath, ct));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "skin.png");
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiConfig.McSkins) { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", acc.AccessToken);
        using var resp = await Http.Client.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Скин не загружен ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}");
    }
}

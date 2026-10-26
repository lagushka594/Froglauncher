using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// ============================================================================
//  NewsPublisher — консоль для новостей FrogLauncher.
//  Пишете новость по-русски → автоматически переводится на английский →
//  публикуется (GitHub Gist) → сразу появляется в лаунчере на русском и английском.
// ============================================================================

// Вывод — UTF-8. Ввод НЕ трогаем: консоль Windows сама отдаёт кириллицу в правильной кодировке (если принудительно
// поставить UTF-8, русские буквы превращаются в «?» — из-за этого раньше в лаунчере не отображался русский текст).
Console.OutputEncoding = Encoding.UTF8;

var app = new Publisher();
await app.RunAsync();

sealed class Config
{
    public string Token { get; set; } = "";
    public string GistId { get; set; } = "";
    public string Owner { get; set; } = "";
}

sealed class Publisher
{
    private const string FileName = "news.json";
    private static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "news-config.json");
    private readonly HttpClient _http = new();
    private Config _cfg = new();
    private JsonArray _news = new();
    private JsonArray _grants = new();      // выданные привилегии (privileges.json)

    public Publisher()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FrogLauncher-NewsPublisher/1.0");
        _http.Timeout = TimeSpan.FromSeconds(40);
    }

    public async Task RunAsync()
    {
        Banner();
        try
        {
            LoadConfig();
            if (string.IsNullOrWhiteSpace(_cfg.Token)) await FirstSetupAsync();
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _cfg.Token);
            await LoadNewsAsync();
        }
        catch (Exception ex)
        {
            Err("Не удалось подключиться: " + ex.Message);
            Pause(); return;
        }

        while (true)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  1 — Добавить новость     2 — Список новостей");
            Console.WriteLine("  3 — Удалить новость      4 — Ссылка на новости");
            Console.WriteLine("  5 — Выдать привилегию    6 — Отозвать привилегию   7 — Список привилегий     0 — Выход");
            Console.ResetColor();
            Console.Write("> ");
            var cmd = (Console.ReadLine() ?? "0").Trim();
            try
            {
                switch (cmd)
                {
                    case "1": await AddAsync(); break;
                    case "2": List(); break;
                    case "3": await DeleteAsync(); break;
                    case "4": ShowUrl(); break;
                    case "5": await GrantAsync(); break;
                    case "6": await RevokeAsync(); break;
                    case "7": ListGrants(); break;
                    case "0": return;
                }
            }
            catch (Exception ex) { Err(ex.Message); }
        }
    }

    // ---------------------------------------------------------------- настройка

    private async Task FirstSetupAsync()
    {
        Console.WriteLine("Первая настройка (делается один раз).");
        Console.WriteLine();
        Console.WriteLine("Новости хранятся в вашем GitHub Gist, лаунчер читает их оттуда.");
        Console.WriteLine("1) Откройте https://github.com/settings/tokens/new");
        Console.WriteLine("2) Название любое, срок — No expiration, поставьте галочку «gist», нажмите Generate token");
        Console.WriteLine("3) Скопируйте токен (ghp_...) и вставьте сюда:");
        Console.Write("> ");
        var token = (Console.ReadLine() ?? "").Trim();
        if (token == "") throw new InvalidOperationException("Токен не введён.");
        _cfg.Token = token;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Console.WriteLine("Создаю хранилище новостей…");
        var body = new JsonObject
        {
            ["description"] = "FrogLauncher news",
            ["public"] = true,
            ["files"] = new JsonObject { [FileName] = new JsonObject { ["content"] = EmptyJson() } }
        };
        using var resp = await _http.PostAsync("https://api.github.com/gists",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub ответил {(int)resp.StatusCode}: {text}");
        var json = JsonNode.Parse(text)!;
        _cfg.GistId = (string)json["id"]!;
        _cfg.Owner = (string)json["owner"]!["login"]!;
        SaveConfig();
        Ok("Готово!");
        ShowUrl();
        TryWriteKeysProps();
    }

    private string RawUrl => $"https://gist.githubusercontent.com/{_cfg.Owner}/{_cfg.GistId}/raw/{FileName}";

    private void ShowUrl()
    {
        Console.WriteLine();
        Console.WriteLine("Ссылка на новости (вставьте в keys.props → NewsUrl и пересоберите лаунчер ОДИН раз):");
        Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine(RawUrl); Console.ResetColor();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "news-url.txt"), RawUrl);
    }

    /// <summary>Если рядом (выше по папкам) лежит keys.props — сразу вписываем туда NewsUrl.</summary>
    private void TryWriteKeysProps()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            var f = Path.Combine(dir.FullName, "keys.props");
            if (!File.Exists(f)) continue;
            var t = File.ReadAllText(f);
            var n = Regex.Replace(t, "<NewsUrl>.*?</NewsUrl>", $"<NewsUrl>{RawUrl}</NewsUrl>", RegexOptions.Singleline);
            if (n != t) { File.WriteAllText(f, n); Ok("NewsUrl записан в " + f); }
            return;
        }
    }

    // ---------------------------------------------------------------- данные

    private static string EmptyJson() => new JsonObject { ["news"] = new JsonArray() }.ToJsonString();

    private async Task LoadNewsAsync()
    {
        using var resp = await _http.GetAsync($"https://api.github.com/gists/{_cfg.GistId}");
        var text = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"GitHub ответил {(int)resp.StatusCode}. Проверьте токен/Gist.");
        var files = JsonNode.Parse(text)!["files"];
        var content = (string?)files?[FileName]?["content"] ?? EmptyJson();
        _news = JsonNode.Parse(content)?["news"]?.AsArray() ?? new JsonArray();
        var pc = (string?)files?["privileges.json"]?["content"];
        _grants = pc != null ? JsonNode.Parse(pc)?["grants"]?.AsArray() ?? new JsonArray() : new JsonArray();
        Console.WriteLine($"Загружено новостей: {_news.Count}, выдано привилегий: {_grants.Count}");
    }

    private Task PublishAsync() =>
        PublishFileAsync(FileName, new JsonObject { ["updated"] = DateTime.UtcNow.ToString("o"), ["news"] = JsonNode.Parse(_news.ToJsonString()) });

    private Task PublishGrantsAsync() =>
        PublishFileAsync("privileges.json", new JsonObject { ["updated"] = DateTime.UtcNow.ToString("o"), ["grants"] = JsonNode.Parse(_grants.ToJsonString()) });

    private async Task PublishFileAsync(string fileName, JsonObject doc)
    {
        var body = new JsonObject
        {
            ["files"] = new JsonObject { [fileName] = new JsonObject { ["content"] = doc.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) } }
        };
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"https://api.github.com/gists/{_cfg.GistId}")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Не удалось опубликовать: {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
    }

    // ---------------------------------------------------------------- действия

    private async Task AddAsync()
    {
        Console.Write("Заголовок (по-русски): ");
        var title = (Console.ReadLine() ?? "").Trim();
        if (title == "") { Warn("Пустой заголовок — отмена."); return; }
        Console.WriteLine("Текст новости (по-русски). Закончите пустой строкой:");
        var sb = new StringBuilder();
        while (true)
        {
            var line = Console.ReadLine();
            if (string.IsNullOrEmpty(line)) break;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(line);
        }
        var text = sb.ToString().Trim();
        if (LooksBroken(title) || LooksBroken(text)) { Warn("В тексте много знаков «?» — похоже, консоль не передала русские буквы. Запустите NewsPublisher.exe через Windows Terminal или выполните «chcp 65001» и повторите."); return; }

        Console.WriteLine("Перевожу на английский…");
        var enTitle = await TranslateAsync(title);
        var enText = text == "" ? "" : await TranslateAsync(text);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("EN: " + enTitle);
        if (enText != "") Console.WriteLine(enText);
        Console.ResetColor();
        Console.Write("Enter — опубликовать, e — поправить перевод вручную, 0 — отмена: ");
        var a = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
        if (a == "0") return;
        if (a == "e")
        {
            Console.Write("English title: "); var t = Console.ReadLine(); if (!string.IsNullOrWhiteSpace(t)) enTitle = t.Trim();
            Console.WriteLine("English text (end with an empty line):");
            var sb2 = new StringBuilder();
            while (true) { var l = Console.ReadLine(); if (string.IsNullOrEmpty(l)) break; if (sb2.Length > 0) sb2.Append('\n'); sb2.Append(l); }
            if (sb2.Length > 0) enText = sb2.ToString().Trim();
        }

        _news.Insert(0, new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString("N")[..8],
            ["date"] = DateTime.Now.ToString("yyyy-MM-dd"),
            ["ru"] = new JsonObject { ["title"] = title, ["text"] = text },
            ["en"] = new JsonObject { ["title"] = enTitle, ["text"] = enText }
        });
        await PublishAsync();
        Ok("Опубликовано! Лаунчер покажет новость при ближайшем обновлении (до 3 минут или кнопка ⟳).");
    }

    private static bool LooksBroken(string t) => t.Count(c => c == '?' || c == '\uFFFD') >= 3 && !t.Any(c => c >= '\u0400' && c <= '\u04FF');

    private void List()
    {
        if (_news.Count == 0) { Console.WriteLine("Новостей пока нет."); return; }
        int i = 1;
        foreach (var n in _news)
            Console.WriteLine($"{i++,2}. [{(string?)n!["date"]}] {(string?)n["ru"]?["title"]}");
    }

    private async Task DeleteAsync()
    {
        List();
        if (_news.Count == 0) return;
        Console.Write("Номер новости для удаления (Enter — отмена): ");
        if (!int.TryParse(Console.ReadLine(), out var n) || n < 1 || n > _news.Count) return;
        _news.RemoveAt(n - 1);
        await PublishAsync();
        Ok("Удалено.");
    }

    // ---------------------------------------------------------------- привилегии

    private static readonly string[] TierIds = { "tadpole", "froglet", "frogplus", "legacy", "admin" };
    private static readonly string[] TierNames = { "Tadpole (50 ₽)", "Froglet (150 ₽)", "Frog+ (300 ₽)", "Frog Legacy (700 ₽)", "Admin (выдаётся только вам и команде)" };

    private async Task GrantAsync()
    {
        Console.Write("Ник игрока в Minecraft: ");
        var nick = (Console.ReadLine() ?? "").Trim();
        if (nick == "") return;

        Console.WriteLine("Какую привилегию выдать?");
        for (int i = 0; i < TierIds.Length; i++) Console.WriteLine($"  {i + 1} — {TierNames[i]}");
        Console.Write("> ");
        if (!int.TryParse(Console.ReadLine(), out var n) || n < 1 || n > TierIds.Length) { Warn("Отмена."); return; }
        var tier = TierIds[n - 1];

        Console.Write("На сколько дней? (Enter — навсегда): ");
        string? expires = null;
        if (int.TryParse(Console.ReadLine(), out var days) && days > 0) expires = DateTime.Now.AddDays(days).ToString("yyyy-MM-dd");

        // Привязка: UUID Microsoft-аккаунта подделать нельзя, ник в оффлайн-режиме — можно.
        string key = "nick:" + nick.ToLowerInvariant();
        Console.WriteLine("Определяю UUID через Mojang…");
        var uuid = await LookupUuidAsync(nick);
        if (uuid != null)
        {
            Console.WriteLine($"Найден лицензионный аккаунт (UUID {uuid}).");
            Console.Write("Привязать к UUID (надёжно, только для входа через Microsoft) — Enter, или к нику (подойдёт и оффлайн) — введите n: ");
            if ((Console.ReadLine() ?? "").Trim().ToLowerInvariant() != "n") key = "uuid:" + uuid;
        }
        else Warn("Такого лицензионного ника нет — привязываю к нику (работает для оффлайн-режима; ник можно подделать).");

        // заменяем прежнюю выдачу этому ключу
        for (int i = _grants.Count - 1; i >= 0; i--)
            if (string.Equals((string?)_grants[i]!["key"], key, StringComparison.OrdinalIgnoreCase)) _grants.RemoveAt(i);
        _grants.Add(new JsonObject
        {
            ["key"] = key, ["name"] = nick, ["tier"] = tier,
            ["since"] = DateTime.Now.ToString("yyyy-MM-dd"), ["expires"] = expires
        });
        await PublishGrantsAsync();
        Ok($"Выдано: {nick} → {TierNames[n - 1]}{(expires != null ? " до " + expires : " навсегда")}. В лаунчере появится при ближайшем обновлении (до 3 минут или «Обновить статус»).");
        Warn($"Не забудьте выдать роль «{tier}» в Discord.");
    }

    private async Task RevokeAsync()
    {
        ListGrants();
        if (_grants.Count == 0) return;
        Console.Write("Номер для отзыва (Enter — отмена): ");
        if (!int.TryParse(Console.ReadLine(), out var n) || n < 1 || n > _grants.Count) return;
        _grants.RemoveAt(n - 1);
        await PublishGrantsAsync();
        Ok("Привилегия отозвана.");
    }

    private void ListGrants()
    {
        if (_grants.Count == 0) { Console.WriteLine("Привилегий пока никому не выдано."); return; }
        int i = 1;
        foreach (var g in _grants)
            Console.WriteLine($"{i++,2}. {(string?)g!["name"],-18} {(string?)g["tier"],-9} с {(string?)g["since"]}  до {((string?)g["expires"] ?? "∞")}   [{(string?)g["key"]}]");
    }

    private async Task<string?> LookupUuidAsync(string nick)
    {
        try
        {
            using var r = await _http.GetAsync("https://api.mojang.com/users/profiles/minecraft/" + Uri.EscapeDataString(nick));
            if (!r.IsSuccessStatusCode) return null;
            return ((string?)JsonNode.Parse(await r.Content.ReadAsStringAsync())?["id"])?.ToLowerInvariant();
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- перевод (MyMemory, ключ не нужен)

    private async Task<string> TranslateAsync(string ru)
    {
        var parts = new List<string>();
        foreach (var para in ru.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(para)) { parts.Add(""); continue; }
            var chunk = new StringBuilder();
            foreach (var sentence in Regex.Split(para, @"(?<=[.!?…])\s+"))
            {
                if (chunk.Length + sentence.Length > 420 && chunk.Length > 0) { parts.Add(await TranslateChunkAsync(chunk.ToString())); chunk.Clear(); }
                chunk.Append(chunk.Length > 0 ? " " : "").Append(sentence);
            }
            if (chunk.Length > 0) parts.Add(await TranslateChunkAsync(chunk.ToString()));
        }
        // абзацы склеиваем обратно через \n (пустые — как были)
        return string.Join("\n", parts).Trim();
    }

    private async Task<string> TranslateChunkAsync(string text)
    {
        try
        {
            var url = "https://api.mymemory.translated.net/get?langpair=ru|en&q=" + Uri.EscapeDataString(text);
            var json = JsonNode.Parse(await _http.GetStringAsync(url))!;
            var tr = (string?)json["responseData"]?["translatedText"];
            if (!string.IsNullOrWhiteSpace(tr) && !tr.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
                return System.Net.WebUtility.HtmlDecode(tr);
        }
        catch (Exception ex) { Warn("Перевод не удался: " + ex.Message); }
        Warn("Не получилось перевести автоматически — оставлю русский текст (можно поправить кнопкой «e»).");
        return text;
    }

    // ---------------------------------------------------------------- мелочи

    private void LoadConfig()
    {
        if (File.Exists(ConfigPath)) _cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(ConfigPath)) ?? new();
    }
    private void SaveConfig() => File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_cfg, new JsonSerializerOptions { WriteIndented = true }));

    private static void Banner()
    {
        Console.Title = "FrogLauncher — новости";
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("🐸  FrogLauncher — публикация новостей");
        Console.WriteLine("    Пишите по-русски — английская версия создаётся автоматически.");
        Console.ResetColor();
    }
    private static void Ok(string m) { Console.ForegroundColor = ConsoleColor.Green; Console.WriteLine(m); Console.ResetColor(); }
    private static void Warn(string m) { Console.ForegroundColor = ConsoleColor.Yellow; Console.WriteLine(m); Console.ResetColor(); }
    private static void Err(string m) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine(m); Console.ResetColor(); }
    private static void Pause() { Console.WriteLine("Нажмите Enter…"); Console.ReadLine(); }
}

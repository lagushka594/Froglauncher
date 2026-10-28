using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.Services;

public sealed class SkinEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    /// <summary>Модель «Alex» (тонкие руки 3 px); иначе «Steve» (4 px).</summary>
    public bool Slim { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class SkinLibrary
{
    public List<SkinEntry> Skins { get; set; } = new();
    /// <summary>Активный скин по id игрового аккаунта.</summary>
    public Dictionary<string, string> Active { get; set; } = new();
}

public sealed class SkinException(string message) : Exception(message);

/// <summary>
/// Скины: библиотека PNG в %AppData%\RibbixLauncher\skins и переключение в один клик.
/// Microsoft-аккаунт — скин загружается в профиль Minecraft (api.minecraftservices.com), его видят все: в мирах и на серверах.
/// Оффлайн-ник — официального способа нет, поэтому лаунчер собирает ресурспак «Ribbix Skin»: он подменяет стандартные
/// скины (Steve, Alex и др.), и при запуске включает его в options.txt. Так ваш скин виден вам в мирах и на серверах;
/// другие игроки видят его только на серверах со SkinsRestorer.
/// </summary>
public sealed class SkinService(IHttpClientFactory httpFactory, ILogger<SkinService> log)
{
    public const string PackFolder = "Ribbix Skin";
    private const string Services = "https://api.minecraftservices.com/";

    public static string Dir => Path.Combine(LauncherPaths.Root, "skins");
    private static string File => Path.Combine(Dir, "skins.json");
    private HttpClient Http => httpFactory.CreateClient(DownloadService.HttpClientName);

    public SkinLibrary Load()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<SkinLibrary>(System.IO.File.ReadAllText(File), Json.Options) ?? new SkinLibrary();
        }
        catch (Exception ex) when (ex is JsonException or IOException) { log.LogWarning(ex, "skins.json повреждён"); }
        return new SkinLibrary();
    }

    public void Save(SkinLibrary lib)
    {
        Directory.CreateDirectory(Dir);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(lib, Json.Options));
    }

    public static string PathOf(SkinEntry s) => Path.Combine(Dir, s.FileName);

    /// <summary>Проверка PNG: подпись и размер 64×64 (современный) или 64×32 (старый формат).</summary>
    internal static (int W, int H) CheckPng(byte[] png)
    {
        if (png.Length < 24 || png[0] != 0x89 || png[1] != 'P' || png[2] != 'N' || png[3] != 'G')
            throw new SkinException(L.T("Это не PNG-картинка"));
        int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
        int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        if (w != 64 || (h != 64 && h != 32)) throw new SkinException(L.T($"Скин должен быть 64×64 (или старый 64×32), а здесь {w}×{h}"));
        return (w, h);
    }

    public SkinEntry Add(SkinLibrary lib, byte[] png, string name, bool slim)
    {
        CheckPng(png);
        var entry = new SkinEntry { Name = string.IsNullOrWhiteSpace(name) ? L.T("Скин") : name.Trim(), Slim = slim };
        entry.FileName = entry.Id + ".png";
        Directory.CreateDirectory(Dir);
        System.IO.File.WriteAllBytes(PathOf(entry), png);
        lib.Skins.Insert(0, entry);
        Save(lib);
        return entry;
    }

    public void Remove(SkinLibrary lib, SkinEntry s)
    {
        lib.Skins.Remove(s);
        foreach (var k in lib.Active.Where(kv => kv.Value == s.Id).Select(kv => kv.Key).ToList()) lib.Active.Remove(k);
        try { System.IO.File.Delete(PathOf(s)); } catch (IOException) { }
        Save(lib);
    }

    /// <summary>Скин лицензионного игрока по нику: Mojang API → профиль → textures.SKIN.</summary>
    public async Task<(byte[] Png, bool Slim)> DownloadByNickAsync(string nick, CancellationToken ct)
    {
        using var resp = await Http.GetAsync("https://api.mojang.com/users/profiles/minecraft/" + Uri.EscapeDataString(nick.Trim()), ct);
        if (!resp.IsSuccessStatusCode) throw new SkinException(L.T($"Игрок «{nick}» не найден — скин есть только у лицензионных аккаунтов"));
        var id = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["id"]?.GetValue<string>() ?? throw new SkinException(L.T("Mojang не вернул профиль"));
        var profile = JsonNode.Parse(await Http.GetStringAsync("https://sessionserver.mojang.com/session/minecraft/profile/" + id, ct));
        var value = profile?["properties"]?.AsArray().FirstOrDefault(p => p?["name"]?.GetValue<string>() == "textures")?["value"]?.GetValue<string>()
                    ?? throw new SkinException(L.T("У игрока стандартный скин"));
        var tex = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value)))?["textures"]?["SKIN"];
        var url = tex?["url"]?.GetValue<string>() ?? throw new SkinException(L.T("У игрока стандартный скин"));
        if (!url.StartsWith("http://textures.minecraft.net/", StringComparison.Ordinal) && !url.StartsWith("https://textures.minecraft.net/", StringComparison.Ordinal))
            throw new SkinException(L.T("Неожиданный адрес скина"));
        var png = await Http.GetByteArrayAsync(url.Replace("http://", "https://"), ct);
        return (png, tex?["metadata"]?["model"]?.GetValue<string>() == "slim");
    }

    /// <summary>Сделать скин активным. Для Microsoft — сразу загрузить в профиль Minecraft.</summary>
    public async Task ApplyAsync(SkinLibrary lib, SkinEntry s, GameAccount account, CancellationToken ct)
    {
        if (account.Type == AccountType.Microsoft) await UploadAsync(account, s, ct);
        lib.Active[account.Id.ToString()] = s.Id;
        Save(lib);
    }

    public void ClearActive(SkinLibrary lib, GameAccount account)
    {
        lib.Active.Remove(account.Id.ToString());
        Save(lib);
    }

    private async Task UploadAsync(GameAccount account, SkinEntry s, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(s.Slim ? "slim" : "classic"), "variant");
        var file = new ByteArrayContent(await System.IO.File.ReadAllBytesAsync(PathOf(s), ct));
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "skin.png");
        using var req = new HttpRequestMessage(HttpMethod.Post, Services + "minecraft/profile/skins") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            log.LogWarning("Загрузка скина: {Code} {Body}", (int)resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
            throw new SkinException(resp.StatusCode == System.Net.HttpStatusCode.Unauthorized
                ? L.T("Mojang не принял вход — перезайдите в аккаунт Microsoft в «Профиле»")
                : L.T($"Mojang не принял скин (код {(int)resp.StatusCode})"));
        }
        log.LogInformation("Скин {Skin} загружен в профиль {Nick}", s.Name, account.Name);
    }

    // ---------- Оффлайн: ресурспак со стандартными скинами ----------

    /// <summary>Перед запуском: для оффлайн-аккаунта с активным скином — собрать «Ribbix Skin» и включить; иначе — выключить.</summary>
    public void PrepareLaunch(Instance inst, GameAccount account)
    {
        try
        {
            var lib = Load();
            var skin = account.Type == AccountType.Offline && lib.Active.TryGetValue(account.Id.ToString(), out var id)
                ? lib.Skins.FirstOrDefault(x => x.Id == id) : null;
            var opt = OptionsFile.Load(inst.GameDirectory);
            if (skin is not null && System.IO.File.Exists(PathOf(skin)))
            {
                BuildPack(Path.Combine(inst.GameDirectory, "resourcepacks", PackFolder), System.IO.File.ReadAllBytes(PathOf(skin)));
                SetPack(opt, enabled: true);
            }
            else SetPack(opt, enabled: false);
            if (opt.Existed || skin is not null) opt.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            log.LogWarning(ex, "Не удалось подготовить оффлайн-скин");
        }
    }

    /// <summary>Все стандартные скины (1.19.3+: 9 персонажей × 2 модели; раньше — Steve и Alex) заменяются скином игрока.</summary>
    internal static readonly string[] DefaultSkins = new[] { "textures/entity/steve.png", "textures/entity/alex.png" }
        .Concat(new[] { "alex", "ari", "efe", "kai", "makena", "noor", "steve", "sunny", "zuri" }
            .SelectMany(n => new[] { $"textures/entity/player/wide/{n}.png", $"textures/entity/player/slim/{n}.png" }))
        .ToArray();

    internal static void BuildPack(string dir, byte[] png)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        foreach (var rel in DefaultSkins)
        {
            var path = Path.Combine(dir, "assets", "minecraft", rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllBytes(path, png);
        }
        // supported_formats — для 1.20.2+, pack_format 15 — середина; старые версии всё равно грузят пак из incompatibleResourcePacks.
        System.IO.File.WriteAllText(Path.Combine(dir, "pack.mcmeta"),
            "{\"pack\":{\"pack_format\":15,\"supported_formats\":[1,999],\"description\":\"Ribbix Skin\"}}");
    }

    /// <summary>Пак — последним (наивысший приоритет) и в incompatibleResourcePacks, чтобы игра не отключила его из-за версии формата.</summary>
    internal static void SetPack(OptionsFile opt, bool enabled)
    {
        const string id = "file/" + PackFolder;
        var packs = opt.GetResourcePacks().Where(p => p != id).ToList();
        var incompatible = ReadList(opt, "incompatibleResourcePacks").Where(p => p != id).ToList();
        if (enabled)
        {
            packs.Add(id);
            incompatible.Add(id);
        }
        if (!enabled && !opt.Has("resourcePacks")) return;
        opt.SetResourcePacks(packs);
        opt.Set("incompatibleResourcePacks", JsonSerializer.Serialize(incompatible));
    }

    private static List<string> ReadList(OptionsFile opt, string key)
    {
        try { return JsonNode.Parse(opt.Get(key) ?? "[]")?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? new(); }
        catch (JsonException) { return new(); }
    }
}

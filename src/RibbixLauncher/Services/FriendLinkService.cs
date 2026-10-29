using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.Services;

/// <summary>Приглашение «Играть с другом»: версия, загрузчик, файлы Modrinth по ID версий и сервер.</summary>
public sealed record FriendInvite(string Name, string GameVersion, LoaderType Loader, string? LoaderVersion, string? Server,
    IReadOnlyList<(string Folder, string VersionId)> Files)
{
    /// <summary>Один и тот же набор — одна версия у друга: повторная ссылка не плодит копии.</summary>
    public string Key
    {
        get
        {
            var raw = $"{GameVersion}|{Loader}|{LoaderVersion}|{string.Join(",", Files.Select(f => f.Folder + "/" + f.VersionId).Order())}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16].ToLowerInvariant();
        }
    }
}

public sealed record FriendLink(string Url, string? WebUrl, int Included, IReadOnlyList<string> NotOnModrinth);

/// <summary>
/// Приглашения через буфер обмена. Discord не делает ribbix:// кликабельным, поэтому друг просто копирует ссылку —
/// лаунчер при переключении в него сам предложит её открыть. Свои скопированные ссылки не предлагаем.
/// </summary>
public static class InviteClipboard
{
    private static readonly HashSet<string> Seen = new();

    /// <summary>Скопировать свою ссылку (её же лаунчер потом не предложит открыть). false — буфер занят.</summary>
    public static bool Copy(string text)
    {
        try { System.Windows.Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { return false; }
        Seen.Add(text.Trim());
        return true;
    }

    /// <summary>Ссылка-приглашение ribbix://play или ribbix://pack, которую ещё не предлагали. Каждую — один раз.</summary>
    public static Uri? Take(string? text)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 8000 || Seen.Contains(text)) return null;
        if (!text.StartsWith("ribbix://play?", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("ribbix://pack?", StringComparison.OrdinalIgnoreCase)) return null;
        Seen.Add(text);
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) ? uri : null;
    }
}

/// <summary>
/// «Играть с другом» по одной ссылке: ribbix://play?… — у друга сам ставится тот же Minecraft, загрузчик
/// и моды (только файлы Modrinth: ссылка несёт ID версий, скачивание — с cdn.modrinth.com с проверкой SHA1),
/// и игра сразу подключается к серверу. Discord не делает ribbix:// кликабельным, поэтому есть и
/// https-ссылка через API лаунчера (страница /play с кнопкой «Открыть»).
/// </summary>
public sealed partial class FriendLinkService(IHttpClientFactory httpFactory, DownloadService downloads, InstanceService instances,
    ModLoaderService loaders, Microsoft.Extensions.Options.IOptions<LauncherOptions> options, ILogger<FriendLinkService> log)
{
    private const string Api = "https://api.modrinth.com/v2/";
    public const int MaxFiles = 300;
    public static readonly string[] Folders = { "mods", "resourcepacks", "shaderpacks" };
    private static readonly Dictionary<string, string> Prefix = new() { ["mods"] = "", ["resourcepacks"] = "r~", ["shaderpacks"] = "s~" };

    private HttpClient Http => httpFactory.CreateClient(DownloadService.HttpClientName);

    public async Task<FriendLink> CreateAsync(Instance inst, string? server, CancellationToken ct)
    {
        var files = new List<(string Folder, string Path, string Sha1)>();
        foreach (var folder in Folders)
        {
            var dir = Path.Combine(inst.GameDirectory, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir).Where(f => f.EndsWith(".jar") || f.EndsWith(".zip")))
                files.Add((folder, f, ModToolsService.Sha1(f)));
        }
        var ids = new Dictionary<string, string>();
        foreach (var chunk in files.Select(f => f.Sha1).Distinct().Chunk(500))
        {
            using var resp = await Http.PostAsJsonAsync(Api + "version_files", new { hashes = chunk, algorithm = "sha1" }, ct);
            resp.EnsureSuccessStatusCode();
            foreach (var (hash, version) in JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!.AsObject())
                if (version?["id"]?.GetValue<string>() is { } id) ids[hash] = id;
        }
        var included = files.Where(f => ids.ContainsKey(f.Sha1)).Select(f => (f.Folder, ids[f.Sha1])).Distinct().Take(MaxFiles).ToList();
        var missing = files.Where(f => !ids.ContainsKey(f.Sha1)).Select(f => Path.GetFileName(f.Path)).ToList();
        var invite = new FriendInvite(inst.Name, inst.GameVersion, inst.Loader, inst.LoaderVersion, string.IsNullOrWhiteSpace(server) ? null : server.Trim(), included);
        var query = Query(invite);
        var api = options.Value.ApiBaseUrl;
        // Пока API не развёрнут (адрес-заглушка), https-ссылку не предлагаем.
        var web = api.Contains(".example", StringComparison.OrdinalIgnoreCase) ? null : api.TrimEnd('/') + "/play?" + query;
        return new FriendLink("ribbix://play?" + query, web, included.Count, missing);
    }

    /// <summary>Секрет для кнопки «Присоединиться» в Discord (≤ 128 символов): версия, загрузчик и сервер, без модов.</summary>
    public static string JoinSecret(Instance inst, string server)
    {
        var q = Query(new FriendInvite(inst.Name.Length > 24 ? inst.Name[..24] : inst.Name, inst.GameVersion, inst.Loader, inst.LoaderVersion, server,
            Array.Empty<(string, string)>()));
        return q.Length <= 128 ? q : Query(new FriendInvite("Ribbix", inst.GameVersion, inst.Loader, inst.LoaderVersion, server, Array.Empty<(string, string)>()));
    }

    internal static string Query(FriendInvite i)
    {
        var parts = new List<string>
        {
            "n=" + Uri.EscapeDataString(i.Name), "mc=" + Uri.EscapeDataString(i.GameVersion), "l=" + i.Loader.ToString().ToLowerInvariant(),
        };
        if (i.LoaderVersion is { } lv) parts.Add("lv=" + Uri.EscapeDataString(lv));
        if (i.Server is { } s) parts.Add("s=" + Uri.EscapeDataString(s));
        if (i.Files.Count > 0) parts.Add("f=" + string.Join(".", i.Files.Select(f => Prefix[f.Folder] + f.VersionId)));
        return string.Join("&", parts);
    }

    /// <summary>Разбор ссылки. Всё, что пришло извне, проверяется: ID версий — только [A-Za-z0-9], папки — из белого списка.</summary>
    public static FriendInvite? Parse(Uri uri)
    {
        if (uri.Host != "play" && !uri.AbsolutePath.TrimEnd('/').EndsWith("/play")) return null;
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var mc = q["mc"];
        if (string.IsNullOrWhiteSpace(mc) || !SafeRegex().IsMatch(mc)) return null;
        if (!Enum.TryParse<LoaderType>(q["l"] ?? "vanilla", ignoreCase: true, out var loader)) return null;
        var lv = q["lv"];
        if (lv is not null && !SafeRegex().IsMatch(lv)) return null;
        var files = new List<(string, string)>();
        foreach (var token in (q["f"] ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries).Take(MaxFiles))
        {
            var (folder, id) = token.StartsWith("r~") ? ("resourcepacks", token[2..]) : token.StartsWith("s~") ? ("shaderpacks", token[2..]) : ("mods", token);
            if (!IdRegex().IsMatch(id)) return null;
            files.Add((folder, id));
        }
        var server = q["s"];
        if (server is not null && !ServerRegex().IsMatch(server)) server = null;
        var name = (q["n"] ?? "").Trim();
        if (name.Length == 0) name = L.T("Сборка друга");
        return new FriendInvite(name.Length > 60 ? name[..60] : name, mc, loader, lv, server, files);
    }

    [GeneratedRegex(@"^[\w.\-+]{1,64}$")] private static partial Regex SafeRegex();
    [GeneratedRegex(@"^[A-Za-z0-9]{8,16}$")] private static partial Regex IdRegex();
    [GeneratedRegex(@"^[A-Za-z0-9.\-]{1,253}(:\d{1,5})?$")] private static partial Regex ServerRegex();

    /// <summary>Уже есть версия из этой же ссылки — используем её, иначе создаём новую.</summary>
    public Instance? Existing(FriendInvite invite) => instances.List().FirstOrDefault(i => i.ShareKey == invite.Key);

    public async Task<Instance> InstallAsync(FriendInvite invite, int ramMb, IProgress<DownloadProgress>? progress, CancellationToken ct) =>
        (await InstallWithFilesAsync(invite, ramMb, progress, ct)).Instance;

    public async Task<(Instance Instance, List<string> Files)> InstallWithFilesAsync(FriendInvite invite, int ramMb, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var inst = Existing(invite);
        var created = inst is null;
        inst ??= instances.Create(invite.Name, invite.GameVersion, invite.Loader, invite.LoaderVersion, ramMb);
        inst.ShareKey = invite.Key;
        try
        {
            var files = await DownloadIntoAsync(inst, invite, progress, ct);
            instances.Save(inst);
            log.LogInformation("Сборка друга {Name}: {Count} файлов в {Instance}", invite.Name, files.Count, inst.Id);
            return (inst, files);
        }
        catch when (created)
        {
            instances.Delete(inst);
            throw;
        }
    }

    /// <summary>Загрузчик и файлы приглашения в существующую версию. Возвращает пути файлов относительно .minecraft.</summary>
    public async Task<List<string>> DownloadIntoAsync(Instance inst, FriendInvite invite, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        {
            progress?.Report(DownloadProgress.Indeterminate(L.T("Устанавливаем Minecraft и загрузчик")));
            inst.LaunchVersionId = await loaders.InstallAsync(inst.Loader, inst.GameVersion, inst.LoaderVersion, progress, ct);

            var items = new List<DownloadItem>();
            foreach (var chunk in invite.Files.Chunk(100))
            {
                var ids = "[" + string.Join(",", chunk.Select(f => $"\"{f.VersionId}\"")) + "]";
                var versions = JsonNode.Parse(await Http.GetStringAsync(Api + "versions?ids=" + Uri.EscapeDataString(ids), ct))!.AsArray();
                foreach (var v in versions)
                {
                    var folder = chunk.First(f => f.VersionId == v!["id"]!.GetValue<string>()).Folder;
                    var files = v!["files"]!.AsArray();
                    var file = files.FirstOrDefault(f => f!["primary"]?.GetValue<bool>() == true) ?? files.First();
                    var url = file!["url"]!.GetValue<string>();
                    if (!ModpackService.IsAllowedUrl(url)) continue;
                    var name = Path.GetFileName(file["filename"]!.GetValue<string>());
                    items.Add(new DownloadItem(url, Path.Combine(inst.GameDirectory, folder, name), file["hashes"]?["sha1"]?.GetValue<string>(),
                        file["size"]?.GetValue<long>() ?? 0, QuickCheck: true));
                }
            }
            await downloads.DownloadAsync(items, L.T("Скачиваем моды друга"), progress, ct);
            return items.Select(i => Path.GetRelativePath(inst.GameDirectory, i.Path)).ToList();
        }
    }
}

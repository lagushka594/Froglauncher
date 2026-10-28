using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.Services;

public sealed class GitHubToken
{
    public string Token { get; set; } = "";
}

/// <summary>
/// «Общая сборка» для компании: хост хранит список модов (то же приглашение, что «Играть с другом») в своём
/// закрытом GitHub Gist и обновляет его при каждом запуске с изменёнными модами. У друзей сборка сама
/// подтягивает изменения при открытии лаунчера: новые файлы скачиваются, убранные хостом — удаляются.
/// Токен GitHub (только право gist) хранится у хоста зашифрованным (DPAPI); друзьям аккаунт не нужен.
/// </summary>
public sealed partial class SharedPackService(IHttpClientFactory httpFactory, FriendLinkService friends, InstanceService instances,
    ILogger<SharedPackService> log)
{
    private const string Api = "https://api.github.com/";
    public const string FileName = "ribbix-pack.txt";
    public const string TokenPage = "https://github.com/settings/tokens/new?scopes=gist&description=Ribbix%20Launcher";
    private static string TokenFile => Path.Combine(LauncherPaths.Root, "github.dat");

    private HttpClient Http
    {
        get
        {
            var h = httpFactory.CreateClient(DownloadService.HttpClientName);
            h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return h;
        }
    }

    public bool HasToken => ProtectedStore.Load<GitHubToken>(TokenFile) is { Token.Length: > 0 };

    public void SaveToken(string token)
    {
        token = token.Trim();
        if (!TokenRegex().IsMatch(token)) throw new InvalidOperationException(L.T("Это не похоже на токен GitHub (ghp_… или github_pat_…)"));
        ProtectedStore.Save(TokenFile, new GitHubToken { Token = token });
    }

    public void ForgetToken() => ProtectedStore.Delete(TokenFile);

    [GeneratedRegex(@"^(ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|gho_[A-Za-z0-9]{30,})$")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"^[0-9a-f]{20,40}$")]
    private static partial Regex GistRegex();

    public static bool IsGistId(string? id) => id is not null && GistRegex().IsMatch(id);

    public static string ShareLink(string gist) => "ribbix://pack?gist=" + gist;

    // ---------- Хост ----------

    /// <summary>Сделать сборку общей (или обновить): приглашение → закрытый gist. Возвращает ссылку для друзей.</summary>
    public async Task<string> PublishAsync(Instance inst, string? server, CancellationToken ct)
    {
        var token = ProtectedStore.Load<GitHubToken>(TokenFile)?.Token ?? throw new InvalidOperationException(L.T("Сначала вставьте токен GitHub"));
        var link = await friends.CreateAsync(inst, server, ct);
        var query = link.Url[(link.Url.IndexOf('?') + 1)..];
        var body = new JsonObject
        {
            ["description"] = $"Ribbix Launcher — {inst.Name}",
            ["public"] = false,
            ["files"] = new JsonObject { [FileName] = new JsonObject { ["content"] = query } },
        };
        using var req = new HttpRequestMessage(inst.HostGist is null ? HttpMethod.Post : HttpMethod.Patch, Api + "gists" + (inst.HostGist is null ? "" : "/" + inst.HostGist))
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await Http.SendAsync(req, ct);
        if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException(L.T("GitHub не принял токен — создайте новый с правом gist"));
        resp.EnsureSuccessStatusCode();
        inst.HostGist = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["id"]?.GetValue<string>() ?? inst.HostGist;
        inst.HostShareKey = FriendLinkService.Parse(new Uri("ribbix://play?" + query))?.Key;
        instances.Save(inst);
        log.LogInformation("Общая сборка {Instance} → gist {Gist}", inst.Id, inst.HostGist);
        return ShareLink(inst.HostGist!);
    }

    /// <summary>Перед запуском у хоста: если моды поменялись — обновить gist (друзья подтянут сами).</summary>
    public async Task PublishIfChangedAsync(Instance inst, CancellationToken ct)
    {
        if (inst.HostGist is null || !HasToken) return;
        try
        {
            var link = await friends.CreateAsync(inst, null, ct);
            var key = FriendLinkService.Parse(new Uri(link.Url))?.Key;
            if (key == inst.HostShareKey) return;
            await PublishAsync(inst, null, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException or IOException)
        {
            log.LogWarning(ex, "Общая сборка не обновлена");
        }
    }

    // ---------- Друг ----------

    public async Task<FriendInvite> FetchAsync(string gist, CancellationToken ct)
    {
        if (!IsGistId(gist)) throw new InvalidOperationException(L.T("Неверная ссылка на общую сборку"));
        var json = JsonNode.Parse(await Http.GetStringAsync(Api + "gists/" + gist, ct));
        var content = json?["files"]?[FileName]?["content"]?.GetValue<string>() ?? throw new InvalidOperationException(L.T("В gist нет сборки Ribbix"));
        return FriendLinkService.Parse(new Uri("ribbix://play?" + content.Trim())) ?? throw new InvalidOperationException(L.T("Сборка в gist повреждена"));
    }

    /// <summary>Вступить в общую сборку: поставить её и запомнить gist для автообновления.</summary>
    public async Task<Instance> JoinAsync(string gist, int ramMb, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var invite = await FetchAsync(gist, ct);
        var existing = instances.List().FirstOrDefault(i => i.SharedGist == gist);
        if (existing is not null)
        {
            await SyncAsync(existing, progress, ct);
            return existing;
        }
        var (inst, files) = await friends.InstallWithFilesAsync(invite, ramMb, progress, ct);
        inst.SharedGist = gist;
        inst.SharedFiles = files;
        instances.Save(inst);
        return inst;
    }

    /// <summary>Подтянуть изменения хоста. true — что-то поменялось.</summary>
    public async Task<bool> SyncAsync(Instance inst, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (!IsGistId(inst.SharedGist)) return false;
        var invite = await FetchAsync(inst.SharedGist!, ct);
        if (invite.Key == inst.ShareKey) return false;
        var files = await friends.DownloadIntoAsync(inst, invite, progress, ct);
        // Убрать файлы, которые раньше пришли из общей сборки, а теперь хост их удалил.
        foreach (var old in inst.SharedFiles.Except(files, StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.GetFullPath(Path.Combine(inst.GameDirectory, old));
            if (path.StartsWith(Path.GetFullPath(inst.GameDirectory), StringComparison.OrdinalIgnoreCase))
                try { File.Delete(path); } catch (IOException) { }
        }
        inst.ShareKey = invite.Key;
        inst.SharedFiles = files;
        instances.Save(inst);
        log.LogInformation("Общая сборка {Instance} обновлена: {Count} файлов", inst.Id, files.Count);
        return true;
    }
}

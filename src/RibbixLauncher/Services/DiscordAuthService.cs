using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RibbixLauncher.Core;
using Ribbix.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RibbixLauncher.Services;

/// <summary>Сессия Discord (discord.dat, DPAPI).</summary>
public sealed class DiscordSession
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public string UserId { get; set; } = "";
    public string Username { get; set; } = "";
    public string? GlobalName { get; set; }
    public string? AvatarUrl { get; set; }

    public string DisplayName => string.IsNullOrEmpty(GlobalName) ? Username : GlobalName;
}

/// <summary>Участник сервера: роли (ID) или null, если пользователь не состоит на сервере.</summary>
public sealed record DiscordMember(IReadOnlyList<string> RoleIds, string? Nick);

public sealed class DiscordAuthException(string message, bool sessionExpired = false) : Exception(message)
{
    public bool SessionExpired { get; } = sessionExpired;
}

/// <summary>
/// Вход через Discord без своего backend: OAuth2 Authorization Code + PKCE для публичного клиента
/// (в Developer Portal включён «Public Client», client secret не нужен и в лаунчере не хранится).
/// Ответ принимаем на http://127.0.0.1:{порт}/callback — этот адрес добавляется в OAuth2 → Redirects.
/// Scopes: identify (ник, аватар) + guilds.members.read (роли на вашем сервере).
/// </summary>
public sealed class DiscordAuthService(IHttpClientFactory httpFactory, IOptions<LauncherOptions> options, ILogger<DiscordAuthService> log)
{
    private const string ApiBase = "https://discord.com/api/v10/";
    private const string Scopes = "identify guilds.members.read";
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private DiscordSession? _session = ProtectedStore.Load<DiscordSession>(LauncherPaths.DiscordFile);

    private LauncherOptions O => options.Value;
    private string RedirectUri => $"http://127.0.0.1:{O.DiscordRedirectPort}/callback";
    private HttpClient Http => httpFactory.CreateClient(DownloadService.HttpClientName);

    public DiscordSession? Current => _session;
    public bool IsLoggedIn => _session is not null;
    public event Action? Changed;

    /// <summary>Открыть браузер, дождаться подтверждения входа и обменять код на токены.</summary>
    public async Task<DiscordSession> LoginAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(O.DiscordApplicationId))
            throw new DiscordAuthException(L.T("Не задан DiscordApplicationId в appsettings.json"));

        var verifier = B64Url(RandomNumberGenerator.GetBytes(48));
        var state = B64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new TcpListener(IPAddress.Loopback, O.DiscordRedirectPort);
        try { listener.Start(); }
        catch (SocketException)
        {
            throw new DiscordAuthException(L.T($"Порт {O.DiscordRedirectPort} занят другой программой — закройте её и повторите вход"));
        }

        var url = "https://discord.com/oauth2/authorize" +
                  $"?client_id={O.DiscordApplicationId}&response_type=code" +
                  $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}&scope={Uri.EscapeDataString(Scopes)}" +
                  $"&state={state}&code_challenge={PkceChallenge(verifier)}&code_challenge_method=S256";
        Shell.Open(url);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var code = await WaitForCodeAsync(listener, state, timeout.Token);

        using var resp = await Http.PostAsync(ApiBase + "oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = O.DiscordApplicationId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["code_verifier"] = verifier,
        }), ct);
        var token = await ReadTokenAsync(resp, ct);

        var session = new DiscordSession
        {
            AccessToken = token.access,
            RefreshToken = token.refresh,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.expiresIn - 60),
        };
        await FillProfileAsync(session, ct);
        Save(session);
        log.LogInformation("Вход через Discord: {User}", session.Username);
        return session;
    }

    public void Logout()
    {
        _session = null;
        ProtectedStore.Delete(LauncherPaths.DiscordFile);
        Changed?.Invoke();
    }

    /// <summary>Роли пользователя на сервере; null — пользователь не вступил на сервер.</summary>
    public async Task<DiscordMember?> GetMemberAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(O.DiscordGuildId))
            throw new DiscordAuthException(L.T("Не задан DiscordGuildId в appsettings.json"));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var token = await GetAccessTokenAsync(force: attempt > 0, ct);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}users/@me/guilds/{O.DiscordGuildId}/member");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await Http.SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                Logout();
                throw new DiscordAuthException(L.T("Сессия Discord истекла — войдите заново"), sessionExpired: true);
            }
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                throw new HttpRequestException(L.T("Discord ограничил частоту запросов, попробуйте через минуту"));
            resp.EnsureSuccessStatusCode();

            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;
            var roles = json["roles"]?.AsArray().Select(r => r!.GetValue<string>()).ToList() ?? new List<string>();
            return new DiscordMember(roles, json["nick"]?.GetValue<string>());
        }

        throw new DiscordAuthException(L.T("Сессия Discord истекла — войдите заново"), sessionExpired: true);
    }

    /// <summary>Refresh-токен Discord одноразовый: обновляем в один поток и сохраняем новый.</summary>
    private async Task<string> GetAccessTokenAsync(bool force, CancellationToken ct)
    {
        var s = _session ?? throw new DiscordAuthException(L.T("Войдите через Discord"), sessionExpired: true);
        if (!force && s.ExpiresAt > DateTimeOffset.UtcNow) return s.AccessToken;

        await _refreshLock.WaitAsync(ct);
        try
        {
            if (_session is null) throw new DiscordAuthException(L.T("Войдите через Discord"), sessionExpired: true);
            if (!force && _session.ExpiresAt > DateTimeOffset.UtcNow) return _session.AccessToken;

            using var resp = await Http.PostAsync(ApiBase + "oauth2/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = O.DiscordApplicationId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = _session.RefreshToken,
            }), ct);
            if (resp.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                Logout();
                throw new DiscordAuthException(L.T("Сессия Discord истекла — войдите заново"), sessionExpired: true);
            }

            var token = await ReadTokenAsync(resp, ct);
            _session.AccessToken = token.access;
            _session.RefreshToken = token.refresh;
            _session.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.expiresIn - 60);
            ProtectedStore.Save(LauncherPaths.DiscordFile, _session);
            return _session.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task FillProfileAsync(DiscordSession s, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + "users/@me");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.AccessToken);
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var me = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;
        s.UserId = me["id"]!.GetValue<string>();
        s.Username = me["username"]?.GetValue<string>() ?? "discord";
        s.GlobalName = me["global_name"]?.GetValue<string>();
        s.AvatarUrl = me["avatar"]?.GetValue<string>() is { Length: > 0 } avatar
            ? $"https://cdn.discordapp.com/avatars/{s.UserId}/{avatar}.png?size=64"
            : null;
    }

    private void Save(DiscordSession s)
    {
        _session = s;
        ProtectedStore.Save(LauncherPaths.DiscordFile, s);
        Changed?.Invoke();
    }

    private static async Task<(string access, string refresh, int expiresIn)> ReadTokenAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            var error = JsonNode.Parse(text)?["error_description"]?.GetValue<string>() ?? L.T($"код {(int)resp.StatusCode}");
            throw new DiscordAuthException(L.T("Discord отклонил вход: ") + error +
                                           L.T(". Проверьте в Developer Portal: OAuth2 → Redirects и галочку Public Client (см. README)."));
        }
        var j = JsonNode.Parse(text)!;
        return (j["access_token"]!.GetValue<string>(), j["refresh_token"]!.GetValue<string>(), j["expires_in"]?.GetValue<int>() ?? 604800);
    }

    /// <summary>Принимаем один запрос браузера на /callback, проверяем state, отдаём страничку «можно закрыть».</summary>
    internal static async Task<string> WaitForCodeAsync(TcpListener listener, string expectedState, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var requestLine = await reader.ReadLineAsync(ct) ?? "";
            var target = requestLine.Split(' ').ElementAtOrDefault(1) ?? "";

            if (!target.StartsWith("/callback", StringComparison.Ordinal))
            {
                await RespondAsync(stream, 404, "Not found", ct);
                continue; // например, браузер запросил /favicon.ico
            }

            var query = new Uri("http://127.0.0.1" + target).Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

            if (query.TryGetValue("error", out var error))
            {
                await RespondAsync(stream, 200, Page(L.T("Вход отменён"), L.T("Можно закрыть вкладку и вернуться в Ribbix.")), ct);
                throw new DiscordAuthException(error == "access_denied" ? L.T("Вход через Discord отменён") : "Discord: " + error);
            }

            if (!query.TryGetValue("state", out var state) || state != expectedState || !query.TryGetValue("code", out var code))
            {
                await RespondAsync(stream, 400, Page(L.T("Ошибка входа"), L.T("Неверный ответ — начните вход заново из лаунчера.")), ct);
                throw new DiscordAuthException(L.T("Неверный ответ Discord (state). Начните вход заново."));
            }

            await RespondAsync(stream, 200, Page(L.T("Готово! 🐸"), L.T("Вы вошли через Discord. Вкладку можно закрыть и вернуться в Ribbix.")), ct);
            return code;
        }
    }

    private static async Task RespondAsync(Stream stream, int status, string html, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static string Page(string title, string text) =>
        "<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>Ribbix</title>" +
        "<style>body{background:#0b1220;color:#d1fae5;font-family:Segoe UI,sans-serif;display:grid;place-items:center;height:100vh;margin:0}</style>" +
        $"</head><body><div style=\"text-align:center\"><div style=\"font-size:64px\">🐸</div><h2>{title}</h2><p>{text}</p></div></body></html>";

    private static string B64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>PKCE S256: base64url(SHA256(verifier)).</summary>
    internal static string PkceChallenge(string verifier) =>
        B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}

/// <summary>Роли Discord → тариф: берём самый высокий тариф среди сопоставленных ролей.</summary>
public static class DiscordRoleMapper
{
    public static TierDto Resolve(IReadOnlyCollection<string> roleIds, IReadOnlyDictionary<string, string> roleToTier, TierCatalog tiers)
    {
        var best = roleIds
            .Select(id => roleToTier.TryGetValue(id, out var slug) ? tiers.Get(slug) : null)
            .OfType<TierDto>()
            .OrderByDescending(t => t.Rank)
            .FirstOrDefault();
        return best ?? tiers.Tadpole;
    }
}

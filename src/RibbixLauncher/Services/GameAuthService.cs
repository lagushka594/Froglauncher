using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RibbixLauncher.Services;

public sealed record DeviceCodeInfo(string UserCode, string VerificationUri, string DeviceCode, int IntervalSeconds, DateTimeOffset ExpiresAt);

public sealed class GameAuthException(string message) : Exception(message);

/// <summary>
/// Игровые аккаунты: оффлайн (ник + UUID) и Microsoft (OAuth 2.0 Device Code → Xbox Live → XSTS → Minecraft).
/// Все токены — только в accounts.dat под DPAPI. Refresh-токен Microsoft ротируется при каждом обновлении.
/// </summary>
public sealed partial class GameAuthService(
    IHttpClientFactory httpFactory,
    IOptions<LauncherOptions> options,
    ILogger<GameAuthService> log)
{
    private const string Scope = "XboxLive.signin offline_access";
    private const string MsAuthority = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";
    private readonly object _fileLock = new();

    private HttpClient Http => httpFactory.CreateClient(DownloadService.HttpClientName);
    private string ClientId => options.Value.MicrosoftClientId;

    public AccountsFile Load() => ProtectedStore.Load<AccountsFile>(LauncherPaths.AccountsFile) ?? new AccountsFile();

    public event Action? Changed;

    public void Save(AccountsFile file)
    {
        lock (_fileLock) ProtectedStore.Save(LauncherPaths.AccountsFile, file);
        Changed?.Invoke();
    }

    public GameAccount? Selected
    {
        get
        {
            var f = Load();
            return f.Accounts.FirstOrDefault(a => a.Id == f.SelectedId) ?? f.Accounts.FirstOrDefault();
        }
    }

    public void Select(Guid id)
    {
        var f = Load();
        f.SelectedId = id;
        Save(f);
    }

    public void Remove(Guid id)
    {
        var f = Load();
        f.Accounts.RemoveAll(a => a.Id == id);
        if (f.SelectedId == id) f.SelectedId = f.Accounts.FirstOrDefault()?.Id;
        Save(f);
    }

    // ---------- Оффлайн ----------

    public GameAccount AddOffline(string nick)
    {
        nick = nick.Trim();
        if (!NickRegex().IsMatch(nick))
            throw new GameAuthException(L.T("Ник: 3–16 символов, латиница, цифры и _"));

        var acc = new GameAccount { Type = AccountType.Offline, Name = nick, Uuid = OfflineUuid(nick), AccessToken = "0" };
        Upsert(acc);
        return acc;
    }

    /// <summary>UUID v3 от "OfflinePlayer:{ник}" — так же считает сервер в offline-mode, скины/инвентарь совпадут.</summary>
    public static string OfflineUuid(string nick)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + nick));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // ---------- Microsoft: Device Code Flow ----------

    public async Task<DeviceCodeInfo> BeginMicrosoftLoginAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(ClientId))
            throw new GameAuthException(L.T("Не задан MicrosoftClientId в appsettings.json"));

        using var resp = await Http.PostAsync(MsAuthority + "devicecode", Form(("client_id", ClientId), ("scope", Scope)), ct);
        var j = await ReadJsonAsync(resp, ct);
        return new DeviceCodeInfo(
            j["user_code"]!.GetValue<string>(),
            j["verification_uri"]!.GetValue<string>(),
            j["device_code"]!.GetValue<string>(),
            j["interval"]?.GetValue<int>() ?? 5,
            DateTimeOffset.UtcNow.AddSeconds(j["expires_in"]?.GetValue<int>() ?? 900));
    }

    /// <summary>Ждём, пока пользователь введёт код на microsoft.com/link, затем проходим цепочку Xbox → Minecraft.</summary>
    public async Task<GameAccount> CompleteMicrosoftLoginAsync(DeviceCodeInfo code, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(code.IntervalSeconds);
        while (DateTimeOffset.UtcNow < code.ExpiresAt)
        {
            await Task.Delay(interval, ct);
            using var resp = await Http.PostAsync(MsAuthority + "token", Form(
                ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                ("client_id", ClientId),
                ("device_code", code.DeviceCode)), ct);
            var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;

            switch (j["error"]?.GetValue<string>())
            {
                case null:
                    var acc = await MinecraftFromMsTokenAsync(j["access_token"]!.GetValue<string>(),
                        j["refresh_token"]?.GetValue<string>(), ct);
                    Upsert(acc);
                    return acc;
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case "authorization_declined":
                    throw new GameAuthException(L.T("Вход отклонён"));
                case "expired_token":
                    throw new GameAuthException(L.T("Код истёк, начните вход заново"));
                default:
                    throw new GameAuthException(j["error_description"]?.GetValue<string>() ?? L.T("Ошибка входа Microsoft"));
            }
        }

        throw new GameAuthException(L.T("Код истёк, начните вход заново"));
    }

    /// <summary>Перед запуском: обновить токен Minecraft, если он истекает (ротация refresh-токена Microsoft).</summary>
    public async Task<GameAccount> EnsureFreshAsync(GameAccount acc, CancellationToken ct)
    {
        if (acc.Type != AccountType.Microsoft) return acc;
        if (acc.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5)) return acc;
        if (acc.MsRefreshToken is null) throw new GameAuthException(L.T("Сессия Microsoft истекла — войдите заново"));

        using var resp = await Http.PostAsync(MsAuthority + "token", Form(
            ("grant_type", "refresh_token"),
            ("client_id", ClientId),
            ("refresh_token", acc.MsRefreshToken),
            ("scope", Scope)), ct);
        if (!resp.IsSuccessStatusCode) throw new GameAuthException(L.T("Сессия Microsoft истекла — войдите заново"));
        var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;

        var fresh = await MinecraftFromMsTokenAsync(j["access_token"]!.GetValue<string>(),
            j["refresh_token"]?.GetValue<string>() ?? acc.MsRefreshToken, ct);
        fresh.Id = acc.Id;
        Upsert(fresh);
        return fresh;
    }

    private async Task<GameAccount> MinecraftFromMsTokenAsync(string msToken, string? msRefresh, CancellationToken ct)
    {
        // 1. Xbox Live
        var xbl = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new JsonObject
        {
            ["Properties"] = new JsonObject
            {
                ["AuthMethod"] = "RPS",
                ["SiteName"] = "user.auth.xboxlive.com",
                ["RpsTicket"] = "d=" + msToken,
            },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT",
        }, ct);
        var xblToken = xbl["Token"]!.GetValue<string>();

        // 2. XSTS
        using var xstsResp = await Http.PostAsJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new JsonObject
        {
            ["Properties"] = new JsonObject { ["SandboxId"] = "RETAIL", ["UserTokens"] = new JsonArray(xblToken) },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT",
        }, ct);
        var xsts = JsonNode.Parse(await xstsResp.Content.ReadAsStringAsync(ct))!;
        if (!xstsResp.IsSuccessStatusCode)
            throw new GameAuthException(xsts["XErr"]?.GetValue<long>() switch
            {
                2148916233 => L.T("У аккаунта Microsoft нет профиля Xbox. Создайте его на xbox.com и повторите."),
                2148916235 => L.T("Xbox Live недоступен в вашей стране."),
                2148916236 or 2148916237 => L.T("Нужна верификация возраста на xbox.com."),
                2148916238 => L.T("Детский аккаунт: его нужно добавить в семейную группу взрослым."),
                _ => L.T("Xbox Live отклонил вход"),
            });

        var uhs = xsts["DisplayClaims"]!["xui"]![0]!["uhs"]!.GetValue<string>();
        var xstsToken = xsts["Token"]!.GetValue<string>();

        // 3. Minecraft
        var mc = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
            new JsonObject { ["identityToken"] = $"XBL3.0 x={uhs};{xstsToken}" }, ct);
        var mcToken = mc["access_token"]!.GetValue<string>();
        var expires = DateTimeOffset.UtcNow.AddSeconds(mc["expires_in"]?.GetValue<int>() ?? 86400);

        // 4. Профиль (404 — игра не куплена на этом аккаунте)
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.Authorization = new("Bearer", mcToken);
        using var profResp = await Http.SendAsync(req, ct);
        if (profResp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new GameAuthException(L.T("На этом аккаунте Microsoft не куплена Minecraft: Java Edition."));
        var profile = await ReadJsonAsync(profResp, ct);

        log.LogInformation("Вход Microsoft: {Nick}", profile["name"]!.GetValue<string>());
        return new GameAccount
        {
            Type = AccountType.Microsoft,
            Name = profile["name"]!.GetValue<string>(),
            Uuid = profile["id"]!.GetValue<string>(),
            AccessToken = mcToken,
            AccessTokenExpiresAt = expires,
            MsRefreshToken = msRefresh,
            Xuid = xsts["DisplayClaims"]!["xui"]![0]!["xid"]?.GetValue<string>(),
        };
    }

    private void Upsert(GameAccount acc)
    {
        var f = Load();
        var existing = f.Accounts.FindIndex(a => a.Id == acc.Id || (a.Type == acc.Type && a.Uuid == acc.Uuid));
        if (existing >= 0)
        {
            acc.Id = f.Accounts[existing].Id;
            f.Accounts[existing] = acc;
        }
        else f.Accounts.Add(acc);
        f.SelectedId = acc.Id;
        Save(f);
    }

    private async Task<JsonNode> PostJsonAsync(string url, JsonObject body, CancellationToken ct)
    {
        using var resp = await Http.PostAsJsonAsync(url, body, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden && url.Contains("minecraftservices"))
            throw new GameAuthException(L.T("Minecraft отклонил приложение: Client ID должен быть одобрен Mojang (см. README)."));
        return await ReadJsonAsync(resp, ct);
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new GameAuthException(L.T($"Ошибка авторизации ({(int)resp.StatusCode})"));
        return JsonNode.Parse(text)!;
    }

    private static FormUrlEncodedContent Form(params (string, string)[] values) =>
        new(values.Select(v => new KeyValuePair<string, string>(v.Item1, v.Item2)));

    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$")]
    private static partial Regex NickRegex();
}

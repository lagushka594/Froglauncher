using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FrogLauncher.Core;
using FrogLauncher.Models;

namespace FrogLauncher.Services;

public sealed record DeviceCodeInfo(string UserCode, string VerificationUri, string DeviceCode, int ExpiresIn, int Interval);

/// <summary>Оффлайн-вход и Microsoft OAuth 2.0 Device Code Flow → Xbox Live → XSTS → Minecraft Services.</summary>
public sealed class AuthService
{
    // ---------- Оффлайн ----------
    public Account CreateOffline(string name)
    {
        name = name.Trim();
        if (name.Length is < 3 or > 16 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new InvalidOperationException("Ник: 3–16 символов, латиница, цифры и «_».");
        return new Account { Type = "offline", Username = name, Uuid = OfflineUuid(name), AccessToken = "0" };
    }

    /// <summary>UUID как у ванильных серверов в offline-режиме: MD5("OfflinePlayer:" + ник), версия 3.</summary>
    public static string OfflineUuid(string name)
    {
        var h = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        h[6] = (byte)((h[6] & 0x0F) | 0x30);
        h[8] = (byte)((h[8] & 0x3F) | 0x80);
        return Convert.ToHexString(h).ToLowerInvariant();
    }

    // ---------- Microsoft ----------
    public async Task<DeviceCodeInfo> StartDeviceFlowAsync(string clientId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Не задан Microsoft Client ID (Настройки). См. README — регистрация приложения в Azure бесплатна.");
        var json = await PostFormAsync(ApiConfig.MsDeviceCode, new()
        {
            ["client_id"] = clientId, ["scope"] = ApiConfig.MsScope
        }, ct);
        return new DeviceCodeInfo((string)json["user_code"]!, (string)json["verification_uri"]!, (string)json["device_code"]!,
            (int)json["expires_in"]!, (int?)json["interval"] ?? 5);
    }

    public async Task<Account> CompleteDeviceFlowAsync(string clientId, DeviceCodeInfo dc, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(dc.ExpiresIn);
        int interval = Math.Max(dc.Interval, 2);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var (ok, json) = await TryPostFormAsync(ApiConfig.MsToken, new()
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = clientId, ["device_code"] = dc.DeviceCode
            }, ct);
            if (ok) return await XboxChainAsync((string)json["access_token"]!, (string)json["refresh_token"]!, ct);

            var err = (string?)json["error"];
            if (err == "authorization_pending") continue;
            if (err == "slow_down") { interval += 5; continue; }
            throw new InvalidOperationException("Вход Microsoft отклонён: " + ((string?)json["error_description"] ?? err));
        }
        throw new TimeoutException("Время ожидания входа истекло. Повторите попытку.");
    }

    public async Task<Account> RefreshAsync(Account acc, string clientId, CancellationToken ct)
    {
        var json = await PostFormAsync(ApiConfig.MsToken, new()
        {
            ["grant_type"] = "refresh_token", ["client_id"] = clientId,
            ["refresh_token"] = acc.MsRefreshToken, ["scope"] = ApiConfig.MsScope
        }, ct);
        return await XboxChainAsync((string)json["access_token"]!, (string?)json["refresh_token"] ?? acc.MsRefreshToken, ct);
    }

    private async Task<Account> XboxChainAsync(string msToken, string msRefresh, CancellationToken ct)
    {
        // 1. Xbox Live
        var xbl = await PostJsonAsync(ApiConfig.XblAuth, new JsonObject
        {
            ["Properties"] = new JsonObject { ["AuthMethod"] = "RPS", ["SiteName"] = "user.auth.xboxlive.com", ["RpsTicket"] = "d=" + msToken },
            ["RelyingParty"] = "http://auth.xboxlive.com", ["TokenType"] = "JWT"
        }, null, ct);
        var xblToken = (string)xbl["Token"]!;
        var uhs = (string)xbl["DisplayClaims"]!["xui"]![0]!["uhs"]!;

        // 2. XSTS
        JsonObject xsts;
        try
        {
            xsts = await PostJsonAsync(ApiConfig.XstsAuth, new JsonObject
            {
                ["Properties"] = new JsonObject { ["SandboxId"] = "RETAIL", ["UserTokens"] = new JsonArray(xblToken) },
                ["RelyingParty"] = "rp://api.minecraftservices.com/", ["TokenType"] = "JWT"
            }, null, ct);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("2148916233"))
        { throw new InvalidOperationException("У этой учётной записи Microsoft нет профиля Xbox. Создайте его на xbox.com."); }
        catch (HttpRequestException ex) when (ex.Message.Contains("2148916238"))
        { throw new InvalidOperationException("Детский аккаунт: добавьте его в семейную группу Microsoft."); }
        var xstsToken = (string)xsts["Token"]!;

        // 3. Minecraft
        var mc = await PostJsonAsync(ApiConfig.McLogin, new JsonObject { ["identityToken"] = $"XBL3.0 x={uhs};{xstsToken}" }, null, ct);
        var mcToken = (string)mc["access_token"]!;
        int expires = (int?)mc["expires_in"] ?? 86400;

        // 4. Профиль (проверка лицензии)
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiConfig.McProfile);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);
        using var resp = await Http.Client.SendAsync(req, ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidOperationException("На этом аккаунте нет лицензии Minecraft: Java Edition.");
        resp.EnsureSuccessStatusCode();
        var prof = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;

        return new Account
        {
            Type = "microsoft", Username = (string)prof["name"]!, Uuid = (string)prof["id"]!,
            AccessToken = mcToken, MsRefreshToken = msRefresh, ExpiresAt = DateTime.UtcNow.AddSeconds(expires - 120)
        };
    }

    // ---------- HTTP-помощники ----------
    private static async Task<JsonObject> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        var (ok, json) = await TryPostFormAsync(url, form, ct);
        if (!ok) throw new InvalidOperationException((string?)json["error_description"] ?? (string?)json["error"] ?? "Ошибка авторизации");
        return json;
    }

    private static async Task<(bool ok, JsonObject json)> TryPostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await Http.Client.PostAsync(url, new FormUrlEncodedContent(form), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        var json = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        return (resp.IsSuccessStatusCode, json);
    }

    private static async Task<JsonObject> PostJsonAsync(string url, JsonObject body, string? bearer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var resp = await Http.Client.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }
}

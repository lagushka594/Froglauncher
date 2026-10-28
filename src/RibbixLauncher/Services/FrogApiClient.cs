using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using RibbixLauncher.Core;
using Ribbix.Shared;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.Services;

public sealed class ApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>Сессия аккаунта RibbixLauncher (session.dat, DPAPI).</summary>
public sealed class LauncherSession
{
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset AccessExpiresAt { get; set; }
    public string Nick { get; set; } = "";
}

/// <summary>
/// Хранит сессию и обновляет access-токен. Refresh-токен одноразовый (ротация на сервере):
/// обновление идёт строго в один поток, иначе второй запрос получит «повторное использование»
/// и сервер отзовёт всю цепочку.
/// </summary>
public sealed class SessionStore(IHttpClientFactory httpFactory, ILogger<SessionStore> log)
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private LauncherSession? _session = ProtectedStore.Load<LauncherSession>(LauncherPaths.SessionFile);

    public LauncherSession? Current => _session;
    public bool IsLoggedIn => _session is not null;

    /// <summary>Сессия кончилась (refresh отклонён, например после отзыва Admin) — нужен повторный вход.</summary>
    public event Action? SessionExpired;
    public event Action? SessionChanged;

    public void Set(TokenPair pair)
    {
        _session = new LauncherSession
        {
            AccessToken = pair.AccessToken,
            RefreshToken = pair.RefreshToken,
            AccessExpiresAt = DateTimeOffset.UtcNow.AddSeconds(pair.ExpiresIn - 30),
            Nick = JwtClaims.Read(pair.AccessToken).Nick,
        };
        ProtectedStore.Save(LauncherPaths.SessionFile, _session);
        SessionChanged?.Invoke();
    }

    public void Clear(bool expired = false)
    {
        _session = null;
        ProtectedStore.Delete(LauncherPaths.SessionFile);
        SessionChanged?.Invoke();
        if (expired) SessionExpired?.Invoke();
    }

    public async Task<string?> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        var s = _session;
        if (s is null) return null;
        if (!forceRefresh && s.AccessExpiresAt > DateTimeOffset.UtcNow) return s.AccessToken;

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Пока ждали — другой поток мог уже обновить.
            if (_session is null) return null;
            if (!ReferenceEquals(_session, s) && _session.AccessExpiresAt > DateTimeOffset.UtcNow) return _session.AccessToken;

            var http = httpFactory.CreateClient(RibbixApiClient.RawClientName);
            using var resp = await http.PostAsJsonAsync("auth/refresh", new RefreshRequest(_session.RefreshToken), RibbixJson.Options, ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                log.LogInformation("Refresh-токен отклонён — нужен повторный вход");
                Clear(expired: true);
                return null;
            }

            resp.EnsureSuccessStatusCode();
            var pair = await resp.Content.ReadFromJsonAsync<TokenPair>(RibbixJson.Options, ct)
                       ?? throw new ApiException(L.T("Пустой ответ при обновлении сессии"));
            Set(pair);
            return pair.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}

/// <summary>Подставляет Bearer и при 401 один раз обновляет токен и повторяет запрос.</summary>
public sealed class AuthHandler(SessionStore session) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await session.GetAccessTokenAsync(false, ct);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await base.SendAsync(request, ct);
        if (resp.StatusCode != HttpStatusCode.Unauthorized || token is null) return resp;

        // Токен могли отозвать (TokenVersion) — пробуем refresh; если и он отозван — SessionExpired.
        var fresh = await session.GetAccessTokenAsync(true, ct);
        if (fresh is null) return resp;

        resp.Dispose();
        var retry = await CloneAsync(request);
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
        return await base.SendAsync(retry, ct);
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage r)
    {
        var clone = new HttpRequestMessage(r.Method, r.RequestUri);
        if (r.Content is not null)
        {
            var bytes = await r.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(bytes);
            foreach (var h in r.Content.Headers) clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        foreach (var h in r.Headers) clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        return clone;
    }
}

/// <summary>Типизированный клиент RibbixLauncher API.</summary>
public sealed class RibbixApiClient(IHttpClientFactory httpFactory, SessionStore session)
{
    public const string ClientName = "frog-api";
    public const string RawClientName = "frog-api-raw";

    private HttpClient Http => httpFactory.CreateClient(ClientName);

    public async Task LoginAsync(string login, string password, CancellationToken ct)
    {
        using var resp = await httpFactory.CreateClient(RawClientName)
            .PostAsJsonAsync("auth/login", new LoginRequest(login, password), RibbixJson.Options, ct);
        session.Set(await ReadAsync<TokenPair>(resp, ct));
    }

    public async Task RegisterAsync(string nick, string email, string password, CancellationToken ct)
    {
        using var resp = await httpFactory.CreateClient(RawClientName)
            .PostAsJsonAsync("auth/register", new RegisterRequest(nick, email, password), RibbixJson.Options, ct);
        session.Set(await ReadAsync<TokenPair>(resp, ct));
    }

    public async Task LogoutAsync(CancellationToken ct)
    {
        if (session.Current is { } s)
        {
            try
            {
                await httpFactory.CreateClient(RawClientName)
                    .PostAsJsonAsync("auth/logout", new RefreshRequest(s.RefreshToken), RibbixJson.Options, ct);
            }
            catch (HttpRequestException) { /* офлайн — просто забываем сессию */ }
        }
        session.Clear();
    }

    public async Task<IReadOnlyList<TierDto>> GetTiersAsync(CancellationToken ct)
    {
        using var resp = await httpFactory.CreateClient(RawClientName).GetAsync("tiers", ct);
        return (await ReadAsync<TiersResponse>(resp, ct)).Tiers;
    }

    public async Task<EntitlementsDto> GetEntitlementsAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync("entitlements", ct);
        return await ReadAsync<EntitlementsDto>(resp, ct);
    }

    public async Task<CheckoutResponse> CheckoutAsync(string tier, BillingPeriod period, string? promoCode, CancellationToken ct)
    {
        using var resp = await Http.PostAsJsonAsync("billing/checkout", new CheckoutRequest(tier, period, PromoCode: promoCode), RibbixJson.Options, ct);
        return await ReadAsync<CheckoutResponse>(resp, ct);
    }

    public async Task<CancelResponse> CancelAsync(CancellationToken ct)
    {
        using var resp = await Http.PostAsync("billing/cancel", null, ct);
        return await ReadAsync<CancelResponse>(resp, ct);
    }

    public async Task<IReadOnlyList<PaymentHistoryItem>> HistoryAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync("billing/history", ct);
        return await ReadAsync<List<PaymentHistoryItem>>(resp, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<T>(RibbixJson.Options, ct) ?? throw new ApiException(L.T("Пустой ответ сервера"));

        string? detail = null;
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            detail = doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
        }
        catch (JsonException) { }

        throw new ApiException(detail ?? resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => L.T("Нужно войти в аккаунт Ribbix"),
            HttpStatusCode.TooManyRequests => L.T("Слишком много попыток, подождите минуту"),
            _ => L.T($"Ошибка сервера ({(int)resp.StatusCode})"),
        }, resp.StatusCode);
    }
}

/// <summary>Чтение claims из JWT без проверки подписи (проверяет сервер; клиенту нужно только отображение).</summary>
public static class JwtClaims
{
    public sealed record Claims(string Nick, string Tier, IReadOnlyList<string> Entitlements, DateTimeOffset? Expires);

    public static Claims Read(string jwt)
    {
        try
        {
            var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var root = doc.RootElement;
            var ents = root.TryGetProperty("entitlements", out var e)
                ? e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string> { e.GetString()! }
                : new List<string>();
            return new Claims(
                root.TryGetProperty("nick", out var n) ? n.GetString() ?? "" : "",
                root.TryGetProperty("tier", out var t) ? t.GetString() ?? TierSlugs.Tadpole : TierSlugs.Tadpole,
                ents,
                root.TryGetProperty("exp", out var exp) ? DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) : null);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or IndexOutOfRangeException)
        {
            return new Claims("", TierSlugs.Tadpole, Array.Empty<string>(), null);
        }
    }
}

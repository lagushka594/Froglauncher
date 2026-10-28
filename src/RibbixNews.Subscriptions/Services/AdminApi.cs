using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Ribbix.Shared;
using Microsoft.Extensions.DependencyInjection;
using Polly;

namespace RibbixNews.Subscriptions.Services;

public sealed class AdminApiException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>
/// Service-to-service токен RibbixNews (audience=ribbixnews). Живёт только в памяти процесса RibbixNews:
/// сотрудник входит при старте, refresh-токен ротируется, на диск ничего не пишется.
/// </summary>
public sealed class ServiceTokenProvider(IHttpClientFactory httpFactory)
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private TokenPair? _pair;
    private DateTimeOffset _expiresAt;

    public string? Login { get; private set; }
    public string? Role { get; private set; }

    /// <summary>Роль берётся из подписанного сервером токена, а не из локальных данных RibbixNews.</summary>
    public bool IsSuperAdmin => Role == SubscriptionsMenu.RequiredRole;

    public event Action? Changed;

    public async Task LoginAsync(string login, string password, CancellationToken ct)
    {
        using var resp = await httpFactory.CreateClient(AdminApiClient.RawClientName)
            .PostAsJsonAsync("auth/ribbixnews/login", new StaffLoginRequest(login, password), RibbixJson.Options, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new AdminApiException("Неверный логин или пароль", resp.StatusCode);
        resp.EnsureSuccessStatusCode();
        Apply(await resp.Content.ReadFromJsonAsync<TokenPair>(RibbixJson.Options, ct)
              ?? throw new AdminApiException("Пустой ответ сервера"));
    }

    /// <summary>Вход без сервера (локальная учётка RibbixNews): роль есть, service-токена нет — API подписок недоступно.</summary>
    public bool IsLocalOnly { get; private set; }

    public void LoginLocal(string login, string role)
    {
        _pair = null;
        IsLocalOnly = true;
        Login = login;
        Role = role;
        Changed?.Invoke();
    }

    public void Logout()
    {
        IsLocalOnly = false;
        _pair = null;
        Login = Role = null;
        Changed?.Invoke();
    }

    public async Task<string?> GetTokenAsync(bool force, CancellationToken ct)
    {
        if (_pair is null) return null;
        if (!force && _expiresAt > DateTimeOffset.UtcNow) return _pair.AccessToken;

        await _lock.WaitAsync(ct);
        try
        {
            if (_pair is null) return null;
            if (!force && _expiresAt > DateTimeOffset.UtcNow) return _pair.AccessToken;

            using var resp = await httpFactory.CreateClient(AdminApiClient.RawClientName)
                .PostAsJsonAsync("auth/ribbixnews/refresh", new RefreshRequest(_pair.RefreshToken), RibbixJson.Options, ct);
            if (!resp.IsSuccessStatusCode)
            {
                Logout();
                return null;
            }
            Apply((await resp.Content.ReadFromJsonAsync<TokenPair>(RibbixJson.Options, ct))!);
            return _pair!.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void Apply(TokenPair pair)
    {
        _pair = pair;
        _expiresAt = DateTimeOffset.UtcNow.AddSeconds(pair.ExpiresIn - 30);
        (Login, Role) = ReadClaims(pair.AccessToken);
        Changed?.Invoke();
    }

    private static (string?, string?) ReadClaims(string jwt)
    {
        var p = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        p = p.PadRight(p.Length + (4 - p.Length % 4) % 4, '=');
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(p)));
        var r = doc.RootElement;
        return (r.TryGetProperty("name", out var n) ? n.GetString() : null,
                r.TryGetProperty("role", out var role) ? role.GetString() : null);
    }
}

internal sealed class ServiceAuthHandler(ServiceTokenProvider tokens) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await tokens.GetTokenAsync(false, ct)
                    ?? throw new AdminApiException(tokens.IsLocalOnly
                        ? "Сервер подписок недоступен — вы вошли локально. Подписки сейчас выдаются ролями в Discord."
                        : "Войдите в Ribbix News заново", HttpStatusCode.Unauthorized);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, ct);
    }
}

/// <summary>Клиент /admin/* backend'а.</summary>
public sealed class AdminApiClient(IHttpClientFactory httpFactory)
{
    public const string ClientName = "ribbixnews-admin";
    public const string RawClientName = "ribbixnews-admin-raw";

    private HttpClient Http => httpFactory.CreateClient(ClientName);

    public async Task<PagedResult<AdminUserDto>> UsersAsync(string? search, int page, int pageSize, CancellationToken ct)
    {
        var url = $"admin/users?page={page}&page_size={pageSize}" +
                  (string.IsNullOrWhiteSpace(search) ? "" : "&search=" + Uri.EscapeDataString(search.Trim()));
        using var resp = await Http.GetAsync(url, ct);
        return await ReadAsync<PagedResult<AdminUserDto>>(resp, ct);
    }

    public async Task<AdminGrantDto> GrantAsync(Guid userId, string reason, CancellationToken ct)
    {
        using var resp = await Http.PostAsJsonAsync("admin/grant", new GrantRequest(userId, TierSlugs.Admin, reason), RibbixJson.Options, ct);
        return await ReadAsync<AdminGrantDto>(resp, ct);
    }

    public async Task<AdminGrantDto> RevokeAsync(Guid grantId, string reason, CancellationToken ct)
    {
        using var resp = await Http.PostAsJsonAsync("admin/revoke", new RevokeRequest(grantId, reason), RibbixJson.Options, ct);
        return await ReadAsync<AdminGrantDto>(resp, ct);
    }

    /// <summary>Выдать платный тариф (оплата через тикет в Discord). Повторная выдача продлевает срок.</summary>
    public async Task<SubscriptionGrantDto> GrantSubscriptionAsync(Guid userId, string tier, BillingPeriod period, string reason, CancellationToken ct)
    {
        using var resp = await Http.PostAsJsonAsync("admin/subscriptions/grant",
            new GrantSubscriptionRequest(userId, tier, period, reason), RibbixJson.Options, ct);
        return await ReadAsync<SubscriptionGrantDto>(resp, ct);
    }

    public async Task<SubscriptionGrantDto> RevokeSubscriptionAsync(Guid userId, string reason, CancellationToken ct)
    {
        using var resp = await Http.PostAsJsonAsync("admin/subscriptions/revoke", new RevokeSubscriptionRequest(userId, reason), RibbixJson.Options, ct);
        return await ReadAsync<SubscriptionGrantDto>(resp, ct);
    }

    /// <summary>Публичный список тарифов (для выбора в диалоге выдачи).</summary>
    public async Task<IReadOnlyList<TierDto>> TiersAsync(CancellationToken ct)
    {
        using var resp = await httpFactory.CreateClient(RawClientName).GetAsync("tiers", ct);
        return (await ReadAsync<TiersResponse>(resp, ct)).Tiers;
    }

    public async Task<IReadOnlyList<AdminGrantDto>> GrantsAsync(Guid? userId, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(userId is null ? "admin/grants" : $"admin/grants?user_id={userId}", ct);
        return await ReadAsync<List<AdminGrantDto>>(resp, ct);
    }

    public async Task<byte[]> ExportCsvAsync(CancellationToken ct)
    {
        using var resp = await Http.GetAsync("admin/grants/export", ct);
        if (!resp.IsSuccessStatusCode) await ReadAsync<object>(resp, ct);
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode)
            return (await resp.Content.ReadFromJsonAsync<T>(RibbixJson.Options, ct))!;

        string? detail = null;
        try
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            detail = doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
        }
        catch (JsonException) { }

        throw new AdminApiException(detail ?? resp.StatusCode switch
        {
            HttpStatusCode.Forbidden => "Нет доступа: раздел только для роли superadmin",
            HttpStatusCode.Unauthorized => "Сессия истекла — войдите заново",
            _ => $"Ошибка сервера ({(int)resp.StatusCode})",
        }, resp.StatusCode);
    }
}

/// <summary>Правило видимости пункта меню «Подписки».</summary>
public static class SubscriptionsMenu
{
    /// <summary>Только superadmin. Роль «админ новостей» (news_admin) доступа НЕ имеет.</summary>
    public const string RequiredRole = "superadmin";

    public static bool IsVisible(ServiceTokenProvider tokens) => tokens.IsSuperAdmin;
}

public static class ServiceCollectionExtensions
{
    /// <summary>Регистрация модуля в DI существующего RibbixNews.</summary>
    public static IServiceCollection AddFrogNewsSubscriptions(this IServiceCollection s, Uri apiBase)
    {
        // 3 повтора, экспоненциальная задержка 2/4/8 с.
        static IAsyncPolicy<HttpResponseMessage> Retry(PolicyBuilder<HttpResponseMessage> p) =>
            p.WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(Math.Pow(2, i)));

        s.AddHttpClient(AdminApiClient.RawClientName, c => c.BaseAddress = apiBase).AddTransientHttpErrorPolicy(Retry);
        s.AddTransient<ServiceAuthHandler>();
        s.AddHttpClient(AdminApiClient.ClientName, c => c.BaseAddress = apiBase)
            .AddHttpMessageHandler<ServiceAuthHandler>()
            .AddTransientHttpErrorPolicy(Retry);

        s.AddSingleton<ServiceTokenProvider>();
        s.AddSingleton<AdminApiClient>();
        s.AddSingleton<IAdminDialogs, WpfAdminDialogs>();
        s.AddTransient<ViewModels.SubscriptionsViewModel>();
        return s;
    }
}

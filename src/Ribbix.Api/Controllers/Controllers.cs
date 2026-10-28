using System.Security.Claims;
using Ribbix.Api.Infrastructure;
using Ribbix.Api.Payments;
using Ribbix.Api.Services;
using Ribbix.Shared;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Ribbix.Api.Controllers;

[ApiController]
[Route("auth")]
[EnableRateLimiting(RateLimits.Auth)]
public sealed class AuthController(AccountService accounts) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<TokenPair>> Register(RegisterRequest req, CancellationToken ct) =>
        await accounts.RegisterAsync(req, ct);

    [HttpPost("login")]
    public async Task<ActionResult<TokenPair>> Login(LoginRequest req, CancellationToken ct) =>
        await accounts.LoginAsync(req, ct) is { } pair ? pair : Unauthorized(Problem401("Неверный логин или пароль"));

    /// <summary>Ротация: старый refresh-токен становится недействительным.</summary>
    [HttpPost("refresh")]
    public async Task<ActionResult<TokenPair>> Refresh(RefreshRequest req, CancellationToken ct) =>
        await accounts.RefreshAsync(req.RefreshToken, ct) is { } pair ? pair : Unauthorized(Problem401("Сессия истекла, войдите заново"));

    [HttpPost("logout")]
    public async Task<IActionResult> Logout(RefreshRequest req, [FromServices] JwtService jwt, CancellationToken ct)
    {
        await jwt.RevokeAsync(req.RefreshToken, ct);
        return NoContent();
    }

    /// <summary>Вход сотрудника RibbixNews → service-токен с audience=ribbixnews.</summary>
    [HttpPost("ribbixnews/login")]
    public async Task<ActionResult<TokenPair>> StaffLogin(StaffLoginRequest req, CancellationToken ct) =>
        await accounts.StaffLoginAsync(req, ct) is { } pair ? pair : Unauthorized(Problem401("Неверный логин или пароль"));

    [HttpPost("ribbixnews/refresh")]
    public async Task<ActionResult<TokenPair>> StaffRefresh(RefreshRequest req, CancellationToken ct) =>
        await accounts.StaffRefreshAsync(req.RefreshToken, ct) is { } pair ? pair : Unauthorized(Problem401("Сессия истекла"));

    private static ProblemDetails Problem401(string detail) => new() { Status = 401, Title = "Unauthorized", Detail = detail };
}

[ApiController]
[Route("tiers")]
public sealed class TiersController(TierCatalog tiers) : ControllerBase
{
    /// <summary>Публичный список тиров. Admin (hidden) сюда не попадает.</summary>
    [HttpGet, AllowAnonymous, ResponseCache(Duration = 300)]
    public TiersResponse Get() => new(tiers.Public);
}

[ApiController]
[Route("entitlements")]
[Authorize(AuthenticationSchemes = AuthSchemes.Launcher)]
public sealed class EntitlementsController(EntitlementService entitlements) : ControllerBase
{
    [HttpGet]
    public Task<EntitlementsDto> Get(CancellationToken ct) => entitlements.RefreshCacheAsync(User.UserId(), ct);
}

[ApiController]
[Route("billing")]
public sealed class BillingController(BillingService billing, ILogger<BillingController> log) : ControllerBase
{
    [HttpPost("checkout"), Authorize(AuthenticationSchemes = AuthSchemes.Launcher)]
    public Task<CheckoutResponse> Checkout(CheckoutRequest req, CancellationToken ct) =>
        billing.CheckoutAsync(User.UserId(), req, ct);

    [HttpPost("cancel"), Authorize(AuthenticationSchemes = AuthSchemes.Launcher)]
    public Task<CancelResponse> Cancel(CancellationToken ct) => billing.CancelAsync(User.UserId(), ct);

    [HttpGet("history"), Authorize(AuthenticationSchemes = AuthSchemes.Launcher)]
    public Task<IReadOnlyList<PaymentHistoryItem>> History(CancellationToken ct) => billing.HistoryAsync(User.UserId(), ct);

    /// <summary>
    /// Вебхук провайдера: /billing/webhook/yookassa?token=... или /billing/webhook/paddle.
    /// Тело читается как есть — подпись считается по сырым байтам.
    /// </summary>
    [HttpPost("webhook/{provider}"), AllowAnonymous, DisableRateLimiting]
    public async Task<IActionResult> Webhook(string provider, [FromQuery] string? token, CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        var headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var req = new WebhookRequest(body, headers, HttpContext.Connection.RemoteIpAddress, token);

        try
        {
            return await billing.HandleWebhookAsync(provider, req, ct) ? Ok() : Unauthorized();
        }
        catch (PaymentProviderException ex)
        {
            // 5xx → провайдер повторит доставку позже.
            log.LogError(ex, "Ошибка обработки вебхука {Provider}", provider);
            return StatusCode(503);
        }
    }

    /// <summary>
    /// Страница возврата после оплаты. Платёжки принимают только http(s) return_url,
    /// поэтому отдаём HTML, который открывает лаунчер по ribbix://.
    /// </summary>
    [HttpGet("return"), AllowAnonymous]
    public ContentResult Return([FromQuery(Name = "sub_id")] Guid subId)
    {
        var deeplink = $"ribbix://billing/success?sub_id={subId}";
        var html = $$"""
            <!doctype html><html lang="ru"><head><meta charset="utf-8"><title>Ribbix</title>
            <meta http-equiv="refresh" content="0;url={{deeplink}}">
            <style>body{background:#0b1220;color:#d1fae5;font-family:Segoe UI,sans-serif;display:grid;place-items:center;height:100vh;margin:0}
            a{color:#22c55e}</style></head>
            <body><div style="text-align:center"><div style="font-size:64px">🐸</div>
            <h2>Оплата принята, возвращаемся в Ribbix…</h2>
            <p>Если лаунчер не открылся — <a href="{{deeplink}}">нажмите сюда</a>. Окно можно закрыть.</p></div></body></html>
            """;
        return Content(html, "text/html; charset=utf-8");
    }
}

/// <summary>Эндпоинты RibbixNews. Только service-токен (audience=ribbixnews) с ролью superadmin.</summary>
[ApiController]
[Route("admin")]
[Authorize(Policy = Policies.RibbixNewsSuperAdmin)]
public sealed class AdminController(AdminService admin) : ControllerBase
{
    private string Actor => User.FindFirstValue("name") ?? User.FindFirstValue("sub") ?? "unknown";

    [HttpGet("users")]
    public Task<PagedResult<AdminUserDto>> Users([FromQuery] string? search, [FromQuery] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 25, CancellationToken ct = default) =>
        admin.SearchUsersAsync(search, page, pageSize, ct);

    [HttpPost("grant")]
    public Task<AdminGrantDto> Grant(GrantRequest req, CancellationToken ct) => admin.GrantAsync(req, Actor, ct);

    [HttpPost("revoke")]
    public Task<AdminGrantDto> Revoke(RevokeRequest req, CancellationToken ct) => admin.RevokeAsync(req, Actor, ct);

    /// <summary>Выдать платный тариф после оплаты через тикет в Discord.</summary>
    [HttpPost("subscriptions/grant")]
    public Task<SubscriptionGrantDto> GrantSubscription(GrantSubscriptionRequest req, CancellationToken ct) =>
        admin.GrantSubscriptionAsync(req, Actor, ct);

    [HttpPost("subscriptions/revoke")]
    public Task<SubscriptionGrantDto> RevokeSubscription(RevokeSubscriptionRequest req, CancellationToken ct) =>
        admin.RevokeSubscriptionAsync(req, Actor, ct);

    [HttpGet("grants")]
    public Task<IReadOnlyList<AdminGrantDto>> Grants([FromQuery(Name = "user_id")] Guid? userId, CancellationToken ct) =>
        admin.GrantsAsync(userId, ct);

    /// <summary>Сколько оплат прошло по каждому промокоду (статистика партнёров).</summary>
    [HttpGet("promo-stats")]
    public Task<IReadOnlyList<PromoStatDto>> PromoStats(CancellationToken ct) => admin.PromoStatsAsync(ct);

    [HttpGet("grants/export")]
    public async Task<FileContentResult> Export(CancellationToken ct) =>
        File(await admin.ExportCsvAsync(ct), "text/csv; charset=utf-8", $"admin_audit_{DateTime.UtcNow:yyyyMMdd_HHmm}.csv");
}

/// <summary>
/// «Играть с другом»: Discord и мессенджеры не делают ribbix:// кликабельным, поэтому лаунчер отдаёт
/// https-ссылку на эту страницу — она сразу открывает RibbixLauncher с тем же запросом. Данные из запроса только
/// экранируются и передаются дальше; сервер ничего не хранит.
/// </summary>
[Route("play")]
public sealed class PlayController : ControllerBase
{
    [HttpGet, AllowAnonymous]
    public ContentResult Get()
    {
        var query = Request.QueryString.Value ?? "";
        if (query.Length > 4096) query = "";
        var deep = System.Text.Encodings.Web.HtmlEncoder.Default.Encode("ribbix://play" + query);
        var name = System.Text.Encodings.Web.HtmlEncoder.Default.Encode(Request.Query["n"].ToString() is { Length: > 0 } n ? n : "Minecraft");
        var html = $$"""
            <!doctype html><html lang="ru"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Играть с другом — Ribbix</title>
            <style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0b1220;color:#e5edf7;font:16px system-ui,sans-serif}
            main{max-width:440px;padding:32px;text-align:center}a.b{display:inline-block;margin-top:18px;padding:14px 26px;border-radius:12px;background:#22c55e;color:#04120a;font-weight:700;text-decoration:none}
            p{color:#93a3b8}</style></head><body><main><div style="font-size:56px">🐸</div>
            <h1>Друг зовёт играть</h1><p>«{{name}}» — лаунчер сам поставит ту же версию Minecraft с модами и подключится к серверу.</p>
            <a class="b" href="{{deep}}">Открыть в Ribbix</a>
            <p style="font-size:13px;margin-top:22px">Нет лаунчера? Установите Ribbix и откройте эту ссылку ещё раз.</p></main>
            <script>setTimeout(function(){location.href=document.querySelector('a.b').href},300)</script></body></html>
            """;
        return Content(html, "text/html; charset=utf-8");
    }
}

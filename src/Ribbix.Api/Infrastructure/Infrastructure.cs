using System.Security.Claims;
using Ribbix.Api.Payments;
using Ribbix.Api.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Ribbix.Api.Infrastructure;

public static class AuthSchemes
{
    /// <summary>Токены лаунчера (aud=ribbix).</summary>
    public const string Launcher = "Launcher";
    /// <summary>Service-to-service токены RibbixNews (aud=ribbixnews, отдельный ключ).</summary>
    public const string RibbixNews = "RibbixNews";
}

public static class Policies
{
    public const string RibbixNewsSuperAdmin = "RibbixNewsSuperAdmin";
    /// <summary>Именно superadmin. Роль news_admin (админ новостей) доступа к подпискам не имеет.</summary>
    public const string SuperAdminRole = "superadmin";
}

public static class RateLimits
{
    public const string Auth = "auth";
}

public static class ClaimsExtensions
{
    public static Guid UserId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue("sub"), out var id)
            ? id
            : throw new UnauthorizedAccessException("В токене нет sub");
}

/// <summary>Единый формат ошибок (ProblemDetails) без утечки стектрейсов наружу.</summary>
public sealed class ErrorHandler(ILogger<ErrorHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var (status, detail) = ex switch
        {
            BillingException b => (b.StatusCode, b.Message),
            PaymentProviderException p => (502, "Платёжная система временно недоступна. Попробуйте позже."),
            UnauthorizedAccessException => (401, "Требуется вход"),
            _ => (500, "Внутренняя ошибка сервера"),
        };

        if (status >= 500) log.LogError(ex, "Необработанная ошибка {Path}", ctx.Request.Path);

        ctx.Response.StatusCode = status;
        await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = status, Detail = detail }, ct);
        return true;
    }
}

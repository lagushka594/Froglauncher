using System.Threading.RateLimiting;
using Ribbix.Api.Data;
using Ribbix.Api.Infrastructure;
using Ribbix.Api.Options;
using Ribbix.Api.Payments;
using Ribbix.Api.Services;
using Ribbix.Shared;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Polly;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog((ctx, cfg) => cfg.ReadFrom.Configuration(ctx.Configuration).WriteTo.Console());

    var cfg = builder.Configuration;
    builder.Services.Configure<JwtOptions>(cfg.GetSection(JwtOptions.Section));
    builder.Services.Configure<BillingOptions>(cfg.GetSection(BillingOptions.Section));
    builder.Services.Configure<YooKassaOptions>(cfg.GetSection(YooKassaOptions.Section));
    builder.Services.Configure<PaddleOptions>(cfg.GetSection(PaddleOptions.Section));

    var connectionString = cfg.GetConnectionString("Ribbix") ?? throw new InvalidOperationException("Нет ConnectionStrings:Ribbix");
    builder.Services.AddDbContext<RibbixDbContext>(o =>
    {
        // "InMemory" — только для локальной разработки без PostgreSQL (данные живут до перезапуска).
        if (connectionString == "InMemory" && builder.Environment.IsDevelopment()) o.UseInMemoryDatabase("frog-dev");
        else o.UseNpgsql(connectionString);
        o.UseSnakeCaseNamingConvention();
    });

    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton(TierCatalog.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "tiers.json")));
    // Тот же promos.json, что читает лаунчер: акции и промокоды; нет файла — скидок нет.
    var promosFile = Path.Combine(AppContext.BaseDirectory, "promos.json");
    builder.Services.AddSingleton(File.Exists(promosFile) ? PromoCatalog.Parse(File.ReadAllText(promosFile)) : new PromoCatalog(new PromoFile()));
    builder.Services.AddScoped<JwtService>();
    builder.Services.AddScoped<EntitlementService>();
    builder.Services.AddScoped<AccountService>();
    builder.Services.AddScoped<BillingService>();
    builder.Services.AddScoped<AdminService>();
    builder.Services.AddScoped<PaymentProviderRegistry>();
    builder.Services.AddHostedService<SubscriptionLifecycleWorker>();

    // HTTP к платёжкам: 3 повтора с экспоненциальной задержкой (2, 4, 8 с).
    builder.Services.AddHttpClient<YooKassaProvider>().AddTransientHttpErrorPolicy(RetryPolicy);
    builder.Services.AddHttpClient<PaddleProvider>().AddTransientHttpErrorPolicy(RetryPolicy);
    builder.Services.AddScoped<IPaymentProvider>(sp => sp.GetRequiredService<YooKassaProvider>());
    builder.Services.AddScoped<IPaymentProvider>(sp => sp.GetRequiredService<PaddleProvider>());
    builder.Services.AddScoped<IPaymentProvider, ManualPaymentProvider>();

    var jwt = cfg.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
    builder.Services.AddAuthentication()
        .AddJwtBearer(AuthSchemes.Launcher, o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = Validation(jwt.Issuer, Audiences.Launcher, jwt.SigningKey);
            o.Events = new JwtBearerEvents
            {
                // Отзыв Admin/принудительный выход: версия токена должна совпадать с БД.
                OnTokenValidated = async ctx =>
                {
                    var db = ctx.HttpContext.RequestServices.GetRequiredService<RibbixDbContext>();
                    var sub = ctx.Principal?.FindFirst("sub")?.Value;
                    var tv = ctx.Principal?.FindFirst(RibbixClaims.TokenVersion)?.Value;
                    var ok = Guid.TryParse(sub, out var id) && int.TryParse(tv, out var version) &&
                             await db.Users.AnyAsync(u => u.Id == id && u.TokenVersion == version);
                    if (!ok) ctx.Fail("Токен отозван");
                },
            };
        })
        .AddJwtBearer(AuthSchemes.RibbixNews, o =>
        {
            o.MapInboundClaims = false;
            o.TokenValidationParameters = Validation(jwt.Issuer, Audiences.RibbixNews, jwt.ServiceSigningKey);
        });

    builder.Services.AddAuthorizationBuilder()
        .AddPolicy(Policies.RibbixNewsSuperAdmin, p => p
            .AddAuthenticationSchemes(AuthSchemes.RibbixNews)
            .RequireAuthenticatedUser()
            .RequireRole(Policies.SuperAdminRole));

    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.AddPolicy(RateLimits.Auth, ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    });

    builder.Services.AddExceptionHandler<ErrorHandler>();
    builder.Services.AddProblemDetails();
    builder.Services.AddResponseCaching();
    builder.Services.AddControllers().AddJsonOptions(o =>
    {
        var shared = RibbixJson.Options;
        o.JsonSerializerOptions.PropertyNamingPolicy = shared.PropertyNamingPolicy;
        o.JsonSerializerOptions.DictionaryKeyPolicy = shared.DictionaryKeyPolicy;
        o.JsonSerializerOptions.DefaultIgnoreCondition = shared.DefaultIgnoreCondition;
        foreach (var c in shared.Converters) o.JsonSerializerOptions.Converters.Add(c);
    });

    // За reverse-proxy (nginx) реальный IP нужен для allowlist вебхуков YooKassa.
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        foreach (var proxy in cfg.GetSection("KnownProxies").Get<string[]>() ?? [])
            o.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
    });

    var app = builder.Build();

    if (args.Length > 0 && await CliCommands.TryRunAsync(app, args)) return 0;

    // Dev без PostgreSQL: RIBBIX_DEV_STAFF="логин:роль:пароль" создаёт сотрудника RibbixNews в InMemory-БД.
    if (connectionString == "InMemory" && app.Environment.IsDevelopment() &&
        Environment.GetEnvironmentVariable("RIBBIX_DEV_STAFF")?.Split(':', 3) is [var login, var role, var pwd])
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RibbixDbContext>();
        db.StaffAccounts.Add(new StaffAccount
        {
            Id = Guid.NewGuid(), Login = login, Role = role,
            PasswordHash = AccountService.HashPassword(pwd), CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        Log.Information("Dev: создан сотрудник Ribbix News {Login} ({Role})", login, role);
    }

    app.UseForwardedHeaders();
    app.UseExceptionHandler();
    app.UseSerilogRequestLogging(o => o.GetLevel = (_, _, _) => Serilog.Events.LogEventLevel.Debug);
    app.UseResponseCaching();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();
    app.MapControllers();
    app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "API не запустился");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static TokenValidationParameters Validation(string issuer, string audience, string key) => new()
{
    ValidateIssuer = true,
    ValidIssuer = issuer,
    ValidateAudience = true,
    ValidAudience = audience,
    ValidateLifetime = true,
    ClockSkew = TimeSpan.FromSeconds(30),
    IssuerSigningKey = JwtService.KeyFrom(key),
    NameClaimType = "name",
    RoleClaimType = RibbixClaims.Role,
};

static IAsyncPolicy<HttpResponseMessage> RetryPolicy(PolicyBuilder<HttpResponseMessage> p) =>
    p.WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));

/// <summary>Служебные команды: миграции и создание сотрудника RibbixNews.</summary>
internal static class CliCommands
{
    public static async Task<bool> TryRunAsync(WebApplication app, string[] args)
    {
        switch (args[0])
        {
            case "migrate":
            {
                await using var scope = app.Services.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RibbixDbContext>().Database.MigrateAsync();
                Log.Information("Миграции применены");
                return true;
            }
            // dotnet run -- seed-staff <login> <role>; пароль — из переменной RIBBIX_STAFF_PASSWORD.
            case "seed-staff" when args.Length >= 3:
            {
                var password = Environment.GetEnvironmentVariable("RIBBIX_STAFF_PASSWORD");
                if (string.IsNullOrEmpty(password) || password.Length < 12)
                    throw new InvalidOperationException("Задайте RIBBIX_STAFF_PASSWORD (минимум 12 символов)");

                await using var scope = app.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<RibbixDbContext>();
                db.StaffAccounts.Add(new StaffAccount
                {
                    Id = Guid.NewGuid(),
                    Login = args[1],
                    Role = args[2],
                    PasswordHash = AccountService.HashPassword(password),
                    CreatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
                Log.Information("Сотрудник {Login} ({Role}) создан", args[1], args[2]);
                return true;
            }
            default:
                return false;
        }
    }
}

/// <summary>Для интеграционных тестов (WebApplicationFactory).</summary>
public partial class Program;

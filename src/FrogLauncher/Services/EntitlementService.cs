using System.Net.Http;
using FrogLauncher.Core;
using Frog.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FrogLauncher.Services;

/// <summary>Снимок прав. Кэшируется в entitlements.dat (DPAPI).</summary>
public sealed class EntitlementSnapshot
{
    public string Tier { get; set; } = TierSlugs.Tadpole;
    public List<string> Entitlements { get; set; } = new() { EntitlementKeys.Base };
    public DateTimeOffset? PeriodEnd { get; set; }
    public bool InGrace { get; set; }
    public DateTimeOffset? GraceUntil { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTimeOffset FetchedAt { get; set; }

    public static EntitlementSnapshot Tadpole() => new() { FetchedAt = DateTimeOffset.UtcNow };

    public static EntitlementSnapshot FromTier(TierDto tier, DateTimeOffset now) =>
        new() { Tier = tier.Slug, Entitlements = tier.Entitlements.ToList(), FetchedAt = now };
}

/// <summary>
/// Права пользователя в лаунчере. Два источника (LauncherOptions.SubscriptionSource):
///  • discord (по умолчанию) — тариф по ролям пользователя на вашем Discord-сервере, своего backend не нужно;
///  • server — ответ Frog.Api /entitlements (сроки, grace, Admin из FrogNews).
/// - IsAllowed(key) — мгновенно, из снимка.
/// - Обновление: при старте, раз в 6 часов и вручную (pull-to-refresh / «⟳ Обновить»).
/// - Нет связи → работаем по кэшу до 7 дней + флаг VerificationFailed для баннера.
/// Ни одно из этих состояний НЕ блокирует запуск игры. Клиентская проверка — только UX:
/// всё, что хранится и проверяется на компьютере игрока, при желании можно обойти.
/// </summary>
public sealed class EntitlementService : IDisposable
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    private readonly FrogApiClient _api;
    private readonly SessionStore _session;
    private readonly DiscordAuthService _discord;
    private readonly TierCatalog _tiers;
    private readonly LauncherOptions _o;
    private readonly TimeProvider _clock;
    private readonly ILogger<EntitlementService> _log;
    private readonly CancellationTokenSource _cts = new();
    private EntitlementSnapshot _snapshot;

    public EntitlementService(FrogApiClient api, SessionStore session, DiscordAuthService discord, TierCatalog tiers,
        IOptions<LauncherOptions> options, TimeProvider clock, ILogger<EntitlementService> log)
    {
        _api = api;
        _session = session;
        _discord = discord;
        _tiers = tiers;
        _o = options.Value;
        _clock = clock;
        _log = log;
        _snapshot = IsSignedIn ? LoadCache() ?? EntitlementSnapshot.Tadpole() : EntitlementSnapshot.Tadpole();
        _snapshot = ApplyLocalExpiry(_snapshot, _clock.GetUtcNow());

        session.SessionChanged += OnSessionChanged;
        discord.Changed += OnDiscordChanged;
    }

    public EntitlementSnapshot Current => _snapshot;
    public string Tier => _snapshot.Tier;
    public bool IsAdmin => _snapshot.Tier == TierSlugs.Admin;

    /// <summary>Тариф берётся из ролей Discord.</summary>
    public bool UsesDiscord => _o.UsesDiscordRoles;

    /// <summary>Вошёл ли пользователь туда, откуда берётся тариф (Discord или аккаунт FrogLauncher).</summary>
    public bool IsSignedIn => UsesDiscord ? _discord.IsLoggedIn : _session.IsLoggedIn;

    public string? SignedInName => UsesDiscord ? _discord.Current?.DisplayName : _session.Current?.Nick;

    /// <summary>Последняя проверка не удалась — показываем «Не удалось проверить подписку».</summary>
    public bool VerificationFailed { get; private set; }

    /// <summary>Вошёл через Discord, но не состоит на сервере — ролей нет, тариф Tadpole.</summary>
    public bool NotGuildMember { get; private set; }

    public event Action? Changed;

    public bool IsAllowed(string key) => _snapshot.Entitlements.Contains(key);

    /// <summary>null — без ограничений.</summary>
    public int? MaxInstances => EntitlementKeys.MaxInstances(_snapshot.Entitlements);

    public int? GraceDaysLeft => _snapshot is { InGrace: true, GraceUntil: { } g }
        ? Math.Max(0, (int)Math.Ceiling((g - _clock.GetUtcNow()).TotalDays))
        : null;

    /// <summary>Запуск фонового обновления раз в 6 часов.</summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            await RefreshAsync(_cts.Token);
            using var timer = new PeriodicTimer(RefreshInterval);
            while (await timer.WaitForNextTickAsync(_cts.Token)) await RefreshAsync(_cts.Token);
        });
    }

    public Task RefreshAsync(CancellationToken ct = default) =>
        UsesDiscord ? RefreshFromDiscordAsync(ct) : RefreshFromServerAsync(ct);

    private async Task RefreshFromDiscordAsync(CancellationToken ct)
    {
        if (!_discord.IsLoggedIn)
        {
            NotGuildMember = false;
            Update(EntitlementSnapshot.Tadpole(), failed: false);
            return;
        }

        try
        {
            var member = await _discord.GetMemberAsync(ct);
            NotGuildMember = member is null;
            var tier = member is null ? _tiers.Tadpole : DiscordRoleMapper.Resolve(member.RoleIds, _o.DiscordRoles, _tiers);
            var snap = EntitlementSnapshot.FromTier(tier, _clock.GetUtcNow());
            ProtectedStore.Save(LauncherPaths.EntitlementsFile, snap);
            Update(snap, failed: false);
        }
        catch (DiscordAuthException ex) when (ex.SessionExpired)
        {
            ProtectedStore.Delete(LauncherPaths.EntitlementsFile);
            Update(EntitlementSnapshot.Tadpole(), failed: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or DiscordAuthException)
        {
            _log.LogWarning("Не удалось проверить роли Discord: {Message}", ex.Message);
            var cached = LoadCache();
            Update(cached is not null ? ApplyLocalExpiry(cached, _clock.GetUtcNow()) : EntitlementSnapshot.Tadpole(), failed: true);
        }
    }

    private async Task RefreshFromServerAsync(CancellationToken ct)
    {
        if (!_session.IsLoggedIn)
        {
            Update(EntitlementSnapshot.Tadpole(), failed: false);
            return;
        }

        try
        {
            var dto = await _api.GetEntitlementsAsync(ct);
            var snap = new EntitlementSnapshot
            {
                Tier = dto.Tier,
                Entitlements = dto.Entitlements.ToList(),
                PeriodEnd = dto.PeriodEnd,
                InGrace = dto.InGrace,
                GraceUntil = dto.GraceUntil,
                CancelAtPeriodEnd = dto.CancelAtPeriodEnd,
                FetchedAt = _clock.GetUtcNow(),
            };
            ProtectedStore.Save(LauncherPaths.EntitlementsFile, snap);
            Update(snap, failed: false);
        }
        catch (ApiException ex) when (ex.Status == System.Net.HttpStatusCode.Unauthorized)
        {
            // Сессия отозвана (например, сняли Admin) — tadpole до повторного входа.
            ProtectedStore.Delete(LauncherPaths.EntitlementsFile);
            Update(EntitlementSnapshot.Tadpole(), failed: false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ApiException)
        {
            _log.LogWarning("Не удалось проверить подписку: {Message}", ex.Message);
            var cached = LoadCache();
            Update(cached is not null ? ApplyLocalExpiry(cached, _clock.GetUtcNow()) : EntitlementSnapshot.Tadpole(), failed: true);
        }
    }

    /// <summary>
    /// Офлайн-правила: кэш старше 7 дней → tadpole; период кончился → считаем grace 7 дней от конца;
    /// grace кончился → tadpole. (У тарифов из ролей Discord срока нет — действует только TTL кэша.)
    /// </summary>
    public static EntitlementSnapshot ApplyLocalExpiry(EntitlementSnapshot s, DateTimeOffset now)
    {
        if (s.Tier == TierSlugs.Tadpole) return s;
        if (now - s.FetchedAt > CacheTtl) return EntitlementSnapshot.Tadpole();
        if (s.Tier == TierSlugs.Admin) return s;

        if (!s.InGrace && s.PeriodEnd is { } end && end < now)
        {
            if (s.CancelAtPeriodEnd) return EntitlementSnapshot.Tadpole();
            s.InGrace = true;
            s.GraceUntil = end.AddDays(7);
        }

        if (s.InGrace && s.GraceUntil is { } g && g < now) return EntitlementSnapshot.Tadpole();
        return s;
    }

    private void OnDiscordChanged()
    {
        if (!UsesDiscord) return;
        if (_discord.IsLoggedIn) _ = RefreshAsync();
        else
        {
            ProtectedStore.Delete(LauncherPaths.EntitlementsFile);
            NotGuildMember = false;
            Update(EntitlementSnapshot.Tadpole(), failed: false);
        }
    }

    private void OnSessionChanged()
    {
        if (UsesDiscord) return;
        if (_session.Current is { } s)
        {
            // Сразу после входа — права из claims токена, не дожидаясь сети.
            var claims = JwtClaims.Read(s.AccessToken);
            if (claims.Tier != _snapshot.Tier)
                Update(new EntitlementSnapshot { Tier = claims.Tier, Entitlements = claims.Entitlements.ToList(), FetchedAt = _clock.GetUtcNow() }, VerificationFailed);
            _ = RefreshAsync();
        }
        else
        {
            ProtectedStore.Delete(LauncherPaths.EntitlementsFile);
            Update(EntitlementSnapshot.Tadpole(), failed: false);
        }
    }

    private static EntitlementSnapshot? LoadCache() => ProtectedStore.Load<EntitlementSnapshot>(LauncherPaths.EntitlementsFile);

    private void Update(EntitlementSnapshot snap, bool failed)
    {
        _snapshot = snap;
        VerificationFailed = failed;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _session.SessionChanged -= OnSessionChanged;
        _discord.Changed -= OnDiscordChanged;
    }
}

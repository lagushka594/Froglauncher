using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FrogLauncher.Core;
using FrogLauncher.Services;
using Frog.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FrogLauncher.ViewModels;

/// <summary>Карточка тарифа.</summary>
public partial class TierCardViewModel : ObservableObject
{
    public TierCardViewModel(TierDto tier, IReadOnlyCollection<string> previous, bool isCurrent)
    {
        Tier = tier;
        IsCurrent = isCurrent;
        Brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(tier.Color));
        Brush.Freeze();
        GlowColor = (Color)ColorConverter.ConvertFromString(tier.Color);
        // На карточке — только то, что добавляет тариф относительно предыдущего («+…»).
        Features = tier.Entitlements.Except(previous)
            .Select(EntitlementLabels.Label).OfType<string>().ToList();
    }

    public TierDto Tier { get; }
    public string Name => Tier.Name;
    public SolidColorBrush Brush { get; }
    public Color GlowColor { get; }
    public bool IsHighlighted => Tier.Highlighted;
    public bool IsFree => Tier.PriceMonth is null or 0;
    public bool IsCurrent { get; }
    public IReadOnlyList<string> Features { get; }

    [ObservableProperty] private bool _yearly;
    /// <summary>Скидка для текущего периода (акция или промокод) — пересчитывает страница.</summary>
    [ObservableProperty] private Discount? _discount;

    public decimal BasePrice => (Yearly ? Tier.PriceYear : Tier.PriceMonth) ?? 0;
    public decimal FinalPrice => Discount?.Apply(BasePrice) ?? BasePrice;
    public bool HasDiscount => !IsFree && Discount is not null;
    public string OldPriceText => HasDiscount ? $"{BasePrice:0} ₽" : "";
    public string DiscountBadge => Discount is { } d ? $"−{d.Percent}% · {d.Emoji} {(d.Code is null ? L.T(d.Label) : d.Label)}".Replace("·  ", "· ") : "";

    public string PriceText => IsFree ? L.T("Бесплатно") : $"{FinalPrice:0} ₽";
    public string PeriodText => IsFree ? "" : Yearly ? L.T("в год") : L.T("в месяц");
    public string? YearHint => !IsFree && Yearly ? L.T($"≈ {Tier.PriceYear / 12:0} ₽/мес · 2 месяца в подарок") : null;
    public bool CanBuy => !IsFree && !IsCurrent;

    partial void OnDiscountChanged(Discount? value) => OnPropertyChanged(string.Empty);

    partial void OnYearlyChanged(bool value)
    {
        OnPropertyChanged(nameof(BasePrice));
        OnPropertyChanged(nameof(FinalPrice));
        OnPropertyChanged(nameof(OldPriceText));
        OnPropertyChanged(nameof(PriceText));
        OnPropertyChanged(nameof(PeriodText));
        OnPropertyChanged(nameof(YearHint));
    }
}

/// <summary>
/// Раздел «Подписка» (👑). Цены — только с сервера (GET /tiers).
/// Оплата: POST /billing/checkout → браузер → froglauncher://billing/success → опрос /entitlements → тост.
/// </summary>
public partial class SubscriptionViewModel(
    FrogApiClient api,
    EntitlementService entitlements,
    SessionStore session,
    GameAuthService accounts,
    ToastService toasts,
    IOptions<LauncherOptions> options,
    PromoService promos,
    NewYearService newYear,
    ILogger<SubscriptionViewModel> log) : ObservableObject, IPageActivated, IRefreshable
{
    // ---------- Акции и промокоды ----------
    [ObservableProperty] private string _promoInput = "";
    [ObservableProperty] private string? _appliedCode;
    [ObservableProperty] private string? _promoMessage;
    [ObservableProperty] private bool _promoError;
    [ObservableProperty] private string? _eventBanner;
    [ObservableProperty] private bool _showPromoBox;

    private void ApplyDiscounts()
    {
        var now = new DateTimeOffset(AppClock.Now);
        var period = IsYearly ? BillingPeriod.Year : BillingPeriod.Month;
        // Новогодний купон (подарки за вход) — ещё один кандидат; скидки по-прежнему не суммируются.
        var gift = newYear.ActiveCoupon(AppClock.Today);
        foreach (var t in Tiers)
        {
            var best = t.IsFree ? null : promos.Catalog.Best(t.Tier.Slug, period, AppliedCode, now);
            if (!t.IsFree && gift > (best?.Percent ?? 0)) best = new Discount(gift, "gift", L.T("Новогодний подарок"), null, null, "🎁");
            t.Discount = best;
        }
        var ev = promos.Catalog.ActiveEvents(DateOnly.FromDateTime(now.LocalDateTime)).OrderByDescending(e => e.Percent).FirstOrDefault();
        EventBanner = ev is null ? null : L.T($"{ev.Emoji} {ev.Title}: −{ev.Percent}% на тарифы — скидка уже в ценах");
    }

    [RelayCommand]
    private void TogglePromo() => ShowPromoBox = !ShowPromoBox;

    /// <summary>Код проверяется для каждого тарифа: подходит хотя бы к одному — применяем.</summary>
    [RelayCommand]
    private void ApplyPromo()
    {
        var code = PromoCatalog.Normalize(PromoInput);
        if (code.Length == 0) return;
        var period = IsYearly ? BillingPeriod.Year : BillingPeriod.Month;
        var checks = Tiers.Where(t => !t.IsFree)
            .Select(t => promos.Catalog.Check(code, t.Tier.Slug, period, DateTimeOffset.Now, out _)).ToList();
        var result = checks.Contains(PromoCheck.Ok) ? PromoCheck.Ok
            : checks.FirstOrDefault(c => c != PromoCheck.Unknown && c != PromoCheck.WrongTier, checks.FirstOrDefault());
        PromoError = result != PromoCheck.Ok;
        if (result == PromoCheck.Ok)
        {
            AppliedCode = code;
            var p = promos.Catalog.Find(code)!;
            PromoMessage = $"«{code}»: −{p.Percent}%" + (p.Tiers.Count > 0 ? L.T(" на ") + string.Join(", ", p.Tiers.Select(TierNames.Display)) : "") +
                           (p.Partner is { Length: > 0 } partner ? L.T($" · от {partner}") : "");
        }
        else PromoMessage = L.T(PromoCatalog.Explain(result));
        ApplyDiscounts();
    }

    [RelayCommand]
    private void ClearPromo()
    {
        AppliedCode = null;
        PromoInput = "";
        PromoMessage = null;
        ApplyDiscounts();
    }

    public Task RefreshPageAsync() => RefreshAsync();

    public ObservableCollection<TierCardViewModel> Tiers { get; } = new();
    public ObservableCollection<PaymentHistoryItem> History { get; } = new();

    public string OfferUrl => options.Value.OfferUrl;
    public bool DiscordPurchase => options.Value.PurchaseMode != "checkout";
    public string DiscordUrl => options.Value.DiscordPurchaseUrl;
    public string RefundUrl => options.Value.RefundPolicyUrl;

    [ObservableProperty] private bool _isYearly;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private bool _isAdmin;
    [ObservableProperty] private string _currentTierName = "Tadpole";
    [ObservableProperty] private Brush _currentTierBrush = Brushes.Gray;
    [ObservableProperty] private string _periodText = "";
    [ObservableProperty] private string? _graceBanner;
    [ObservableProperty] private bool _verificationFailed;
    [ObservableProperty] private bool _showManage;
    [ObservableProperty] private bool _canCancel;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string _signInHint = "";
    [ObservableProperty] private bool _canManage;

    public async Task OnActivatedAsync()
    {
        entitlements.Changed -= OnEntitlementsChanged;
        entitlements.Changed += OnEntitlementsChanged;
        UpdateStatus();
        _ = RefreshPromosAsync();
        await LoadTiersAsync();
    }

    private async Task RefreshPromosAsync()
    {
        await promos.RefreshAsync(CancellationToken.None);
        ApplyDiscounts();
    }

    private void OnEntitlementsChanged() =>
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            UpdateStatus();
            RebuildCards(Tiers.Select(t => t.Tier).ToList());
        });

    partial void OnIsYearlyChanged(bool value)
    {
        foreach (var t in Tiers) t.Yearly = value;
        ApplyDiscounts();
    }

    private static string TiersCacheFile => Path.Combine(LauncherPaths.Cache, "tiers.json");

    /// <summary>
    /// Тарифы: с сервера (и сохраняем копию); без сети — последняя сохранённая копия,
    /// а если её нет — tiers.json, вшитый в лаунчер. Карточки видны всегда.
    /// </summary>
    private async Task LoadTiersAsync()
    {
        IsLoading = true;
        Error = null;
        // Сразу показываем сохранённые/вшитые карточки — не ждём сеть (повторы запроса могут идти до минуты).
        if (Tiers.Count == 0) RebuildCards(LoadOfflineTiers());
        try
        {
            var tiers = await api.GetTiersAsync(CancellationToken.None);
            RebuildCards(tiers);
            Directory.CreateDirectory(LauncherPaths.Cache);
            await File.WriteAllTextAsync(TiersCacheFile, JsonSerializer.Serialize(new TiersResponse(tiers), FrogJson.Options));
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Тарифы недоступны: {Message}", ex.Message);
            RebuildCards(LoadOfflineTiers());
            Error = L.T("Нет связи с сервером — показаны сохранённые тарифы. Оформить подписку можно, когда сервер станет доступен; игра запускается как обычно.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal static IReadOnlyList<TierDto> LoadOfflineTiers()
    {
        try
        {
            if (File.Exists(TiersCacheFile) &&
                JsonSerializer.Deserialize<TiersResponse>(File.ReadAllText(TiersCacheFile), FrogJson.Options) is { Tiers.Count: > 0 } cached)
                return cached.Tiers;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { }

        return EmbeddedTiers.Load().Public;
    }

    private void RebuildCards(IReadOnlyList<TierDto> tiers)
    {
        var previous = new List<string>();
        var cards = new List<TierCardViewModel>();
        // Скрытые тиры (admin) сервер не отдаёт, но фильтруем и на клиенте.
        foreach (var t in tiers.Where(t => !t.Hidden).OrderBy(t => t.Rank))
        {
            cards.Add(new TierCardViewModel(t, previous, t.Slug == entitlements.Tier) { Yearly = IsYearly });
            previous = t.Entitlements.ToList();
        }
        Tiers.ReplaceWith(cards);
        ApplyDiscounts();
    }

    private void UpdateStatus()
    {
        var s = entitlements.Current;
        IsLoggedIn = entitlements.IsSignedIn;
        // История платежей и автопродление есть только у подписок через свой сервер.
        CanManage = IsLoggedIn && !entitlements.UsesDiscord;
        SignInHint = entitlements.UsesDiscord
            ? L.T("Войдите через Discord в «Профиле» — тариф включится по вашей роли на сервере. Играть можно и без входа.")
            : L.T("Войдите в аккаунт FrogLauncher в «Профиле», чтобы оформить подписку. Играть можно и без него.");
        IsAdmin = entitlements.IsAdmin;
        VerificationFailed = entitlements.VerificationFailed;
        CurrentTierName = TierNames.Display(s.Tier);
        CurrentTierBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(s.Tier switch
        {
            TierSlugs.Froglet => "#22C55E",
            TierSlugs.FrogPlus => "#F59E0B",
            TierSlugs.FrogPro => "#F97316",
            TierSlugs.FrogLegend => "#8B5CF6",
            TierSlugs.Admin => "#7C3AED",
            _ => "#6B7280",
        }));
        PeriodText = s switch
        {
            { Tier: TierSlugs.Admin } => L.T("Выдано администрацией · бессрочно"),
            _ when entitlements.NotGuildMember => L.T("Вы не на нашем Discord-сервере — вступите, чтобы роль подтянулась"),
            { Tier: not TierSlugs.Tadpole } when entitlements.UsesDiscord => L.T("Тариф по вашей роли на Discord-сервере"),
            { PeriodEnd: { } end, CancelAtPeriodEnd: true } => L.T($"Автопродление отключено · доступ до {end.LocalDateTime:dd.MM.yyyy}"),
            { PeriodEnd: { } end } => L.T($"Продлится {end.LocalDateTime:dd.MM.yyyy}"),
            _ => L.T("Бесплатный тариф · игра доступна всегда"),
        };
        GraceBanner = entitlements.GraceDaysLeft is { } d
            ? L.T($"Подписка истекла. Продлите. Осталось {d} {MainViewModel.Plural(d, "день", "дня", "дней")}.")
            : null;
        CanCancel = s.Tier is not (TierSlugs.Tadpole or TierSlugs.Admin) && !s.CancelAtPeriodEnd;
    }

    /// <summary>«Pull-to-refresh» (жест прокрутки вверх на странице) и кнопка «Обновить».</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        try
        {
            await entitlements.RefreshAsync();
            await LoadTiersAsync();
            if (entitlements.VerificationFailed) toasts.Show(L.T("Не удалось проверить подписку — работаем по кэшу"), ToastKind.Warning);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task CheckoutAsync(TierCardViewModel card)
    {
        // Через Discord заказ можно оформить и без входа: ник попадёт в текст заказа.
        if (DiscordPurchase)
        {
            OpenDiscordTicket(card);
            return;
        }

        if (!session.IsLoggedIn)
        {
            toasts.Show(L.T("Войдите в аккаунт FrogLauncher в «Профиле», чтобы оформить подписку"), ToastKind.Info);
            WeakReferenceMessenger.Default.Send(new NavigateMessage(Page.Profile));
            return;
        }

        try
        {
            var r = await api.CheckoutAsync(card.Tier.Slug, IsYearly ? BillingPeriod.Year : BillingPeriod.Month, AppliedCode, CancellationToken.None);
            if (r.IsUpgrade && r.Amount > 0) toasts.Show(L.T($"Апгрейд: доплата за остаток периода — {r.Amount:0.##} ₽"), ToastKind.Info, 6);
            Shell.Open(r.PaymentUrl);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            toasts.Show(ex is ApiException ? ex.Message : L.T("Сервер недоступен"), ToastKind.Error, 6);
        }
    }

    /// <summary>
    /// Покупка через тикет в Discord: копируем готовый текст заказа и открываем сервер.
    /// После оплаты сотрудник выдаёт тариф в FrogNews, лаунчер подхватит его при обновлении.
    /// </summary>
    private void OpenDiscordTicket(TierCardViewModel card)
    {
        // Текст заказа читают сотрудники в Discord — он всегда по-русски, на каком бы языке ни был лаунчер. noloc
        var period = IsYearly ? "год" : "месяц"; // noloc
        var promo = card.Discount switch
        {
            { Code: { } code } d => $"\nПромокод: {code} (−{d.Percent}%)" + (d.Partner is { } p ? $" · партнёр {p}" : ""), // noloc
            { Source: "gift" } d => $"\nНовогодний подарок: −{d.Percent}% (дней в лаунчере: {newYear.Days(AppClock.Today)})", // noloc
            { } d => $"\nАкция: {d.Label} (−{d.Percent}%)", // noloc
            null => "",
        };
        var full = card.HasDiscount ? $" (без скидки {card.BasePrice:0} ₽)" : ""; // noloc
        var account = entitlements.SignedInName is { Length: > 0 } nick
            ? (entitlements.UsesDiscord ? $"Discord: {nick}" : $"Аккаунт FrogLauncher: {nick}") // noloc
            : (entitlements.UsesDiscord ? "Discord: пока не вошёл в лаунчере" : "Аккаунт FrogLauncher: не создан"); // noloc
        var order = $"🐸 Заказ FrogLauncher\n{account}\nИгровой ник: {accounts.Selected?.Name ?? "—"}\nТариф: {card.Name} ({period}){promo}\nСумма: {card.FinalPrice:0} ₽{full}"; // noloc
        try { System.Windows.Clipboard.SetText(order); }
        catch (System.Runtime.InteropServices.COMException) { /* буфер занят другой программой — не критично */ }

        Shell.Open(DiscordUrl);
        toasts.Show(L.T("Создайте тикет в канале покупки и вставьте заказ (Ctrl+V) — он уже скопирован. ") +
                    L.T("После оплаты тариф включится автоматически."), ToastKind.Info, 9);
    }

    [RelayCommand]
    private void OpenDiscord() => Shell.Open(DiscordUrl);

    [RelayCommand]
    private async Task ManageAsync()
    {
        ShowManage = !ShowManage;
        if (!ShowManage || !session.IsLoggedIn) return;
        try
        {
            History.ReplaceWith(await api.HistoryAsync(CancellationToken.None));
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            toasts.Show(L.T("История платежей недоступна"), ToastKind.Warning);
        }
    }

    [RelayCommand]
    private async Task CancelAutoRenewAsync()
    {
        var ok = System.Windows.MessageBox.Show(
            L.T("Отключить автопродление? Доступ к функциям сохранится до конца оплаченного периода."),
            "FrogLauncher", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.Yes) return;
        try
        {
            var r = await api.CancelAsync(CancellationToken.None);
            toasts.Show(r.ActiveUntil is { } until ? L.T($"Автопродление отключено. Доступ до {until.LocalDateTime:dd.MM.yyyy}") : L.T("Подписка отменена"), ToastKind.Info, 6);
            await entitlements.RefreshAsync();
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            toasts.Show(ex is ApiException ? ex.Message : L.T("Сервер недоступен"), ToastKind.Error);
        }
    }

    [RelayCommand]
    private void OpenLink(string url) => Shell.Open(url);

    /// <summary>
    /// После возврата из браузера: вебхук мог ещё не дойти — опрашиваем /entitlements до 60 секунд.
    /// </summary>
    public async Task WaitForActivationAsync(string? subId)
    {
        var before = entitlements.Tier;
        toasts.Show(L.T("Проверяем оплату…"), ToastKind.Info, 3);
        for (var i = 0; i < 20; i++)
        {
            await entitlements.RefreshAsync();
            if (entitlements.Tier != before || (entitlements.Current.PeriodEnd is { } end && end > DateTimeOffset.UtcNow.AddDays(25)))
            {
                toasts.Show(L.T("Подписка активирована 🐸"), ToastKind.Success, 6);
                UpdateStatus();
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(3));
        }

        log.LogInformation("Подписка {SubId} пока не активна", subId);
        toasts.Show(L.T("Оплата обрабатывается. Подписка включится автоматически в течение нескольких минут."), ToastKind.Info, 8);
    }
}

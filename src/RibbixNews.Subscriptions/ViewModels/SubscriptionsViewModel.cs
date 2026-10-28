using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Ribbix.Shared;
using RibbixNews.Subscriptions.Services;

namespace RibbixNews.Subscriptions.ViewModels;

/// <summary>
/// Раздел «Подписки» RibbixNews: пользователи RibbixLauncher (поиск, пагинация, выдача/снятие Admin) и аудит.
/// Email приходит с сервера уже замаскированным — полный адрес в RibbixNews не попадает.
/// </summary>
public partial class SubscriptionsViewModel(AdminApiClient api, ServiceTokenProvider tokens, IAdminDialogs dialogs) : ObservableObject
{
    public const int PageSize = 25;
    private CancellationTokenSource? _searchCts;

    public ObservableCollection<AdminUserDto> Users { get; } = new();
    public ObservableCollection<AdminGrantDto> Audit { get; } = new();

    [ObservableProperty] private string _search = "";
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrevPageCommand), nameof(NextPageCommand))]
    private int _page = 1;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrevPageCommand), nameof(NextPageCommand))]
    private int _totalPages = 1;
    [ObservableProperty] private int _total;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _error;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GrantCommand), nameof(RevokeCommand),
        nameof(GrantSubscriptionCommand), nameof(RevokeSubscriptionCommand))]
    private AdminUserDto? _selectedUser;

    public string Operator => tokens.Login ?? "—";

    public async Task LoadAsync()
    {
        await LoadUsersAsync();
        await LoadAuditAsync();
    }

    /// <summary>Поиск с задержкой 350 мс, чтобы не дёргать сервер на каждую букву.</summary>
    partial void OnSearchChanged(string value)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        _ = Task.Delay(350, ct).ContinueWith(async _ =>
        {
            Page = 1;
            await LoadUsersAsync();
        }, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.FromCurrentSynchronizationContext());
    }

    [RelayCommand]
    private async Task LoadUsersAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            var r = await api.UsersAsync(Search, Page, PageSize, CancellationToken.None);
            Users.Clear();
            foreach (var u in r.Items) Users.Add(u);
            Total = r.Total;
            TotalPages = Math.Max(1, r.TotalPages);
        }
        catch (Exception ex) when (ex is AdminApiException or HttpRequestException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task LoadAuditAsync()
    {
        try
        {
            var list = await api.GrantsAsync(null, CancellationToken.None);
            Audit.Clear();
            foreach (var g in list) Audit.Add(g);
        }
        catch (Exception ex) when (ex is AdminApiException or HttpRequestException)
        {
            Error = ex.Message;
        }
    }

    private bool CanPrev => Page > 1;
    private bool CanNext => Page < TotalPages;

    [RelayCommand(CanExecute = nameof(CanPrev))]
    private async Task PrevPageAsync()
    {
        Page--;
        await LoadUsersAsync();
    }

    [RelayCommand(CanExecute = nameof(CanNext))]
    private async Task NextPageAsync()
    {
        Page++;
        await LoadUsersAsync();
    }

    private bool CanGrant => SelectedUser is { IsAdmin: false };
    private bool CanRevoke => SelectedUser is { IsAdmin: true, ActiveGrantId: not null };

    /// <summary>Выдать Admin: причина обязательна + подтверждение вводом слова ADMIN (в диалоге).</summary>
    [RelayCommand(CanExecute = nameof(CanGrant))]
    private async Task GrantAsync()
    {
        if (SelectedUser is not { } user) return;
        var reason = dialogs.AskGrantReason(user.LauncherNick);
        if (reason is null) return;

        try
        {
            await api.GrantAsync(user.Id, reason, CancellationToken.None);
            await LoadAsync();
        }
        catch (Exception ex) when (ex is AdminApiException or HttpRequestException)
        {
            dialogs.ShowError(ex.Message);
        }
    }

    /// <summary>Снять Admin: сервер отзывает все токены пользователя — лаунчер потребует повторный вход.</summary>
    [RelayCommand(CanExecute = nameof(CanRevoke))]
    private async Task RevokeAsync()
    {
        if (SelectedUser is not { ActiveGrantId: { } grantId } user) return;
        var reason = dialogs.AskRevokeReason(user.LauncherNick);
        if (reason is null) return;

        try
        {
            await api.RevokeAsync(grantId, reason, CancellationToken.None);
            await LoadAsync();
        }
        catch (Exception ex) when (ex is AdminApiException or HttpRequestException)
        {
            dialogs.ShowError(ex.Message);
        }
    }

    private bool CanGrantSubscription => SelectedUser is { IsAdmin: false };
    private bool CanRevokeSubscription => SelectedUser is { IsAdmin: false } u && u.Tier != TierSlugs.Tadpole;

    /// <summary>Выдать платный тариф после оплаты через тикет в Discord (повторно — продлить).</summary>
    [RelayCommand(CanExecute = nameof(CanGrantSubscription))]
    private async Task GrantSubscriptionAsync()
    {
        if (SelectedUser is not { } user) return;
        try
        {
            var tiers = (await api.TiersAsync(CancellationToken.None)).Where(t => t.PriceMonth > 0).ToList();
            if (dialogs.AskSubscriptionGrant(user.LauncherNick, tiers) is not { } choice) return;
            await api.GrantSubscriptionAsync(user.Id, choice.Tier, choice.Period, choice.Reason, CancellationToken.None);
            await LoadAsync();
        }
        catch (Exception ex) when (ex is AdminApiException or HttpRequestException)
        {
            dialogs.ShowError(ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRevokeSubscription))]
    private async Task RevokeSubscriptionAsync()
    {
        if (SelectedUser is not { } user) return;
        var reason = dialogs.AskSubscriptionRevokeReason(user.LauncherNick);
        if (reason is null) return;
        try
        {
            await api.RevokeSubscriptionAsync(user.Id, reason, CancellationToken.None);
            await LoadAsync();
        }
        catch (Exception ex) when (ex is AdminApiException or HttpRequestException)
        {
            dialogs.ShowError(ex.Message);
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        var path = dialogs.AskCsvPath($"admin_audit_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        if (path is null) return;
        try
        {
            await File.WriteAllBytesAsync(path, await api.ExportCsvAsync(CancellationToken.None));
        }
        catch (Exception ex) when (ex is AdminApiException or HttpRequestException or IOException)
        {
            dialogs.ShowError(ex.Message);
        }
    }
}

/// <summary>Диалог выдачи: кнопка активна только при заполненной причине и введённом слове ADMIN.</summary>
public partial class GrantAdminDialogViewModel(string nick) : ObservableObject
{
    public const string ConfirmWord = "ADMIN";

    public string Nick { get; } = nick;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _reason = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _confirmation = "";

    public bool CanConfirm => Reason.Trim().Length >= 5 && Confirmation.Trim() == ConfirmWord;
}

/// <summary>Диалог выдачи платного тарифа: тариф, срок и номер тикета Discord.</summary>
public partial class GrantSubscriptionDialogViewModel(string nick, IReadOnlyList<TierDto> tiers) : ObservableObject
{
    public string Nick { get; } = nick;
    public IReadOnlyList<TierDto> Tiers { get; } = tiers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm), nameof(Summary))]
    private TierDto? _selectedTier = tiers.FirstOrDefault();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool _yearly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _reason = "";

    public string Summary => SelectedTier is { } t
        ? $"{t.Name} на {(Yearly ? "год" : "месяц")} — {(Yearly ? t.PriceYear : t.PriceMonth):0} ₽. Если тариф уже есть, срок продлится."
        : "";

    public bool CanConfirm => SelectedTier is not null && Reason.Trim().Length >= 5;
}

public partial class RevokeAdminDialogViewModel(string nick) : ObservableObject
{
    public string Nick { get; } = nick;
    public string Title { get; init; } = "Снять Admin с " + nick;
    public string Warning { get; init; } =
        "Все сессии пользователя будут завершены: лаунчер попросит войти заново, тир вернётся к оплаченному (или Tadpole).";
    public string ConfirmText { get; init; } = "Снять и завершить сессии";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirm))]
    private string _reason = "";

    public bool CanConfirm => Reason.Trim().Length >= 5;
}

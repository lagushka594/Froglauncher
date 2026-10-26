using System.Windows;
using Frog.Shared;
using FrogNews.Subscriptions.ViewModels;
using FrogNews.Subscriptions.Views;
using Microsoft.Win32;

namespace FrogNews.Subscriptions.Services;

/// <summary>Диалоги — за интерфейсом, чтобы ViewModel тестировалась без окон.</summary>
public interface IAdminDialogs
{
    /// <summary>Причина выдачи или null, если отменено.</summary>
    string? AskGrantReason(string nick);

    /// <summary>Причина отзыва или null, если отменено.</summary>
    string? AskRevokeReason(string nick);

    /// <summary>Тариф, период и номер тикета или null, если отменено.</summary>
    (string Tier, BillingPeriod Period, string Reason)? AskSubscriptionGrant(string nick, IReadOnlyList<TierDto> tiers);

    /// <summary>Причина снятия платного тарифа или null.</summary>
    string? AskSubscriptionRevokeReason(string nick);

    string? AskCsvPath(string suggestedName);

    void ShowError(string message);
}

public sealed class WpfAdminDialogs : IAdminDialogs
{
    public string? AskGrantReason(string nick)
    {
        var vm = new GrantAdminDialogViewModel(nick);
        var dlg = new GrantAdminDialog { DataContext = vm, Owner = Application.Current.MainWindow };
        return dlg.ShowDialog() == true ? vm.Reason.Trim() : null;
    }

    public string? AskRevokeReason(string nick)
    {
        var vm = new RevokeAdminDialogViewModel(nick);
        var dlg = new RevokeAdminDialog { DataContext = vm, Owner = Application.Current.MainWindow };
        return dlg.ShowDialog() == true ? vm.Reason.Trim() : null;
    }

    public (string Tier, BillingPeriod Period, string Reason)? AskSubscriptionGrant(string nick, IReadOnlyList<TierDto> tiers)
    {
        var vm = new GrantSubscriptionDialogViewModel(nick, tiers);
        var dlg = new GrantSubscriptionDialog { DataContext = vm, Owner = Application.Current.MainWindow };
        return dlg.ShowDialog() == true && vm.SelectedTier is { } t
            ? (t.Slug, vm.Yearly ? BillingPeriod.Year : BillingPeriod.Month, vm.Reason.Trim())
            : null;
    }

    public string? AskSubscriptionRevokeReason(string nick)
    {
        var vm = new RevokeAdminDialogViewModel(nick)
        {
            Title = "Снять подписку с " + nick,
            Warning = "Платный тариф, выданный вручную, будет снят сразу — пользователь вернётся на Tadpole. Используйте при возврате денег или ошибке выдачи.",
            ConfirmText = "Снять подписку",
        };
        var dlg = new RevokeAdminDialog { DataContext = vm, Owner = Application.Current.MainWindow };
        return dlg.ShowDialog() == true ? vm.Reason.Trim() : null;
    }

    public string? AskCsvPath(string suggestedName)
    {
        var dlg = new SaveFileDialog { FileName = suggestedName, Filter = "CSV (*.csv)|*.csv", Title = "Экспорт аудита" };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public void ShowError(string message) =>
        MessageBox.Show(message, "FrogNews — Подписки", MessageBoxButton.OK, MessageBoxImage.Warning);
}

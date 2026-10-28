using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RibbixLauncher.Core;
using RibbixLauncher.Services;

namespace RibbixLauncher.ViewModels;

public sealed record QuestRow(string Emoji, string Title, string ProgressText, double Percent, bool Done, string Reward);

/// <summary>Карточка «Задания дня» на главной: три задания, уровень и опыт.</summary>
public partial class QuestsViewModel(QuestService quests, ToastService toasts) : ObservableObject
{
    public ObservableCollection<QuestRow> Rows { get; } = new();
    [ObservableProperty] private string _levelText = "";
    [ObservableProperty] private double _levelPercent;
    [ObservableProperty] private string _nextReward = "";

    public void Refresh()
    {
        var (list, justDone, xp) = quests.Refresh();
        Rows.ReplaceWith(list.Select(q => new QuestRow(q.Def.Emoji, q.Def.Title,
            q.Def.Kind == "play" ? L.T($"{q.Value} / {q.Def.Goal} мин") : $"{q.Value} / {q.Def.Goal}", q.Percent, q.Done, $"+{q.Def.Xp} XP")));
        var level = xp / QuestService.XpPerLevel + 1;
        LevelText = L.T($"Уровень {level} · {xp % QuestService.XpPerLevel} / {QuestService.XpPerLevel} XP");
        LevelPercent = xp % QuestService.XpPerLevel * 100.0 / QuestService.XpPerLevel;
        NextReward = level < 5 ? L.T("На 5 уровне — тема «Неоновое болото»")
            : level < 10 ? L.T("На 10 уровне — тема «Король пруда»")
            : L.T("Все награды собраны — вы король пруда 👑");
        foreach (var q in justDone)
            toasts.Show(L.T($"{q.Emoji} Задание выполнено: «{q.Title}» +{q.Xp} XP"), ToastKind.Success, 5);
        var before = (xp - justDone.Sum(q => q.Xp)) / QuestService.XpPerLevel + 1;
        if (justDone.Count > 0 && level > before)
            toasts.Show(level is 5 or 10 ? L.T($"🎉 Уровень {level}! Открыта новая тема — «Настройки» → «Тема»") : L.T($"🎉 Уровень {level}!"), ToastKind.Success, 7);
    }
}

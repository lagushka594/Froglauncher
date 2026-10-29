using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RibbixLauncher.Core;
using RibbixLauncher.Services;

namespace RibbixLauncher.ViewModels;

public sealed record QuestRow(string Emoji, string Title, string ProgressText, double Percent, bool Done, string Reward);

/// <summary>Карточка «Задания дня» на главной: три задания, уровень и опыт.</summary>
public partial class QuestsViewModel(QuestService quests, AchievementService achievements, ToastService toasts) : ObservableObject
{
    public ObservableCollection<QuestRow> Rows { get; } = new();
    public ObservableCollection<QuestRow> WeeklyRows { get; } = new();
    [ObservableProperty] private string _levelText = "";
    [ObservableProperty] private double _levelPercent;
    [ObservableProperty] private string _nextReward = "";

    public void Refresh()
    {
        var (list, weekly, justDone, xp) = quests.Refresh();
        static QuestRow Row(QuestProgress q) => new(q.Def.Emoji, q.Def.Title,
            q.Def.Kind is "play" or "wplay" ? L.T($"{q.Value} / {q.Def.Goal} мин") : $"{q.Value} / {q.Def.Goal}", q.Percent, q.Done, $"+{q.Def.Xp} XP");
        Rows.ReplaceWith(list.Select(Row));
        WeeklyRows.ReplaceWith(weekly.Select(Row));
        var level = xp / QuestService.XpPerLevel + 1;
        LevelText = L.T($"Уровень {level} · {xp % QuestService.XpPerLevel} / {QuestService.XpPerLevel} XP");
        LevelPercent = xp % QuestService.XpPerLevel * 100.0 / QuestService.XpPerLevel;
        var next = QuestService.Rewards.FirstOrDefault(r => r.Level > level);
        NextReward = next.Level > 0 ? L.T($"На {next.Level} уровне — тема «{next.Theme}»") : L.T("Все награды собраны — вы король пруда 👑");
        foreach (var q in justDone)
            toasts.Show(L.T($"{q.Emoji} Задание выполнено: «{q.Title}» +{q.Xp} XP"), ToastKind.Success, 5);
        var before = (xp - justDone.Sum(q => q.Xp)) / QuestService.XpPerLevel + 1;
        if (justDone.Count > 0 && level > before)
            toasts.Show(QuestService.Rewards.Any(r => r.Level > before && r.Level <= level)
                ? L.T($"🎉 Уровень {level}! Открыта новая тема — «Настройки» → «Тема»") : L.T($"🎉 Уровень {level}!"), ToastKind.Success, 7);
        foreach (var a in achievements.Check())
            toasts.Show(L.T($"🏅 Достижение: {a.Emoji} «{a.Title}»"), ToastKind.Success, 6);
    }
}

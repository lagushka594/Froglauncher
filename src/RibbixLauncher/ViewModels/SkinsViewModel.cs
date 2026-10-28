using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using RibbixLauncher.Services;
using Microsoft.Extensions.Logging;

namespace RibbixLauncher.ViewModels;

public partial class SkinItem(SkinEntry entry, bool active) : ObservableObject
{
    public SkinEntry Entry { get; } = entry;
    public string Path => SkinService.PathOf(Entry);
    public string Name => Entry.Name;
    public bool Slim => Entry.Slim;
    public string ModelText => Entry.Slim ? "Alex" : "Steve";
    [ObservableProperty] private bool _isActive = active;
}

/// <summary>«Скины»: библиотека, превью, переключение в один клик, добавление из файла и по нику.</summary>
public partial class SkinsViewModel(SkinService skins, GameAuthService accounts, ToastService toasts, ILogger<SkinsViewModel> log)
    : ObservableObject, IPageActivated
{
    public ObservableCollection<SkinItem> Items { get; } = new();
    [ObservableProperty] private SkinItem? _selected;
    [ObservableProperty] private string _nick = "";
    [ObservableProperty] private bool _newSlim;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _accountText = "";
    [ObservableProperty] private string _howText = "";
    private SkinLibrary _lib = new();

    public Task OnActivatedAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    private void Reload()
    {
        _lib = skins.Load();
        var acc = accounts.Selected;
        var active = acc is not null && _lib.Active.TryGetValue(acc.Id.ToString(), out var id) ? id : null;
        Items.ReplaceWith(_lib.Skins.Select(s => new SkinItem(s, s.Id == active)));
        Selected = Items.FirstOrDefault(i => i.IsActive) ?? Items.FirstOrDefault();
        AccountText = acc is null ? L.T("Аккаунт не выбран") : $"{acc.Name} · {acc.TypeLabel}";
        HowText = acc?.Type == AccountType.Microsoft
            ? L.T("Лицензия: скин загружается в ваш профиль Minecraft — его видят все игроки в мирах и на серверах.")
            : L.T("Оффлайн-ник: скин включается ресурспаком при запуске — вы видите его в мирах и на серверах. Другие игроки видят его только на серверах со SkinsRestorer. Игроки без своего скина тоже будут выглядеть так на вашем экране.");
    }

    /// <summary>Надеть в один клик.</summary>
    [RelayCommand]
    private async Task WearAsync(SkinItem? item)
    {
        item ??= Selected;
        if (item is null || IsBusy) return;
        if (accounts.Selected is not { } acc)
        {
            toasts.Show(L.T("Добавьте аккаунт в «Профиле» — подойдёт и оффлайн-ник"), ToastKind.Info);
            return;
        }
        IsBusy = true;
        try
        {
            acc = await accounts.EnsureFreshAsync(acc, CancellationToken.None);
            await skins.ApplyAsync(_lib, item.Entry, acc, CancellationToken.None);
            foreach (var i in Items) i.IsActive = i == item;
            QuestService.MarkGlobal("skin");
            toasts.Show(acc.Type == AccountType.Microsoft
                ? L.T($"Скин «{item.Name}» загружен в профиль Minecraft 🐸 В игре он появится сразу (на сервере — после перезахода)")
                : L.T($"Скин «{item.Name}» надет 🐸 Он включится при следующем запуске игры"), ToastKind.Success, 6);
        }
        catch (Exception ex) when (ex is SkinException or HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            log.LogWarning(ex, "Скин не надет");
            toasts.Show(ex is SkinException ? ex.Message : L.T("Не удалось надеть скин: ") + ex.Message, ToastKind.Error, 7);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void TakeOff()
    {
        if (accounts.Selected is not { } acc) return;
        skins.ClearActive(_lib, acc);
        foreach (var i in Items) i.IsActive = false;
        toasts.Show(acc.Type == AccountType.Microsoft
            ? L.T("Скин снят в лаунчере. В профиле Minecraft остаётся последний загруженный — поменяйте его на другой.")
            : L.T("Скин снят — в игре снова стандартный"), ToastKind.Info, 6);
    }

    [RelayCommand]
    private void AddFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = L.T("Скин Minecraft|*.png"), Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        foreach (var f in dlg.FileNames)
        {
            try
            {
                var e = skins.Add(_lib, File.ReadAllBytes(f), Path.GetFileNameWithoutExtension(f), NewSlim);
                toasts.Show(L.T($"Скин «{e.Name}» добавлен"), ToastKind.Success);
            }
            catch (Exception ex) when (ex is SkinException or IOException)
            {
                toasts.Show(L.T($"«{Path.GetFileName(f)}»: ") + ex.Message, ToastKind.Error, 6);
            }
        }
        Reload();
    }

    [RelayCommand]
    private async Task AddByNickAsync()
    {
        if (string.IsNullOrWhiteSpace(Nick) || IsBusy) return;
        IsBusy = true;
        try
        {
            var (png, slim) = await skins.DownloadByNickAsync(Nick, CancellationToken.None);
            var e = skins.Add(_lib, png, Nick.Trim(), slim);
            Nick = "";
            Reload();
            Selected = Items.FirstOrDefault(i => i.Entry.Id == e.Id);
            toasts.Show(L.T($"Скин «{e.Name}» добавлен"), ToastKind.Success);
        }
        catch (Exception ex) when (ex is SkinException or HttpRequestException or TaskCanceledException or FormatException or IOException)
        {
            toasts.Show(ex is SkinException ? ex.Message : L.T("Mojang недоступен, попробуйте позже"), ToastKind.Error, 6);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ToggleModel(SkinItem? item)
    {
        if ((item ?? Selected) is not { } i) return;
        i.Entry.Slim = !i.Entry.Slim;
        skins.Save(_lib);
        var id = i.Entry.Id;
        Reload();
        Selected = Items.FirstOrDefault(x => x.Entry.Id == id);
    }

    [RelayCommand]
    private void Remove(SkinItem? item)
    {
        if ((item ?? Selected) is not { } i) return;
        if (System.Windows.MessageBox.Show(L.T($"Удалить скин «{i.Name}»?"), "Ribbix Launcher", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) != System.Windows.MessageBoxResult.Yes) return;
        skins.Remove(_lib, i.Entry);
        Reload();
    }

    [RelayCommand]
    private static void OpenFolder()
    {
        Directory.CreateDirectory(SkinService.Dir);
        Shell.Open(SkinService.Dir);
    }
}

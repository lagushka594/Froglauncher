using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrogLauncher.Core;
using FrogLauncher.Models;
using FrogLauncher.Services;
using Microsoft.Win32;

namespace FrogLauncher.ViewModels;

/// <summary>Карточка привилегии на вкладке «Привилегии».</summary>
public sealed class TierCard
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string PriceText { get; set; } = "";
    public string Perks { get; set; } = "";
    public string Color { get; set; } = "#8FA89A";
    public bool Owned { get; set; }
}

/// <summary>Привилегии (донат): Tadpole · Froglet · Frog+ · Frog Legacy · Admin.</summary>
public sealed partial class MainViewModel
{
    private readonly PrivilegeService _priv = new();
    private List<PrivilegeGrant> _grants = new();
    private bool _privLoaded;

    // ----- текущий уровень -----
    private Tier _tier;
    public Tier CurrentTier { get => _tier; private set { if (Set(ref _tier, value)) NotifyTier(); } }
    public bool HasTier => _tier != Tier.None;
    public string TierName => _tier == Tier.None ? Loc.T("priv_none") : TierTable.NameOf(_tier);
    public string TierBadge => _tier == Tier.None ? "" : "🐸 " + TierTable.NameOf(_tier);
    public string TierColor => TierTable.ColorOf(_tier);

    // возможности по уровням (у каждого уровня есть свои уникальные)
    public bool CanBackground => _tier >= Tier.Tadpole;      // свой фон
    public bool CanAutoBackup => _tier >= Tier.Froglet;      // авто-бэкап миров
    public bool CanStats => _tier >= Tier.Froglet;           // статистика игры
    public bool CanAutoUpdateMods => _tier >= Tier.FrogPlus; // авто-обновление модов
    public bool CanFast => _tier >= Tier.FrogPlus;           // быстрая загрузка
    public bool CanModProfiles => _tier >= Tier.FrogPlus;    // профили модов
    public bool CanRpcText => _tier >= Tier.Legacy;          // свой статус Discord
    public bool CanBeta => _tier >= Tier.Legacy;             // бета-версии лаунчера
    public bool IsAdmin => _tier == Tier.Admin;              // панель администратора

    // ----- настройки привилегий -----
    private string _customBg = ""; public string CustomBackground { get => _customBg; set { Set(ref _customBg, value); UpdateBackground(); } }
    private bool _autoBackup; public bool AutoBackupWorlds { get => _autoBackup; set => Set(ref _autoBackup, value); }
    private bool _autoUpdMods; public bool AutoUpdateMods { get => _autoUpdMods; set => Set(ref _autoUpdMods, value); }
    private bool _fast; public bool FastDownloads { get => _fast; set { Set(ref _fast, value); ApplyPerkRuntime(); } }
    private bool _beta; public bool BetaUpdates { get => _beta; set => Set(ref _beta, value); }
    private string _presence = ""; public string CustomPresence { get => _presence; set { Set(ref _presence, value); ApplyPerkRuntime(); } }
    private ImageSource? _bgImage; public ImageSource? CustomBackgroundImage { get => _bgImage; private set => Set(ref _bgImage, value); }

    private void LoadPrivSettings(AppSettings s)
    {
        CustomBackground = s.CustomBackground; AutoBackupWorlds = s.AutoBackupWorlds; AutoUpdateMods = s.AutoUpdateMods;
        FastDownloads = s.FastDownloads; BetaUpdates = s.BetaUpdates; CustomPresence = s.CustomPresence;
    }

    private void SavePrivSettings(AppSettings s)
    {
        s.CustomBackground = CustomBackground; s.AutoBackupWorlds = AutoBackupWorlds; s.AutoUpdateMods = AutoUpdateMods;
        s.FastDownloads = FastDownloads; s.BetaUpdates = BetaUpdates; s.CustomPresence = CustomPresence;
    }

    // ----- карточки уровней, статистика, админ-панель -----
    public ObservableCollection<TierCard> Tiers { get; } = new();
    public ObservableCollection<PrivilegeGrant> AdminGrants { get; } = new();
    public string StatsText { get; private set; } = "";
    public string AdminSummary => $"{Loc.T("priv_admin_total")}: {_grants.Count}";

    public ICommand BuyCommand { get; private set; } = null!;
    public ICommand RefreshPrivilegesCommand { get; private set; } = null!;
    public ICommand PickBackgroundCommand { get; private set; } = null!;
    public ICommand ResetBackgroundCommand { get; private set; } = null!;
    public ICommand SaveModProfileCommand { get; private set; } = null!;
    public ICommand ApplyModProfileCommand { get; private set; } = null!;
    public ICommand DeleteModProfileCommand { get; private set; } = null!;

    private void InitPrivileges()
    {
        BuyCommand = new RelayCommand(p => Buy(p as string));
        RefreshPrivilegesCommand = new AsyncRelayCommand(_ => RefreshPrivilegesAsync());
        PickBackgroundCommand = new RelayCommand(_ => PickBackground());
        ResetBackgroundCommand = new RelayCommand(_ => CustomBackground = "");
        SaveModProfileCommand = new RelayCommand(_ => SaveModProfile());
        ApplyModProfileCommand = new RelayCommand(_ => ApplyModProfile());
        DeleteModProfileCommand = new RelayCommand(_ => DeleteModProfile());
        foreach (var n in new[] { nameof(BuyCommand), nameof(RefreshPrivilegesCommand), nameof(PickBackgroundCommand), nameof(ResetBackgroundCommand),
                     nameof(SaveModProfileCommand), nameof(ApplyModProfileCommand), nameof(DeleteModProfileCommand) })
            OnChanged(n);

        Loc.I.PropertyChanged += (_, _) => { RebuildTiers(); OnChanged(nameof(TierName)); OnChanged(nameof(AdminSummary)); };
        RebuildTiers();
        RebuildThemes();
        _ = RefreshPrivilegesAsync();
    }

    public async Task RefreshPrivilegesAsync()
    {
        var url = !string.IsNullOrWhiteSpace(NewsUrlOverride) ? NewsUrlOverride.Trim() : BuiltIn.NewsUrl;
        try
        {
            _grants = await _priv.FetchAsync(url, CancellationToken.None);
            _privLoaded = true;
        }
        catch (Exception ex) { Log.Warn("Привилегии: " + ex.Message); }
        UpdateTier();
        RebuildAdminGrants();
    }

    /// <summary>Пересчитать уровень для текущего аккаунта (вызывается при входе/смене аккаунта и после загрузки списка).</summary>
    private void UpdateTier()
    {
        var t = PrivilegeService.Resolve(_grants, _account);
        if (t == _tier) { NotifyTier(); return; }
        CurrentTier = t;      // вызовет NotifyTier()
    }

    private void NotifyTier()
    {
        foreach (var n in new[] { nameof(HasTier), nameof(TierName), nameof(TierBadge), nameof(TierColor), nameof(CanBackground), nameof(CanAutoBackup),
                     nameof(CanStats), nameof(CanAutoUpdateMods), nameof(CanFast), nameof(CanModProfiles), nameof(CanRpcText), nameof(CanBeta), nameof(IsAdmin) })
            OnChanged(n);
        RebuildThemes();
        RebuildTiers();
        BuildStats();
        ApplyPerkRuntime();
        RebuildAdminGrants();
    }

    // ----- темы: у каждого уровня своя эксклюзивная -----
    private void RebuildThemes()
    {
        var allowed = new List<string> { "Green", "Cyan", "Purple" };
        allowed.AddRange(TierTable.ThemesFor(_tier));
        Themes.Clear();
        foreach (var t in allowed) Themes.Add(t);
        // если уровня больше нет — возвращаем бесплатную тему (но только когда список привилегий точно загружен)
        if (_privLoaded && !allowed.Contains(Theme)) Theme = "Green";
        else if (allowed.Contains(Theme)) OnChanged(nameof(Theme));
    }

    private void RebuildTiers()
    {
        Tiers.Clear();
        foreach (var d in TierTable.Purchasable)
            Tiers.Add(new TierCard
            {
                Id = d.Id, Name = d.Name, PriceText = $"{d.PriceRub} ₽", Color = d.Color,
                Perks = Loc.T("tier_" + d.Id + "_perks"), Owned = _tier >= d.Tier
            });
    }

    private void RebuildAdminGrants()
    {
        AdminGrants.Clear();
        if (!IsAdmin) return;
        foreach (var g in _grants.OrderByDescending(g => g.Since)) AdminGrants.Add(g);
        OnChanged(nameof(AdminSummary));
    }

    private void BuildStats()
    {
        if (!CanStats) { StatsText = ""; OnChanged(nameof(StatsText)); return; }
        long total = Instances.Sum(i => i.PlaySeconds);
        var top = Instances.OrderByDescending(i => i.PlaySeconds).FirstOrDefault();
        var t = TimeSpan.FromSeconds(total);
        StatsText = string.Format(Loc.T("priv_stats_fmt"), $"{(int)t.TotalHours} ч {t.Minutes} мин", Instances.Count,
            top != null && top.PlaySeconds > 0 ? top.Name : "—");
        OnChanged(nameof(StatsText));
    }

    /// <summary>Применить «живые» эффекты: потоки загрузки, текст статуса Discord, фон, тема.</summary>
    private void ApplyPerkRuntime()
    {
        Downloader.Parallelism = CanFast && FastDownloads ? 16 : 8;
        if (_rpc != null) _rpc.StateOverride = CanRpcText && !string.IsNullOrWhiteSpace(CustomPresence) ? CustomPresence.Trim() : null;
        UpdateBackground();
        if (Themes.Contains(Theme)) App.ApplyTheme(Theme);
    }

    private void UpdateBackground()
    {
        try
        {
            if (CanBackground && !string.IsNullOrWhiteSpace(CustomBackground) && File.Exists(CustomBackground))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(CustomBackground);
                bi.CacheOption = BitmapCacheOption.OnLoad;      // файл не блокируем
                bi.DecodePixelWidth = 1920;
                bi.EndInit();
                bi.Freeze();
                CustomBackgroundImage = bi;
            }
            else CustomBackgroundImage = null;
        }
        catch (Exception ex) { Log.Warn("Фон: " + ex.Message); CustomBackgroundImage = null; }
    }

    private void PickBackground()
    {
        var dlg = new OpenFileDialog { Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dlg.ShowDialog() == true) CustomBackground = dlg.FileName;
    }

    // ----- привилегии перед запуском игры -----
    private async Task PreLaunchPerksAsync(Instance inst, IProgress<DownloadProgress> prog, CancellationToken ct)
    {
        if (CanAutoBackup && AutoBackupWorlds)
        {
            StatusText = "Резервная копия миров…";
            try { _instances.BackupWorlds(inst); _instances.PruneBackups(inst, 5); }
            catch (Exception ex) { Log.Warn("Авто-бэкап: " + ex.Message); }
        }
        if (CanAutoUpdateMods && AutoUpdateMods && inst.Loader != "Vanilla")
        {
            StatusText = "Обновление модов…";
            try { await _modSvc.UpdateAllAsync(inst, prog, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { Log.Warn("Авто-обновление модов: " + ex.Message); }
        }
    }

    // ----- покупка: ведёт в Discord, текст заявки копируется в буфер -----
    private void Buy(string? id)
    {
        var def = TierTable.Purchasable.FirstOrDefault(d => d.Id == id) ?? TierTable.Purchasable[0];
        var nick = CurrentAccount?.Username ?? "—";
        var type = CurrentAccount == null ? "—" : CurrentAccount.IsMicrosoft ? "Microsoft" : "Offline";
        var msg = string.Format(Loc.T("priv_msg_template"), def.Name, def.PriceRub, nick, type);
        try { Clipboard.SetText(msg); } catch { }
        OpenDiscord();
        Dialogs.Info(string.Format(Loc.T("priv_buy_dialog"), def.Name, def.PriceRub, msg));
    }

    // ----- профили модов (Frog+) -----
    public ObservableCollection<string> ModProfiles { get; } = new();
    private string? _selProfile; public string? SelectedModProfile { get => _selProfile; set => Set(ref _selProfile, value); }
    private string _profileName = ""; public string NewModProfileName { get => _profileName; set => Set(ref _profileName, value); }

    private string ProfilesPath => _inst == null ? "" : Path.Combine(_inst.Dir, "mod-profiles.json");

    private Dictionary<string, List<string>> ReadProfiles()
    {
        try
        {
            if (ProfilesPath != "" && File.Exists(ProfilesPath))
                return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(ProfilesPath)) ?? new();
        }
        catch (Exception ex) { Log.Warn("mod-profiles.json: " + ex.Message); }
        return new();
    }

    private void LoadModProfiles()
    {
        ModProfiles.Clear();
        foreach (var k in ReadProfiles().Keys.OrderBy(k => k)) ModProfiles.Add(k);
    }

    private void SaveModProfile()
    {
        if (_inst == null || string.IsNullOrWhiteSpace(NewModProfileName)) { Dialogs.Info(Loc.T("priv_profile_name")); return; }
        var all = ReadProfiles();
        all[NewModProfileName.Trim()] = Mods.Where(m => m.Enabled).Select(m => m.FileName).ToList();
        Directory.CreateDirectory(_inst.Dir);
        File.WriteAllText(ProfilesPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        LoadModProfiles();
        SelectedModProfile = NewModProfileName.Trim();
        StatusText = Loc.T("priv_profile_saved");
    }

    private void ApplyModProfile()
    {
        if (_inst == null || SelectedModProfile == null) return;
        if (!ReadProfiles().TryGetValue(SelectedModProfile, out var enabled)) return;
        var set = enabled.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var m in Mods.ToList()) m.Enabled = set.Contains(m.FileName);   // включаем/выключаем переименованием
        ReloadMods();
        StatusText = Loc.T("priv_profile_applied");
    }

    private void DeleteModProfile()
    {
        if (_inst == null || SelectedModProfile == null) return;
        var all = ReadProfiles();
        if (all.Remove(SelectedModProfile))
            File.WriteAllText(ProfilesPath, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        LoadModProfiles();
    }
}

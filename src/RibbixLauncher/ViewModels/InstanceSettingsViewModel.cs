using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RibbixLauncher.Core;
using RibbixLauncher.Models;
using RibbixLauncher.Services;
using Ribbix.Shared;

namespace RibbixLauncher.ViewModels;

public partial class SoundItem(string key, string label, double value) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    [ObservableProperty] private double _value = value;
}

public partial class KeyBindItem(string key, string label, string value) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Display))] private string _value = value;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Display))] private bool _isCapturing;
    public string Display => IsCapturing ? L.T("Нажмите клавишу…") : McValues.KeyLabel(Value);
}

public partial class PackItem(string id, string title, bool enabled) : ObservableObject
{
    /// <summary>Как в options.txt: "file/имя.zip" или "vanilla".</summary>
    public string Id { get; } = id;
    public string Title { get; } = title;
    [ObservableProperty] private bool _enabled = enabled;
}

public partial class SodiumOption(string path, string label, bool value) : ObservableObject
{
    /// <summary>Путь в sodium-options.json, например "performance.use_entity_culling".</summary>
    public string Path { get; } = path;
    public string Label { get; } = label;
    [ObservableProperty] private bool _value = value;
}

/// <summary>
/// Экран «Настройки версии»: options.txt Minecraft (графика, игра, звук, управление), параметры Java
/// и производительности из instance.json, ресурспаки, шейдеры Iris и опции Sodium.
/// Ничего не пишется, пока не нажато «Сохранить».
/// </summary>
public partial class InstanceSettingsViewModel : ObservableObject
{
    private readonly Instance _inst;
    private readonly InstanceService _instances;
    private readonly JavaService _java;
    private readonly EntitlementService _entitlements;
    private readonly ToastService _toasts;
    private OptionsFile _opt;
    private readonly bool _legacyGraphics;

    public InstanceSettingsViewModel(Instance inst, InstanceService instances, JavaService java,
        EntitlementService entitlements, ToastService toasts, WorldsViewModel worlds, PackTransferService transfer,
        FriendLinkService friends, string? lastServer, FpsBenchmarkService bench, GameAuthService accounts, SharedPackService shared)
    {
        _shared = shared;
        HasGitHubToken = shared.HasToken;
        SharedLink = inst.HostGist is { } g ? SharedPackService.ShareLink(g) : null;
        _bench = bench;
        _accounts = accounts;
        _friends = friends;
        InviteServer = lastServer ?? "";
        _transfer = transfer;
        Worlds = worlds;
        _ = worlds.LoadAsync();
        _inst = inst;
        _instances = instances;
        _java = java;
        _entitlements = entitlements;
        _toasts = toasts;
        Hardware = HardwareService.Scan();
        Presets = HardwareService.Presets(Hardware);
        RecommendedKind = HardwareService.Classify(Hardware);
        _opt = OptionsFile.Load(inst.GameDirectory);
        _legacyGraphics = _opt.Has("fancyGraphics") && !_opt.Has("graphicsMode");
        LoadFromOptions();
        LoadInstance();
        LoadPacks();
        _ = LoadJavaAsync();
    }

    public Instance Instance => _inst;
    public WorldsViewModel Worlds { get; }

    // ---------- Замер FPS ----------
    private readonly FpsBenchmarkService _bench;
    private readonly GameAuthService _accounts;
    private CancellationTokenSource? _benchCts;
    public IReadOnlyList<int> FpsTargets { get; } = new[] { 30, 60, 90, 120, 144 };
    [ObservableProperty] private int _fpsTarget = 60;
    [ObservableProperty] private bool _isBenchmarking;
    [ObservableProperty] private string? _benchmarkText;

    /// <summary>Настоящий замер: игра сама заходит в копию мира, лаунчер считает FPS и подбирает пресет под цель.</summary>
    [RelayCommand]
    private async Task RunBenchmarkAsync()
    {
        if (IsBenchmarking) { _benchCts?.Cancel(); return; }
        if (_accounts.Selected is not { } account)
        {
            _toasts.Show(L.T("Добавьте аккаунт в «Профиле» — подойдёт и оффлайн-ник"), ToastKind.Info);
            return;
        }
        IsBenchmarking = true;
        _benchCts = new CancellationTokenSource();
        try
        {
            var r = await _bench.RunAsync(_inst, account, Presets, RecommendedKind, FpsTarget,
                new Progress<string>(s => BenchmarkText = s), _benchCts.Token);
            BenchmarkText = r.Message + "\n" + string.Join("\n", r.Runs.Select(x => L.T($"• {x.Preset.Title}: {x.Average:0} FPS, в худшие моменты {x.Low:0}")));
            if (r.Best is { } best) ApplyPreset(best);
            QuestService.MarkGlobal("fps");
        }
        catch (OperationCanceledException)
        {
            BenchmarkText = L.T("Замер остановлен, настройки игры не изменились.");
        }
        catch (Exception ex) when (ex is FpsBenchmarkException or InvalidOperationException or IOException or System.Net.Http.HttpRequestException)
        {
            BenchmarkText = ex.Message;
        }
        finally
        {
            IsBenchmarking = false;
        }
    }

    // ---------- Общая сборка (GitHub Gist) ----------
    private readonly SharedPackService _shared;
    [ObservableProperty] private bool _hasGitHubToken;
    [ObservableProperty] private string? _sharedLink;
    [ObservableProperty] private bool _isSharing;

    [RelayCommand]
    private void SaveGitHubToken(System.Windows.Controls.PasswordBox? box)
    {
        if (box is null) return;
        try
        {
            _shared.SaveToken(box.Password);
            box.Clear();
            HasGitHubToken = true;
            _toasts.Show(L.T("Токен сохранён (зашифрован на этом ПК)"), ToastKind.Success);
        }
        catch (InvalidOperationException ex) { _toasts.Show(ex.Message, ToastKind.Error, 6); }
    }

    [RelayCommand]
    private static void OpenTokenPage() => Shell.Open(SharedPackService.TokenPage);

    [RelayCommand]
    private void ForgetGitHubToken()
    {
        _shared.ForgetToken();
        HasGitHubToken = false;
    }

    [RelayCommand]
    private async Task MakeSharedAsync()
    {
        IsSharing = true;
        try
        {
            SharedLink = await _shared.PublishAsync(_inst, InviteServer, CancellationToken.None);
            CopyInvite(SharedLink);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
        {
            _toasts.Show(L.T("Не удалось сделать общей: ") + ex.Message, ToastKind.Error, 7);
        }
        finally
        {
            IsSharing = false;
        }
    }

    // ---------- «Играть с другом» ----------
    private readonly FriendLinkService _friends;
    [ObservableProperty] private bool _showInvite;
    [ObservableProperty] private string _inviteServer = "";
    [ObservableProperty] private string? _inviteLink;
    [ObservableProperty] private string? _inviteWebLink;
    [ObservableProperty] private string? _inviteNote;
    [ObservableProperty] private bool _isMakingInvite;

    [RelayCommand]
    private void ToggleInvite() => ShowInvite = !ShowInvite;

    [RelayCommand]
    private async Task MakeInviteAsync()
    {
        IsMakingInvite = true;
        try
        {
            var link = await _friends.CreateAsync(_inst, InviteServer, CancellationToken.None);
            InviteLink = link.Url;
            InviteWebLink = link.WebUrl;
            InviteNote = link.NotOnModrinth.Count == 0
                ? L.T($"В ссылке {link.Included} файлов с Modrinth — у друга всё скачается само.")
                : L.T($"В ссылке {link.Included} файлов с Modrinth. Не с Modrinth ({link.NotOnModrinth.Count}) друг не получит — отправьте их отдельно: {string.Join(", ", link.NotOnModrinth.Take(5))}");
            CopyInvite(link.WebUrl ?? link.Url);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
        {
            _toasts.Show(L.T("Modrinth недоступен, попробуйте позже"), ToastKind.Error);
        }
        finally
        {
            IsMakingInvite = false;
        }
    }

    /// <summary>Скопировать ссылку и открыть личные сообщения Discord — вставить другу.</summary>
    [RelayCommand]
    private void SendInDiscord()
    {
        CopyInvite(InviteWebLink ?? InviteLink);
        Shell.Open("https://discord.com/channels/@me");
    }

    [RelayCommand]
    private void CopyInvite(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { System.Windows.Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { return; }
        _toasts.Show(L.T("Ссылка скопирована — отправьте её другу 👫"), ToastKind.Success);
    }

    // ---------- Экспорт сборки ----------
    private readonly PackTransferService _transfer;
    [ObservableProperty] private bool _showPackExport;
    [ObservableProperty] private bool _exportMrpack = true;
    [ObservableProperty] private bool _exportWorlds;
    [ObservableProperty] private bool _exportConfigs = true;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string? _exportStatus;

    [RelayCommand]
    private void TogglePackExport() => ShowPackExport = !ShowPackExport;

    [RelayCommand]
    private async Task ExportPackAsync()
    {
        var ext = ExportMrpack ? ".mrpack" : ".zip";
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = string.Concat(_inst.Name.Split(Path.GetInvalidFileNameChars())) + ext,
            Filter = ExportMrpack ? L.T("Сборка Modrinth|*.mrpack") : L.T("Полная копия Ribbix|*.zip"),
        };
        if (dlg.ShowDialog() != true) return;
        IsExporting = true;
        try
        {
            var progress = new Progress<string>(s => ExportStatus = s);
            var r = await _transfer.ExportAsync(_inst, new ExportOptions(ExportMrpack ? PackFormat.Mrpack : PackFormat.FullZip, ExportWorlds, ExportConfigs),
                dlg.FileName, progress, CancellationToken.None);
            ExportStatus = ExportMrpack
                ? L.T($"Готово: {r.Linked} файлов ссылками на Modrinth, {r.Embedded} — внутри сборки.") +
                  (r.ModrinthOffline ? L.T(" Modrinth был недоступен — всё положено внутрь, файл получился больше.") : "")
                : L.T($"Готово: {r.Embedded} файлов в архиве.");
            _toasts.Show(L.T("Сборка экспортирована 📦 Её можно отправить другу"), ToastKind.Success);
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\"");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ExportStatus = null;
            _toasts.Show(L.T("Экспорт не удался: ") + ex.Message, ToastKind.Error, 7);
        }
        finally
        {
            IsExporting = false;
        }
    }
    public HardwareInfo Hardware { get; }
    public IReadOnlyList<SettingsPreset> Presets { get; }
    public PresetKind RecommendedKind { get; }
    public bool OptionsExisted => _opt.Existed;
    public bool CanAutotune => _entitlements.IsAllowed(EntitlementKeys.JvmAutotune);

    public string HardwareText
    {
        get
        {
            var gpu = Hardware.MainGpu;
            var vram = gpu is { VramBytes: > 0 } ? L.T($", {gpu.VramBytes / 1024.0 / 1024 / 1024:0.#} ГБ") : "";
            return L.T($"ОЗУ {Hardware.TotalRamGb:0.#} ГБ · {Hardware.Cores} потоков · {Hardware.CpuName}\n") +
                   L.T($"Видеокарта: {gpu?.Name ?? "не определена"}{vram} · Экран {Hardware.ScreenWidth}×{Hardware.ScreenHeight}");
        }
    }

    public string RecommendationText =>
        L.T($"Рекомендуем: {Presets.First(p => p.Kind == RecommendedKind).Title} · RAM {HardwareService.RecommendedRamMb(Hardware.TotalRamGb) / 1024} ГБ ") +
        L.T($"(у вас {Hardware.TotalRamGb:0} ГБ)");

    // ---------- Графика ----------
    public IReadOnlyList<string> GraphicsModes { get; } = new[] { L.T("Быстрая (Fast)"), L.T("Красивая (Fancy)"), "Fabulous!" };
    public IReadOnlyList<string> CloudModes { get; } = new[] { L.T("Выкл"), L.T("Быстрые"), L.T("Красивые") };
    public IReadOnlyList<string> ParticleModes { get; } = new[] { L.T("Все"), L.T("Уменьшено"), L.T("Минимум") };
    public IReadOnlyList<string> GuiScales { get; } = new[] { L.T("Авто"), L.T("Маленький"), L.T("Средний"), L.T("Большой"), L.T("Огромный") };
    public IReadOnlyList<string> ChatVisibilities { get; } = new[] { L.T("Показывать"), L.T("Только команды"), L.T("Скрыть") };

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Warnings))] private int _graphicsMode;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RenderHint), nameof(Warnings))] private int _renderDistance;
    [ObservableProperty] private int _simulationDistance;
    [ObservableProperty] private int _clouds;
    [ObservableProperty] private bool _entityShadows;
    [ObservableProperty] private bool _smoothLighting;
    [ObservableProperty] private int _particles;
    [ObservableProperty] private int _biomeBlend;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(FpsText))] private int _maxFps;
    [ObservableProperty] private bool _vSync;
    [ObservableProperty] private int _mipmap;
    [ObservableProperty] private int _entityDistance;
    [ObservableProperty] private int _fov;
    [ObservableProperty] private int _brightness;
    [ObservableProperty] private bool _bobView;
    [ObservableProperty] private int _screenEffects;
    [ObservableProperty] private int _fovEffects;
    [ObservableProperty] private int _guiScale;
    [ObservableProperty] private int _chatScale;
    [ObservableProperty] private int _chatOpacity;
    [ObservableProperty] private int _chatWidth;

    public string RenderHint => McValues.RenderDistanceHint(RenderDistance);
    public string FpsText => MaxFps >= McValues.UnlimitedFps ? L.T("без ограничения") : $"{MaxFps} FPS";

    // ---------- Игра ----------
    public IReadOnlyList<Choice> Languages => McValues.Languages;
    public ObservableCollection<SoundItem> Sounds { get; } = new();
    public ObservableCollection<KeyBindItem> Keys { get; } = new();
    [ObservableProperty] private bool _autoJump;
    [ObservableProperty] private int _chatVisibility;
    [ObservableProperty] private string _language = "ru_ru";
    [ObservableProperty] private int _mouseSensitivity;
    [ObservableProperty] private bool _invertMouse;

    // ---------- Производительность ----------
    public IReadOnlyList<Choice> GcOptions => McValues.GarbageCollectors;
    public IReadOnlyList<Choice> PriorityOptions => McValues.Priorities;
    public IReadOnlyList<string> ResolutionPresets { get; } = new[] { L.T("По умолчанию"), "1280×720", "1920×1080", "2560×1440", L.T("Своё") };
    public int MaxRamMb => HardwareService.MaxSafeRamMb(Hardware) + 2048;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Warnings), nameof(RamText))] private int _ramMax;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Warnings), nameof(RamText))] private int _ramMin;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Warnings))] private string _gc = "g1";
    [ObservableProperty] private string _priority = "normal";
    [ObservableProperty] private string _jvmArgs = "";
    [ObservableProperty] private bool _autotune;
    [ObservableProperty] private bool _fullscreen;
    [ObservableProperty] private bool _discreteGpu;
    [ObservableProperty] private string _resolutionPreset = L.T("По умолчанию");
    [ObservableProperty] private string _width = "";
    [ObservableProperty] private string _height = "";

    public string RamText => L.T($"{RamMin / 1024.0:0.#}–{RamMax / 1024.0:0.#} ГБ");
    public bool ShowDiscreteGpu => Hardware.HasDiscreteAndIntegrated;

    // ---------- Java ----------
    public ObservableCollection<JavaRuntime> Runtimes { get; } = new();
    [ObservableProperty] private JavaRuntime? _selectedRuntime;
    [ObservableProperty] private string? _javaTestOutput;
    public int RequiredJava => JavaService.RequiredMajor(_inst.GameVersion);

    // ---------- Паки ----------
    public ObservableCollection<PackItem> ResourcePacks { get; } = new();
    public ObservableCollection<string> ShaderPacks { get; } = new();
    public ObservableCollection<SodiumOption> SodiumOptions { get; } = new();
    [ObservableProperty] private string? _selectedShader;
    [ObservableProperty] private bool _shadersEnabled;
    public bool HasIris { get; private set; }
    public bool HasSodium { get; private set; }

    /// <summary>Предупреждения валидации — пересчитываются при изменении ключевых параметров.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var w = new List<string>();
            var physical = (int)(Hardware.TotalRamBytes / 1024 / 1024);
            if (RamMax > physical)
                w.Add(L.T($"⛔ {RamMax / 1024.0:0.#} ГБ RAM на {Hardware.TotalRamGb:0} ГБ системе — игра не запустится."));
            else if (RamMax > HardwareService.MaxSafeRamMb(Hardware))
                w.Add(L.T("⚠ Системе останется меньше 2 ГБ — Windows и браузер начнут тормозить."));
            if (RamMin > RamMax) w.Add(L.T("⛔ Минимум RAM больше максимума."));
            if (RecommendedKind == PresetKind.Performance && RenderDistance > 16)
                w.Add(L.T($"⚠ Дальность {RenderDistance} чанков на слабой видеокарте даст ~15 FPS. Рекомендуем 8–10."));
            if (RecommendedKind == PresetKind.Performance && GraphicsMode == 2)
                w.Add(L.T("⚠ Fabulous на встроенной графике сильно просаживает FPS."));
            if (Gc == "zgc" && RequiredJava < 17) w.Add(L.T("ℹ ZGC нужна Java 17+, для этой версии будет использован G1."));
            return w;
        }
    }

    // ================== Загрузка ==================

    private void LoadFromOptions()
    {
        var o = _opt;
        GraphicsMode = _legacyGraphics ? (o.GetBool("fancyGraphics", true) ? 1 : 0) : Math.Clamp(o.GetInt("graphicsMode", 1), 0, 2);
        RenderDistance = Math.Clamp(o.GetInt("renderDistance", 12), 2, 32);
        SimulationDistance = Math.Clamp(o.GetInt("simulationDistance", 12), 5, 32);
        Clouds = Math.Max(0, Array.IndexOf(McValues.Clouds, o.GetString("renderClouds") ?? "true"));
        EntityShadows = o.GetBool("entityShadows", true);
        SmoothLighting = o.Get("ao") is { } ao ? ao is "true" or "1" or "2" : true;
        Particles = Math.Clamp(o.GetInt("particles", 0), 0, 2);
        BiomeBlend = Math.Clamp(o.GetInt("biomeBlendRadius", 2), 0, 7);
        MaxFps = Math.Clamp(o.GetInt("maxFps", 120), 10, McValues.UnlimitedFps);
        VSync = o.GetBool("enableVsync", true);
        Mipmap = Math.Clamp(o.GetInt("mipmapLevels", 4), 0, 4);
        EntityDistance = (int)Math.Round(o.GetDouble("entityDistanceScaling", 1.0) * 100);
        Fov = McValues.FovFromOption(o.GetDouble("fov", 0));
        Brightness = (int)Math.Round(o.GetDouble("gamma", 0.5) * 100);
        BobView = o.GetBool("bobView", true);
        ScreenEffects = (int)Math.Round(o.GetDouble("screenEffectScale", 1) * 100);
        FovEffects = (int)Math.Round(o.GetDouble("fovEffectScale", 1) * 100);
        GuiScale = Math.Clamp(o.GetInt("guiScale", 0), 0, 4);
        ChatScale = (int)Math.Round(o.GetDouble("chatScale", 1) * 100);
        ChatOpacity = (int)Math.Round(o.GetDouble("chatOpacity", 1) * 100);
        ChatWidth = (int)Math.Round(o.GetDouble("chatWidth", 1) * 100);

        AutoJump = o.GetBool("autoJump", false);
        ChatVisibility = Math.Clamp(o.GetInt("chatVisibility", 0), 0, 2);
        Language = o.GetString("lang") ?? "ru_ru";
        MouseSensitivity = (int)Math.Round(o.GetDouble("mouseSensitivity", 0.5) * 200);
        InvertMouse = o.GetBool("invertYMouse", false);

        Sounds.ReplaceWith(McValues.SoundCategories.Select(s => new SoundItem(s.Key, s.Label, Math.Round(o.GetDouble(s.Key, 1) * 100))));
        Keys.ReplaceWith(McValues.KeyActions.Select(k => new KeyBindItem(k.Key, k.Label, o.Get(k.Key) ?? McValues.DefaultKeys[k.Key])));
    }

    private void LoadInstance()
    {
        RamMax = _inst.RamMb;
        RamMin = _inst.RamMinMb ?? Math.Min(_inst.RamMb, 1024);
        Gc = _inst.Gc;
        Priority = _inst.Priority;
        JvmArgs = _inst.JvmArgs;
        Autotune = _inst.JvmAutotune;
        Fullscreen = _inst.Fullscreen;
        DiscreteGpu = _inst.PreferDiscreteGpu;
        Width = _inst.Width?.ToString() ?? "";
        Height = _inst.Height?.ToString() ?? "";
        ResolutionPreset = (_inst.Width, _inst.Height) switch
        {
            (null, null) => L.T("По умолчанию"),
            (1280, 720) => "1280×720",
            (1920, 1080) => "1920×1080",
            (2560, 1440) => "2560×1440",
            _ => L.T("Своё"),
        };
    }

    partial void OnResolutionPresetChanged(string value)
    {
        // Первый пункт («По умолчанию») переведён — сравниваем с самим списком, а не с русской строкой.
        if (value == ResolutionPresets[0])
        {
            Width = Height = "";
            return;
        }
        switch (value)
        {
            case "1280×720": Width = "1280"; Height = "720"; break;
            case "1920×1080": Width = "1920"; Height = "1080"; break;
            case "2560×1440": Width = "2560"; Height = "1440"; break;
        }
    }

    private void LoadPacks()
    {
        var game = _inst.GameDirectory;
        // Ресурспаки: включённые — в порядке options.txt (последний — наивысший приоритет), затем остальные.
        var enabled = _opt.GetResourcePacks();
        var files = Directory.Exists(Path.Combine(game, "resourcepacks"))
            ? Directory.GetFileSystemEntries(Path.Combine(game, "resourcepacks")).Select(Path.GetFileName).OfType<string>().ToList()
            : new List<string>();
        var items = enabled.Where(e => e.StartsWith("file/", StringComparison.Ordinal) && files.Contains(e[5..]))
            .Reverse().Select(e => new PackItem(e, e[5..], true)).ToList();
        items.AddRange(files.Where(f => !enabled.Contains("file/" + f)).Order().Select(f => new PackItem("file/" + f, f, false)));
        ResourcePacks.ReplaceWith(items);

        var mods = Directory.Exists(Path.Combine(game, "mods")) ? Directory.GetFiles(Path.Combine(game, "mods"), "*.jar") : Array.Empty<string>();
        HasIris = mods.Any(m => Path.GetFileName(m).StartsWith("iris", StringComparison.OrdinalIgnoreCase)) ||
                  File.Exists(Path.Combine(game, "config", "iris.properties"));
        HasSodium = mods.Any(m => Path.GetFileName(m).StartsWith("sodium", StringComparison.OrdinalIgnoreCase));

        var shaderDir = Path.Combine(game, "shaderpacks");
        ShaderPacks.ReplaceWith(Directory.Exists(shaderDir)
            ? Directory.GetFileSystemEntries(shaderDir).Select(Path.GetFileName).OfType<string>().Where(n => !n.EndsWith(".txt")).Order()
            : Enumerable.Empty<string>());
        var iris = ReadProperties(Path.Combine(game, "config", "iris.properties"));
        SelectedShader = iris.GetValueOrDefault("shaderPack");
        ShadersEnabled = iris.GetValueOrDefault("enableShaders") == "true";

        // Sodium: показываем только те переключатели, что есть в конфиге этой версии мода.
        SodiumOptions.Clear();
        var sodiumFile = Path.Combine(game, "config", "sodium-options.json");
        if (File.Exists(sodiumFile) && JsonNode.Parse(File.ReadAllText(sodiumFile)) is JsonObject root)
        {
            (string path, string label)[] known =
            {
                ("performance.use_entity_culling", L.T("Отсечение невидимых сущностей (Entity Culling)")),
                ("performance.use_fog_occlusion", L.T("Отсечение в тумане")),
                ("performance.use_block_face_culling", L.T("Отсечение граней блоков")),
                ("performance.animate_only_visible_textures", L.T("Анимировать только видимые текстуры")),
                ("performance.always_defer_chunk_updates_v2", L.T("Отложенное обновление чанков")),
                ("quality.enable_vignette", L.T("Виньетка")),
                ("advanced.use_advanced_staging_buffers", L.T("Продвинутые буферы (быстрее на NVIDIA/AMD)")),
            };
            foreach (var (path, label) in known)
                if (Find(root, path) is JsonValue v && v.TryGetValue(out bool b))
                    SodiumOptions.Add(new SodiumOption(path, label, b));
        }
        OnPropertyChanged(nameof(HasIris));
        OnPropertyChanged(nameof(HasSodium));
    }

    private async Task LoadJavaAsync()
    {
        try
        {
            var list = await _java.DetectAsync();
            Runtimes.ReplaceWith(list);
            SelectedRuntime = list.FirstOrDefault(r => string.Equals(r.JavaExe, _inst.JavaPath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ================== Сохранение ==================

    [RelayCommand]
    private void Save()
    {
        if (Warnings.Any(w => w.StartsWith('⛔')))
        {
            _toasts.Show(L.T("Исправьте ошибки (⛔) — с такими настройками игра не запустится"), ToastKind.Error, 6);
            return;
        }

        WriteOptions();
        _opt.Save();
        WritePacksAndMods();

        _inst.RamMb = RamMax;
        _inst.RamMinMb = RamMin;
        _inst.Gc = Gc;
        _inst.Priority = Priority;
        _inst.JvmArgs = JvmArgs;
        _inst.JvmAutotune = Autotune && CanAutotune;
        _inst.Fullscreen = Fullscreen;
        _inst.PreferDiscreteGpu = DiscreteGpu;
        _inst.Width = int.TryParse(Width, out var w) && w > 0 ? w : null;
        _inst.Height = int.TryParse(Height, out var h) && h > 0 ? h : null;
        _inst.JavaPath = SelectedRuntime?.JavaExe;
        _instances.Save(_inst);
        _toasts.Show(OptionsExisted ? L.T("Настройки сохранены 🐸") : L.T("Настройки сохранены — Minecraft применит их при первом запуске 🐸"), ToastKind.Success);
    }

    private void WriteOptions()
    {
        var o = _opt;
        o.SetInt("graphicsMode", GraphicsMode);
        if (_legacyGraphics) o.SetBool("fancyGraphics", GraphicsMode > 0);
        o.SetInt("renderDistance", RenderDistance);
        o.SetInt("simulationDistance", SimulationDistance);
        o.SetString("renderClouds", McValues.Clouds[Clouds]);
        o.SetBool("entityShadows", EntityShadows);
        // До 1.19 ao — число 0..2, позже — true/false.
        if (o.Get("ao") is "0" or "1" or "2") o.SetInt("ao", SmoothLighting ? 2 : 0);
        else o.SetBool("ao", SmoothLighting);
        o.SetInt("particles", Particles);
        o.SetInt("biomeBlendRadius", BiomeBlend);
        o.SetInt("maxFps", MaxFps);
        o.SetBool("enableVsync", VSync);
        o.SetInt("mipmapLevels", Mipmap);
        o.SetDouble("entityDistanceScaling", EntityDistance / 100.0);
        o.SetDouble("fov", McValues.FovToOption(Fov));
        o.SetDouble("gamma", Brightness / 100.0);
        o.SetBool("bobView", BobView);
        o.SetDouble("screenEffectScale", ScreenEffects / 100.0);
        o.SetDouble("fovEffectScale", FovEffects / 100.0);
        o.SetInt("guiScale", GuiScale);
        o.SetDouble("chatScale", ChatScale / 100.0);
        o.SetDouble("chatOpacity", ChatOpacity / 100.0);
        o.SetDouble("chatWidth", ChatWidth / 100.0);
        o.SetBool("autoJump", AutoJump);
        o.SetInt("chatVisibility", ChatVisibility);
        o.SetString("lang", Language);
        o.SetDouble("mouseSensitivity", MouseSensitivity / 200.0);
        o.SetBool("invertYMouse", InvertMouse);
        o.SetBool("fullscreen", Fullscreen);
        foreach (var s in Sounds) o.SetDouble(s.Key, s.Value / 100.0);
        foreach (var k in Keys) o.Set(k.Key, k.Value);
    }

    private void WritePacksAndMods()
    {
        // options.txt: снизу вверх — первый в нашем списке имеет наивысший приоритет.
        var enabled = new List<string> { "vanilla" };
        enabled.AddRange(ResourcePacks.Where(p => p.Enabled).Reverse().Select(p => p.Id));
        foreach (var keep in _opt.GetResourcePacks().Where(p => !p.StartsWith("file/", StringComparison.Ordinal) && p != "vanilla"))
            enabled.Insert(1, keep); // встроенные паки (programmer_art, fabric) не теряем
        _opt.SetResourcePacks(enabled);
        _opt.Save();

        var game = _inst.GameDirectory;
        if (HasIris)
        {
            var file = Path.Combine(game, "config", "iris.properties");
            var props = ReadProperties(file);
            props["enableShaders"] = ShadersEnabled ? "true" : "false";
            if (SelectedShader is { Length: > 0 }) props["shaderPack"] = SelectedShader;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllLines(file, props.Select(p => $"{p.Key}={p.Value}"));
        }

        var sodiumFile = Path.Combine(game, "config", "sodium-options.json");
        if (SodiumOptions.Count > 0 && JsonNode.Parse(File.ReadAllText(sodiumFile)) is JsonObject root)
        {
            foreach (var s in SodiumOptions)
            {
                var parts = s.Path.Split('.');
                if (root[parts[0]] is JsonObject section) section[parts[1]] = s.Value;
            }
            File.WriteAllText(sodiumFile, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    // ================== Пресеты, сброс, экспорт ==================

    [RelayCommand]
    private void ApplyPreset(SettingsPreset? p)
    {
        if (p is null) return;
        GraphicsMode = p.Graphics;
        RenderDistance = p.RenderDistance;
        SimulationDistance = Math.Max(5, p.SimulationDistance);
        MaxFps = p.MaxFps;
        VSync = p.VSync;
        Clouds = Math.Max(0, Array.IndexOf(McValues.Clouds, p.Clouds));
        Particles = p.Particles;
        RamMax = p.RamMb;
        RamMin = Math.Min(1024, p.RamMb);
        EntityDistance = p.Kind == PresetKind.Performance ? 75 : 100;
        Mipmap = p.Kind == PresetKind.Performance ? 2 : 4;
        if (p.Kind == PresetKind.Performance && CanAutotune) { Gc = "g1"; Autotune = true; }
        if (HasIris) ShadersEnabled = p.Shaders && ShaderPacks.Count > 0;
        _toasts.Show(L.T($"Пресет «{p.Title}» применён — проверьте и нажмите «Сохранить»"), ToastKind.Info, 5);
    }

    [RelayCommand]
    private void ResetRecommended() => ApplyPreset(Presets.First(p => p.Kind == RecommendedKind));

    /// <summary>Сбросить всё: удалить options.txt — Minecraft создаст его заново со стандартными значениями.</summary>
    [RelayCommand]
    private void ResetAll()
    {
        var ok = System.Windows.MessageBox.Show(L.T("Сбросить все настройки Minecraft этой версии к стандартным (включая управление)?"),
            "Ribbix Launcher", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (ok != System.Windows.MessageBoxResult.Yes) return;
        if (File.Exists(_opt.Path)) File.Delete(_opt.Path);
        _opt = OptionsFile.Load(_inst.GameDirectory);
        LoadFromOptions();
        _toasts.Show(L.T("Настройки Minecraft сброшены"), ToastKind.Info);
    }

    [RelayCommand]
    private void ResetKeys()
    {
        foreach (var k in Keys) k.Value = McValues.DefaultKeys[k.Key];
    }

    /// <summary>Экспорт «моих настроек» одним файлом: графика, звук, управление, RAM и JVM.</summary>
    [RelayCommand]
    private void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"{_inst.Name}.ribbixpreset.json", Filter = L.T("Пресет Ribbix|*.ribbixpreset.json") };
        if (dlg.ShowDialog() != true) return;
        WriteOptions();
        var options = _opt.All.Where(kv => !kv.Key.StartsWith("lastServer") && kv.Key != "resourcePacks" && kv.Key != "incompatibleResourcePacks")
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        var preset = new JsonObject
        {
            ["format"] = "ribbixpreset/1",
            ["name"] = _inst.Name,
            ["options"] = JsonSerializer.SerializeToNode(options),
            ["ram_min_mb"] = RamMin, ["ram_max_mb"] = RamMax, ["gc"] = Gc, ["jvm_args"] = JvmArgs,
        };
        File.WriteAllText(dlg.FileName, preset.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        _toasts.Show(L.T("Пресет экспортирован — им можно поделиться 🐸"), ToastKind.Success);
    }

    [RelayCommand]
    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = L.T("Пресет Ribbix|*.ribbixpreset.json;*.frogpreset.json;*.json") };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(dlg.FileName)) as JsonObject;
            // frogpreset/1 — пресеты, сохранённые в FrogLauncher.
            if (root?["format"]?.GetValue<string>() is not ("ribbixpreset/1" or "frogpreset/1")) throw new InvalidDataException();
            foreach (var (key, value) in root["options"]!.AsObject())
                if (value is JsonValue v && v.TryGetValue(out string? s) && key.Length < 80) _opt.Set(key, s);
            LoadFromOptions();
            RamMax = Math.Min(root["ram_max_mb"]?.GetValue<int>() ?? RamMax, MaxRamMb);
            RamMin = Math.Min(root["ram_min_mb"]?.GetValue<int>() ?? RamMin, RamMax);
            Gc = root["gc"]?.GetValue<string>() is { } g && GcOptions.Any(o => o.Code == g) ? g : Gc;
            JvmArgs = root["jvm_args"]?.GetValue<string>() ?? JvmArgs;
            _toasts.Show(L.T("Пресет загружен — проверьте и нажмите «Сохранить»"), ToastKind.Info);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException or IOException)
        {
            _toasts.Show(L.T("Это не пресет Ribbix"), ToastKind.Error);
        }
    }

    // ================== Java ==================

    [RelayCommand]
    private void UseAutoJava() => SelectedRuntime = null;

    /// <summary>«Проверить»: java -version выбранной (или подходящей) Java.</summary>
    [RelayCommand]
    private async Task TestJavaAsync()
    {
        var exe = (SelectedRuntime ?? Runtimes.FirstOrDefault(r => r.Major == RequiredJava))?.JavaExe;
        if (exe is null)
        {
            JavaTestOutput = L.T($"Java {RequiredJava} не найдена — её скачает лаунчер при запуске или кнопкой ниже.");
            return;
        }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe.Replace("javaw.exe", "java.exe"), "-version")
            {
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            })!;
            JavaTestOutput = (await p.StandardError.ReadToEndAsync()).Trim();
            await p.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            JavaTestOutput = L.T("Не удалось запустить: ") + ex.Message;
        }
    }

    [RelayCommand]
    private async Task DownloadJavaAsync()
    {
        JavaTestOutput = L.T($"Скачиваем Java {RequiredJava}…");
        try
        {
            await _java.DownloadTemurinAsync(RequiredJava, null, CancellationToken.None);
            Runtimes.ReplaceWith(await _java.DetectAsync(force: true));
            JavaTestOutput = L.T($"Java {RequiredJava} установлена ☕");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or IOException or InvalidOperationException)
        {
            JavaTestOutput = L.T("Не удалось скачать: ") + ex.Message;
        }
    }

    // ================== Ресурспаки и папки ==================

    [RelayCommand]
    private void MovePackUp(PackItem? p) => Move(p, -1);

    [RelayCommand]
    private void MovePackDown(PackItem? p) => Move(p, +1);

    private void Move(PackItem? p, int delta)
    {
        if (p is null) return;
        var i = ResourcePacks.IndexOf(p);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= ResourcePacks.Count) return;
        ResourcePacks.Move(i, j);
    }

    [RelayCommand]
    private void OpenFolder(string name)
    {
        var dir = Path.Combine(_inst.GameDirectory, name);
        Directory.CreateDirectory(dir);
        Shell.Open(dir);
    }

    [RelayCommand]
    private void RefreshPacks() => LoadPacks();

    private static Dictionary<string, string> ReadProperties(string file) =>
        File.Exists(file)
            ? File.ReadAllLines(file).Where(l => l.Contains('=') && !l.TrimStart().StartsWith('#'))
                .Select(l => l.Split('=', 2)).GroupBy(p => p[0].Trim()).ToDictionary(g => g.Key, g => g.Last()[1].Trim())
            : new Dictionary<string, string>();

    private static JsonNode? Find(JsonObject root, string path)
    {
        JsonNode? node = root;
        foreach (var part in path.Split('.')) node = node?[part];
        return node;
    }
}

using System.ComponentModel;

namespace FrogLauncher.Core;

/// <summary>Локализация RU/EN. В XAML: {Binding [key], Source={x:Static core:Loc.I}}.</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc I { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;

    private string _lang = "ru";
    public string Language
    {
        get => _lang;
        set { _lang = value == "en" ? "en" : "ru"; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty)); }
    }

    public string this[string key] => (_lang == "en" ? En : Ru).TryGetValue(key, out var v) ? v : key;
    public static string T(string key) => I[key];

    private static readonly Dictionary<string, string> Ru = new()
    {
        ["nav_home"] = "Главная", ["nav_versions"] = "Каталог версий", ["nav_instances"] = "Мои версии",
        ["nav_java"] = "Java", ["nav_settings"] = "Настройки", ["nav_exit"] = "Выход",
        ["account"] = "Аккаунт", ["nickname"] = "Ник", ["login_offline"] = "Оффлайн", ["login_ms"] = "Microsoft",
        ["logout"] = "Выйти из аккаунта", ["not_logged"] = "Не выполнен вход", ["instance"] = "Версия",
        ["play"] = "ИГРАТЬ", ["stop"] = "СТОП", ["news"] = "Новости лаунчера", ["status"] = "Серверы Mojang",
        ["filter_release"] = "Релизы", ["filter_snapshot"] = "Снапшоты", ["filter_old"] = "Alpha / Beta",
        ["col_version"] = "Версия", ["col_type"] = "Тип", ["col_date"] = "Дата", ["col_size"] = "Размер",
        ["loader"] = "Модлоадер", ["loader_version"] = "Версия загрузчика", ["install"] = "Установить",
        ["refresh"] = "Обновить", ["cancel"] = "Отмена", ["new_instance"] = "Новая версия",
        ["save"] = "Сохранить", ["delete"] = "Удалить", ["folder"] = "Папка", ["mods"] = "Моды",
        ["export"] = "Экспорт", ["import"] = "Импорт", ["name"] = "Название", ["ram_min"] = "RAM мин (МБ)",
        ["ram_max"] = "RAM макс (МБ)", ["jvm_args"] = "JVM-аргументы", ["resolution"] = "Разрешение окна",
        ["fullscreen"] = "Полный экран", ["demo"] = "Демо-режим", ["java_path"] = "Java (пусто = авто)",
        ["rescan"] = "Найти Java", ["download_java"] = "Скачать Java", ["compat"] = "Совместимость",
        ["language"] = "Язык", ["theme"] = "Тема", ["game_folder"] = "Папка игры", ["proxy"] = "Прокси",
        ["update_repo"] = "Репозиторий обновлений (owner/repo)", ["ms_client"] = "Microsoft Client ID",
        ["check_updates"] = "Проверить обновления", ["skin"] = "Скин", ["pick_skin"] = "Выбрать PNG",
        ["upload_skin"] = "Загрузить скин", ["slim"] = "Тонкие руки (slim)", ["apply"] = "Применить",
        ["select_version"] = "Выберите версию", ["pick_instance"] = "Выберите версию",
        ["ready"] = "Готово к игре", ["running"] = "Игра запущена", ["crash_title"] = "Игра завершилась с ошибкой",
        ["last_lines"] = "Последние 50 строк лога:", ["close"] = "Закрыть", ["open_mclogs"] = "Лог на mclo.gs",
                ["ver_none"] = "Версия не выбрана", ["ver_missing"] = "⚠ Не установлена — скачается при запуске", ["ver_installed"] = "✔ Установлена",
        ["welcome_title"] = "Добро пожаловать!", ["welcome_text"] = "Войдите, чтобы начать играть. Это нужно сделать один раз.",
        ["hide_on_launch"] = "Скрывать лаунчер после запуска игры", ["close_instead"] = "Закрывать лаунчер вместо скрытия",
        ["reopen_after"] = "Открывать лаунчер снова после закрытия игры", ["show_log"] = "Показывать консоль игры на главной",
        ["autostart"] = "Запускать вместе с Windows", ["default_ram"] = "RAM по умолчанию для новых версий (МБ)",
        ["news_url"] = "Ссылка на новости лаунчера (необязательно)", ["open_game"] = "Папка игры", ["open_logs"] = "Папка логов",
        ["clear_cache"] = "Очистить кэш", ["reset_settings"] = "Сбросить настройки", ["repair"] = "Проверить файлы",
        ["auto_ram"] = "Авто-RAM", ["clone"] = "Копия", ["backup"] = "Бэкап миров", ["shortcut"] = "Ярлык", ["update_mods"] = "Обновить моды",
        ["server"] = "Сервер при запуске (host:port)", ["pre_cmd"] = "Команда перед запуском", ["post_cmd"] = "Команда после выхода",
        ["behavior"] = "Поведение", ["tools"] = "Инструменты", ["advanced"] = "Дополнительно (необязательно)", ["playtime"] = "Время в игре",
        ["login_hint"] = "Ник (3–16 символов)",
        ["loading"] = "Загрузка…", ["add_mods"] = "Добавить моды", ["create"] = "Создать", ["browse"] = "Обзор…",
        ["get_client_id"] = "Как получить", ["mod_remove"] = "Удалить мод", ["new_instance_title"] = "Новая версия",
        ["mc_version"] = "Версия Minecraft", ["mods_list"] = "Моды (галочка = включён)",
        ["nav_modcat"] = "Каталог модов", ["search"] = "Поиск", ["install_mod"] = "Установить мод", ["more"] = "Показать ещё",
        ["cf_key"] = "CurseForge API key", ["get_key"] = "Получить ключ", ["mod_search_hint"] = "Название мода (пусто = популярные)"
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["nav_home"] = "Home", ["nav_versions"] = "Version catalog", ["nav_instances"] = "My versions",
        ["nav_java"] = "Java", ["nav_settings"] = "Settings", ["nav_exit"] = "Exit",
        ["account"] = "Account", ["nickname"] = "Nickname", ["login_offline"] = "Offline", ["login_ms"] = "Microsoft",
        ["logout"] = "Sign out", ["not_logged"] = "Not signed in", ["instance"] = "Version",
        ["play"] = "PLAY", ["stop"] = "STOP", ["news"] = "Launcher news", ["status"] = "Mojang servers",
        ["filter_release"] = "Releases", ["filter_snapshot"] = "Snapshots", ["filter_old"] = "Alpha / Beta",
        ["col_version"] = "Version", ["col_type"] = "Type", ["col_date"] = "Date", ["col_size"] = "Size",
        ["loader"] = "Mod loader", ["loader_version"] = "Loader version", ["install"] = "Install",
        ["refresh"] = "Refresh", ["cancel"] = "Cancel", ["new_instance"] = "New version",
        ["save"] = "Save", ["delete"] = "Delete", ["folder"] = "Folder", ["mods"] = "Mods",
        ["export"] = "Export", ["import"] = "Import", ["name"] = "Name", ["ram_min"] = "RAM min (MB)",
        ["ram_max"] = "RAM max (MB)", ["jvm_args"] = "JVM arguments", ["resolution"] = "Window size",
        ["fullscreen"] = "Fullscreen", ["demo"] = "Demo mode", ["java_path"] = "Java (empty = auto)",
        ["rescan"] = "Scan for Java", ["download_java"] = "Download Java", ["compat"] = "Compatibility",
        ["language"] = "Language", ["theme"] = "Theme", ["game_folder"] = "Game folder", ["proxy"] = "Proxy",
        ["update_repo"] = "Updates repository (owner/repo)", ["ms_client"] = "Microsoft Client ID",
        ["check_updates"] = "Check for updates", ["skin"] = "Skin", ["pick_skin"] = "Choose PNG",
        ["upload_skin"] = "Upload skin", ["slim"] = "Slim arms", ["apply"] = "Apply",
        ["select_version"] = "Select a version", ["pick_instance"] = "Select a version",
        ["ready"] = "Ready to play", ["running"] = "Game is running", ["crash_title"] = "The game crashed",
        ["last_lines"] = "Last 50 log lines:", ["close"] = "Close", ["open_mclogs"] = "Log on mclo.gs",
                ["ver_none"] = "No version selected", ["ver_missing"] = "⚠ Not installed — will be downloaded on launch", ["ver_installed"] = "✔ Installed",
        ["welcome_title"] = "Welcome!", ["welcome_text"] = "Sign in to start playing. You only need to do this once.",
        ["hide_on_launch"] = "Hide the launcher after the game starts", ["close_instead"] = "Close the launcher instead of hiding it",
        ["reopen_after"] = "Reopen the launcher when the game closes", ["show_log"] = "Show the game console on the home screen",
        ["autostart"] = "Start with Windows", ["default_ram"] = "Default RAM for new versions (MB)",
        ["news_url"] = "Launcher news URL (optional)", ["open_game"] = "Game folder", ["open_logs"] = "Logs folder",
        ["clear_cache"] = "Clear cache", ["reset_settings"] = "Reset settings", ["repair"] = "Verify files",
        ["auto_ram"] = "Auto RAM", ["clone"] = "Duplicate", ["backup"] = "Back up worlds", ["shortcut"] = "Shortcut", ["update_mods"] = "Update mods",
        ["server"] = "Join server on launch (host:port)", ["pre_cmd"] = "Pre-launch command", ["post_cmd"] = "Post-exit command",
        ["behavior"] = "Behavior", ["tools"] = "Tools", ["advanced"] = "Advanced (optional)", ["playtime"] = "Play time",
        ["login_hint"] = "Nickname (3–16 characters)",
        ["loading"] = "Loading…", ["add_mods"] = "Add mods", ["create"] = "Create", ["browse"] = "Browse…",
        ["get_client_id"] = "How to get one", ["mod_remove"] = "Delete mod", ["new_instance_title"] = "New version",
        ["mc_version"] = "Minecraft version", ["mods_list"] = "Mods (checked = enabled)",
        ["nav_modcat"] = "Mod browser", ["search"] = "Search", ["install_mod"] = "Install mod", ["more"] = "Show more",
        ["cf_key"] = "CurseForge API key", ["get_key"] = "Get a key", ["mod_search_hint"] = "Mod name (empty = popular)"
    };
}

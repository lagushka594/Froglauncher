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
        ["dlg_ok"] = "ОК", ["dlg_yes"] = "Да", ["dlg_no"] = "Нет", ["dlg_error"] = "Ошибка", ["dlg_warn"] = "Внимание", ["dlg_confirm"] = "Подтверждение",
        ["nav_mods_installed"] = "Моды версии", ["nav_about"] = "О приложении", ["discord"] = "Discord", ["more_actions"] = "Подробнее",
        ["login_close"] = "Закрыть", ["switch_account"] = "Сменить аккаунт", ["mods_title"] = "Установленные моды", ["no_mods"] = "Модов пока нет — добавьте файлом или из каталога.",
        ["about_title"] = "FrogLauncher", ["about_creator"] = "Создатель: Frog Lab",
        ["about_text1"] = "FrogLauncher — удобный лаунчер для Minecraft: сборки, моды из Modrinth и CurseForge, все загрузчики и автоподбор Java в одном окне.",
        ["about_text2"] = "Мы — небольшая команда Frog Lab: любим Minecraft, лягушек и аккуратный софт. Лаунчер делается с душой, без рекламы и лишней возни. 🐸💚",
        ["about_text3"] = "Нашли ошибку или есть идея? Напишите нам в Discord или создайте issue на GitHub — читаем всё.",
        ["about_disclaimer"] = "Minecraft — торговая марка Mojang. Проект не связан с Mojang и Microsoft.", ["github"] = "GitHub",
        ["nav_priv"] = "Привилегии", ["priv_title"] = "Привилегии FrogLauncher", ["priv_status"] = "Ваш статус", ["priv_none"] = "нет привилегии",
        ["priv_buy"] = "Купить", ["priv_owned"] = "✔ Активно", ["priv_refresh"] = "Обновить статус",
        ["priv_price_note"] = "Разовая оплата, привилегия остаётся навсегда. Все привилегии поддерживают развитие лаунчера 💚",
        ["priv_howto_title"] = "Как купить",
        ["priv_howto"] = "1. Нажмите «Купить» у нужной привилегии — откроется наш Discord, а текст заявки уже скопируется в буфер обмена.\n2. Напишите в личные сообщения поддержке (ЛС в Discord) примерно так:\n    «Здравствуйте! Хочу купить привилегию <название>. Мой ник в Minecraft: <ник>. Тип аккаунта: Microsoft или Offline.»\n3. Поддержка подскажет способы оплаты и поможет со всем — не стесняйтесь спрашивать.\n4. После оплаты привилегия включится в лаунчере (нажмите «Обновить статус» или перезапустите лаунчер).\n5. Всем купившим в нашем Discord выдаётся роль с названием привилегии: от Tadpole (50 ₽) до Frog Legacy (700 ₽).",
        ["priv_msg_template"] = "Здравствуйте! Хочу купить привилегию {0} ({1} ₽).\nМой ник в Minecraft: {2}\nТип аккаунта: {3}\nПодскажите, пожалуйста, как оплатить.",
        ["priv_buy_dialog"] = "Привилегия «{0}» — {1} ₽.\n\nМы открыли наш Discord. Напишите поддержке в личные сообщения — текст заявки уже скопирован, просто вставьте его (Ctrl+V):\n\n{2}\n\nПоддержка поможет со всем. После оплаты вы получите привилегию в лаунчере и роль в Discord.",
        ["tier_tadpole_perks"] = "• Бейдж «Tadpole» рядом с ником\n• Роль «Tadpole» в Discord\n• Эксклюзивная тема «Mint»\n• Свой фон лаунчера (любая ваша картинка)",
        ["tier_froglet_perks"] = "• Всё из Tadpole\n• Роль «Froglet» в Discord\n• Эксклюзивная тема «Sunset»\n• Авто-бэкап миров перед каждым запуском\n• Статистика игры: время по версиям",
        ["tier_frogplus_perks"] = "• Всё из Froglet\n• Роль «Frog+» в Discord\n• Эксклюзивная тема «Aurora»\n• Авто-обновление модов при запуске\n• Быстрая загрузка (16 потоков)\n• Профили модов: сохраняйте и переключайте наборы",
        ["tier_legacy_perks"] = "• Всё из Frog+\n• Роль «Frog Legacy» в Discord\n• Золотая тема «Gold» и золотой бейдж\n• Свой текст в статусе Discord (Играет в FrogLauncher)\n• Ранний доступ: бета-версии лаунчера",
        ["priv_my_perks"] = "Мои возможности", ["priv_bg"] = "Свой фон лаунчера", ["priv_choose"] = "Выбрать картинку", ["priv_reset"] = "Сбросить",
        ["priv_autobackup"] = "Авто-бэкап миров перед запуском (хранится 5 последних)", ["priv_autoupdate"] = "Авто-обновление модов при запуске",
        ["priv_fast"] = "Быстрая загрузка (16 потоков)", ["priv_beta"] = "Получать бета-версии лаунчера", ["priv_rpc"] = "Свой текст статуса Discord (пусто — стандартный)",
        ["priv_stats_title"] = "Статистика игры", ["priv_stats_fmt"] = "Всего в игре: {0}  •  Версий: {1}  •  Любимая версия: {2}",
        ["priv_profiles"] = "Профили модов", ["priv_profile_name"] = "Введите название профиля.", ["priv_profile_saved"] = "Профиль модов сохранён",
        ["priv_profile_applied"] = "Профиль модов применён", ["priv_profile_new"] = "Название нового профиля", ["priv_save_profile"] = "Сохранить набор", ["priv_apply_profile"] = "Применить",
        ["priv_admin_panel"] = "Панель администратора", ["priv_admin_total"] = "Выдано привилегий", ["priv_admin_hint"] = "Привилегии выдаются и отзываются в консоли NewsPublisher (пункты 5–7).",
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
        ["dlg_ok"] = "OK", ["dlg_yes"] = "Yes", ["dlg_no"] = "No", ["dlg_error"] = "Error", ["dlg_warn"] = "Warning", ["dlg_confirm"] = "Confirm",
        ["nav_mods_installed"] = "Version mods", ["nav_about"] = "About", ["discord"] = "Discord", ["more_actions"] = "More",
        ["login_close"] = "Close", ["switch_account"] = "Switch account", ["mods_title"] = "Installed mods", ["no_mods"] = "No mods yet — add a file or install from the catalog.",
        ["about_title"] = "FrogLauncher", ["about_creator"] = "Created by Frog Lab",
        ["about_text1"] = "FrogLauncher is a handy Minecraft launcher: modpacks, mods from Modrinth and CurseForge, every loader and automatic Java in one window.",
        ["about_text2"] = "We are Frog Lab, a small team that loves Minecraft, frogs and tidy software. The launcher is made with care — no ads, no fuss. 🐸💚",
        ["about_text3"] = "Found a bug or have an idea? Tell us on Discord or open an issue on GitHub — we read everything.",
        ["about_disclaimer"] = "Minecraft is a trademark of Mojang. This project is not affiliated with Mojang or Microsoft.", ["github"] = "GitHub",
        ["nav_priv"] = "Perks", ["priv_title"] = "FrogLauncher perks", ["priv_status"] = "Your status", ["priv_none"] = "no perk",
        ["priv_buy"] = "Buy", ["priv_owned"] = "✔ Active", ["priv_refresh"] = "Refresh status",
        ["priv_price_note"] = "One-time payment, the perk is yours forever. Every perk supports the launcher's development 💚",
        ["priv_howto_title"] = "How to buy",
        ["priv_howto"] = "1. Press “Buy” on the perk you want — our Discord opens and the request text is already copied to your clipboard.\n2. Send our support a private message (Discord DM) like this:\n    “Hello! I want to buy the <name> perk. My Minecraft nickname: <nick>. Account type: Microsoft or Offline.”\n3. Support will tell you the payment options and help with everything — feel free to ask.\n4. After payment the perk turns on in the launcher (press “Refresh status” or restart it).\n5. Everyone who buys gets a Discord role named after the perk: from Tadpole (50 RUB) to Frog Legacy (700 RUB).",
        ["priv_msg_template"] = "Hello! I want to buy the {0} perk ({1} RUB).\nMy Minecraft nickname: {2}\nAccount type: {3}\nPlease tell me how to pay.",
        ["priv_buy_dialog"] = "Perk “{0}” — {1} RUB.\n\nWe opened our Discord. Send support a private message — the request text is already copied, just paste it (Ctrl+V):\n\n{2}\n\nSupport will help with everything. After payment you get the perk in the launcher and a role on Discord.",
        ["tier_tadpole_perks"] = "• “Tadpole” badge next to your name\n• “Tadpole” role on Discord\n• Exclusive “Mint” theme\n• Your own launcher background (any picture)",
        ["tier_froglet_perks"] = "• Everything from Tadpole\n• “Froglet” role on Discord\n• Exclusive “Sunset” theme\n• World auto-backup before every launch\n• Play statistics per version",
        ["tier_frogplus_perks"] = "• Everything from Froglet\n• “Frog+” role on Discord\n• Exclusive “Aurora” theme\n• Mods auto-update on launch\n• Fast downloads (16 threads)\n• Mod profiles: save and switch mod sets",
        ["tier_legacy_perks"] = "• Everything from Frog+\n• “Frog Legacy” role on Discord\n• Golden “Gold” theme and golden badge\n• Your own Discord status text (Playing FrogLauncher)\n• Early access: launcher beta builds",
        ["priv_my_perks"] = "My perks", ["priv_bg"] = "Custom launcher background", ["priv_choose"] = "Choose picture", ["priv_reset"] = "Reset",
        ["priv_autobackup"] = "World auto-backup before launch (keeps the last 5)", ["priv_autoupdate"] = "Auto-update mods on launch",
        ["priv_fast"] = "Fast downloads (16 threads)", ["priv_beta"] = "Get launcher beta builds", ["priv_rpc"] = "Custom Discord status text (empty = default)",
        ["priv_stats_title"] = "Play statistics", ["priv_stats_fmt"] = "Total played: {0}  •  Versions: {1}  •  Favourite: {2}",
        ["priv_profiles"] = "Mod profiles", ["priv_profile_name"] = "Enter a profile name.", ["priv_profile_saved"] = "Mod profile saved",
        ["priv_profile_applied"] = "Mod profile applied", ["priv_profile_new"] = "New profile name", ["priv_save_profile"] = "Save set", ["priv_apply_profile"] = "Apply",
        ["priv_admin_panel"] = "Admin panel", ["priv_admin_total"] = "Perks granted", ["priv_admin_hint"] = "Perks are granted and revoked in the NewsPublisher console (items 5–7).",
        ["loading"] = "Loading…", ["add_mods"] = "Add mods", ["create"] = "Create", ["browse"] = "Browse…",
        ["get_client_id"] = "How to get one", ["mod_remove"] = "Delete mod", ["new_instance_title"] = "New version",
        ["mc_version"] = "Minecraft version", ["mods_list"] = "Mods (checked = enabled)",
        ["nav_modcat"] = "Mod browser", ["search"] = "Search", ["install_mod"] = "Install mod", ["more"] = "Show more",
        ["cf_key"] = "CurseForge API key", ["get_key"] = "Get a key", ["mod_search_hint"] = "Mod name (empty = popular)"
    };
}

# 🐸 FrogLauncher

Неофициальный лаунчер Minecraft с открытым кодом: ванилла, Fabric, Quilt, Forge (1.7.10 → 1.21+) и NeoForge,
каталог модов Modrinth, подписки на функции лаунчера (покупка через тикет в Discord) и выдача тарифов из FrogNews.

> FrogLauncher не является продуктом Mojang/Microsoft и не одобрен ими. Minecraft — товарный знак Mojang AB.
> Подписка даёт доступ только к функциям лаунчера; **игра запускается на любом тарифе**, даже без аккаунта.

Исходный код: https://github.com/lagushka594/Froglauncher

---

## 1. Архитектура

```
Froglauncher/
├─ FrogLauncher.sln
├─ Directory.Build.props / Directory.Packages.props   — общие настройки, версии NuGet
├─ assets/
│  ├─ atlas/            — исходные атласы (3 листа)
│  ├─ assets.map.json   — координаты каждого ассета на атласе
│  ├─ out/{installer,ui,main}/*.png|ico — нарезанные ассеты (их использует UI)
│  └─ discord/          — картинки для Discord Rich Presence
├─ src/
│  ├─ Frog.Shared/                 — DTO, ключи тиров/фич, маскирование PII, JSON snake_case
│  ├─ Frog.Api/                    — ASP.NET Core 8 + PostgreSQL + EF Core
│  │  ├─ Data/ (Entities, FrogDbContext, Migrations/)
│  │  ├─ Services/ (TierCatalog, JwtService, EntitlementService, BillingService,
│  │  │            ProrationCalculator, AdminService, AccountService, SubscriptionLifecycleWorker)
│  │  ├─ Payments/ (IPaymentProvider, YooKassaProvider, PaddleProvider)
│  │  ├─ Controllers/ (auth, tiers, entitlements, billing, admin)
│  │  └─ tiers.json                — единственный источник цен
│  ├─ FrogLauncher/                — WPF .NET 8, MVVM (CommunityToolkit), DI (Generic Host)
│  │  ├─ Core/ (пути, DPAPI-хранилище, хэши)
│  │  ├─ Models/ (version.json Mojang, инстанс, аккаунты)
│  │  ├─ Services/ (Download, Version, Java, ModLoader, Launch, GameAuth, FrogApiClient,
│  │  │            EntitlementService, Instance, DiscordPresence, Theme, UrlScheme/SingleInstance)
│  │  ├─ ViewModels/ + Views/ (Главная, Каталог, Java, Подписка, Профиль, Настройки, Помощь)
│  │  └─ Themes/Theme.xaml
│  ├─ FrogNews.Subscriptions/      — модуль «Подписки» для существующего FrogNews (WPF)
│  └─ FrogLauncher.Installer/      — установщик FrogLauncherSetup.exe (.NET Framework 4.8)
├─ samples/FrogNews.DevHost/       — мини-«FrogNews» для проверки модуля без вашего приложения
├─ tests/
│  ├─ Frog.Api.Tests/              — unit + сквозные сценарии подписок (WebApplicationFactory)
│  └─ FrogLauncher.Tests/          — офлайн-кэш, команда запуска, Java, maven, deeplink
├─ tools/
│  ├─ AssetCutter/                 — нарезка атласов → PNG/ICO
│  ├─ LaunchSmoke/                 — реальная установка и запуск игры без UI
│  └─ *.ps1                        — скриншоты окон, UI Automation, картинки для Discord
├─ build/build.ps1                 — релизная сборка
└─ docs/ (schema.sql, TESTING.md)
```

**Почему WPF, а не Avalonia.** Лаунчер завязан на Windows: DPAPI, реестр (поиск Java, URL-схема `froglauncher://`),
Win32-процессы. FrogNews уже на WPF — общие стили, навыки и инструменты. Avalonia имеет смысл, когда понадобятся macOS/Linux:
сервисный слой (`Services/`) от WPF не зависит и переносится как есть.

**Установщик на .NET Framework 4.8**, потому что он встроен в Windows 10/11. Установщику не нужен свой рантайм,
поэтому его размер ≈ размеру лаунчера. Ставит в `%LocalAppData%\Programs\FrogLauncher` без прав администратора.

### Как связаны части

```
FrogLauncher ──JWT aud=froglauncher──► Frog.Api ◄──JWT aud=frognews (role=superadmin)── FrogNews
      │  ▲                               │  ▲
      │  └── froglauncher://billing/...  │  └── вебхуки YooKassa / Paddle
      └── браузер → страница оплаты ─────┘
```

---

## 2. Тиры и правила

`src/Frog.Api/tiers.json` проверяется при старте API: цена 50–700 ₽/мес, год = 10 × месяц, `admin` скрыт и без цены.
Ссылки на другие тиры в `entitlements` (`"frog_plus"`) разворачиваются рекурсивно.

| Ситуация | Что происходит |
|---|---|
| Неудачное продление | `past_due`, **grace 7 дней** с флагом `in_grace` → затем `tadpole` |
| Отмена автопродления | Фичи до конца оплаченного периода → `tadpole` (без grace) |
| Апгрейд в том же периоде | Доплата = (новая − старая цена) × доля оставшегося времени, дата окончания сохраняется |
| Апгрейд месяц → год | Новый период с сегодняшнего дня, остаток старого идёт в зачёт |
| Понижение тарифа | Через отмену автопродления; после окончания периода оформляется новый тариф |
| Покупка (по умолчанию) | «Оформить» копирует заказ и открывает Discord → тикет → superadmin в FrogNews жмёт «Выдать подписку» |
| Повторная выдача того же тарифа | Срок продлевается от текущей даты окончания; другой тариф заменяет прежний |
| Ручная подписка закончилась | Grace 7 дней с баннером «Продлите» → затем `tadpole` |
| Выдача Admin | Только FrogNews, только роль `superadmin`, причина обязательна, запись в аудит |
| Отзыв Admin | Отзыв всех refresh-токенов + инкремент `TokenVersion` → старый access-токен сразу отклоняется |
| Сервер недоступен | Лаунчер работает по кэшу `entitlements.dat` (DPAPI) до 7 дней и показывает баннер |

Лимит версий (инстансов): **Tadpole — 25**, Froglet — 50, Frog+ и выше — без ограничений.
Модлоадеры и каталог модов — бесплатно на любом тарифе.

Карточки тарифов видны всегда: если сервер недоступен, лаунчер показывает последнюю сохранённую копию `/tiers`,
а при первом запуске — `tiers.json`, вшитый в exe (тот же файл, что на сервере).

---

## 3. Сборка и запуск

Нужны: Windows 10/11, .NET SDK 8, для API — PostgreSQL 14+.

```powershell
# всё решение + тесты
dotnet build FrogLauncher.sln
dotnet test FrogLauncher.sln

# релиз: artifacts\FrogLauncherSetup.exe и artifacts\api\
powershell -ExecutionPolicy Bypass -File build\build.ps1
```

### Backend (Frog.Api)

Секреты — только в User Secrets или переменных окружения:

```powershell
cd src\Frog.Api
dotnet user-secrets set "ConnectionStrings:Frog" "Host=localhost;Database=frog;Username=frog;Password=..."
dotnet user-secrets set "Jwt:SigningKey"        "<случайные 48+ символов>"
dotnet user-secrets set "Jwt:ServiceSigningKey" "<другие случайные 48+ символов>"
dotnet user-secrets set "YooKassa:ShopId"        "..."
dotnet user-secrets set "YooKassa:SecretKey"     "..."
dotnet user-secrets set "YooKassa:WebhookSecret" "<случайная строка>"
dotnet user-secrets set "Paddle:ApiKey"          "..."
dotnet user-secrets set "Paddle:WebhookSecret"   "pdl_ntfset_..."
dotnet user-secrets set "Paddle:Prices:frog_plus:month" "pri_..."   # и так для каждого тира/периода

dotnet tool restore
dotnet ef database update            # или: dotnet run -- migrate
$env:FROG_STAFF_PASSWORD = "<пароль superadmin>"; dotnet run -- seed-staff boss superadmin
dotnet run                           # http://localhost:5080
```

В production то же самое задаётся переменными окружения: `ConnectionStrings__Frog`, `Jwt__SigningKey`, `YooKassa__SecretKey` и т.д.
SQL-скрипт схемы: `docs/schema.sql`.

**Без PostgreSQL (только для разработки):** `ConnectionStrings__Frog=InMemory` + `ASPNETCORE_ENVIRONMENT=Development`.
Переменная `FROG_DEV_STAFF=boss:superadmin:пароль` создаст сотрудника FrogNews при старте.

**Уже есть свой backend с `/auth/login`?** Перенесите из `Frog.Api` сервисы, контроллеры и миграцию. Свой логин оставьте,
но в access-токен добавьте claims `tier`, `entitlements[]`, `tv` (см. `JwtService.CreateAccessToken`), а refresh-токены
храните хэшем и ротируйте. Таблица `staff_accounts` нужна, только если у FrogNews нет своей таблицы сотрудников.

### Лаунчер

`src/FrogLauncher/appsettings.json` (рядом с exe):

| Ключ | Значение |
|---|---|
| `ApiBaseUrl` | адрес Frog.Api |
| `MicrosoftClientId` | `577bb56b-e06f-47d3-98cb-ebb21dacf055` |
| `DiscordApplicationId` | `1564103277010886746` |
| `SubscriptionSource` | `discord` — тариф по ролям Discord (без своего сервера, по умолчанию) или `server` — Frog.Api |
| `DiscordGuildId`, `DiscordRoles` | ID вашего сервера и соответствие «ID роли → тариф» (см. раздел 4) |
| `DiscordRedirectPort` | порт возврата из браузера при входе через Discord (`53134`) |
| `PurchaseMode` | `discord` (тикет в Discord, по умолчанию) или `checkout` (YooKassa/Paddle) |
| `DiscordPurchaseUrl` | `https://discord.gg/u6zJZ7BzU5` |
| `GitHubUrl`, `OfferUrl`, `RefundPolicyUrl` | ссылки в «Помощи» и «Подписке» |

Любой ключ можно переопределить переменной `FROGLAUNCHER_Launcher__ApiBaseUrl=...`.
Данные лежат в `%AppData%\FrogLauncher`: `game/`, `instances/{id}/instance.json`, `runtime/` (Java),
`session.dat`, `accounts.dat`, `entitlements.dat` (все `.dat` зашифрованы DPAPI), `logs/`.

**Вход через Microsoft.** Используется Device Code Flow (`XboxLive.signin offline_access`, tenant `consumers`).
В Azure (Microsoft Entra → App registrations) у приложения должны быть включены **Allow public client flows** и типы аккаунтов
*Personal Microsoft accounts*. Важно: Minecraft Services принимают токены только от Client ID,
**одобренных Mojang** (форма «Minecraft: Java Edition — App registration»). Пока одобрения нет, вход дойдёт до Xbox,
а `login_with_xbox` вернёт 403. Лаунчер покажет понятную ошибку, оффлайн-режим продолжит работать.

**Discord Rich Presence.** Лаунчер показывает «В лаунчере / Смотрит тарифы», «Готовит 1.20.1 · 42%»,
«Устанавливает Java 17», «Играет в Minecraft 1.20.1 · Fabric» с таймером и кнопку на GitHub.
В Discord Developer Portal → приложение `1564103277010886746` → **Rich Presence → Art Assets** загрузите файлы из
`assets/discord/` под их именами: `frog_logo`, `playing`, `download`, `java` (пересобрать: `tools\Make-DiscordAssets.ps1`).
Отключается в «Настройках».

### FrogNews

Модуль `src/FrogNews.Subscriptions` подключается в существующее приложение:

```csharp
services.AddFrogNewsSubscriptions(new Uri("https://api.froglauncher.example/"));

// после входа сотрудника в FrogNews — получить service-токен (audience=frognews):
await sp.GetRequiredService<ServiceTokenProvider>().LoginAsync(login, password, ct);

// пункт меню «Подписки» — только для superadmin (роль из подписанного сервером токена):
subscriptionsMenuItem.Visibility = SubscriptionsMenu.IsVisible(tokens) ? Visibility.Visible : Visibility.Collapsed;

// содержимое раздела:
host.Content = new SubscriptionsView { DataContext = sp.GetRequiredService<SubscriptionsViewModel>() };
```

Пока модуль не встроен в ваше приложение, можно пользоваться готовым **`FrogNews.exe`** (`samples/FrogNews.DevHost`,
собирается `build.ps1` в `artifacts\FrogNews`). Адрес backend — в `FrogNews.settings.json` рядом с exe
(или переменная `FROGNEWS_API`).

### Сборки (модпаки Modrinth)

Пункт меню **«Сборки»**: поиск модпаков Modrinth с фильтром по модлоадеру и версии игры → «Установить».
Лаунчер скачивает `.mrpack`, читает `modrinth.index.json` и создаёт новую версию с нужными Minecraft и модлоадером
(Fabric / Quilt / Forge / NeoForge), скачивает файлы с проверкой SHA1 только с разрешённых форматом хостов
(cdn.modrinth.com, github.com, raw.githubusercontent.com, gitlab.com), распаковывает `overrides/` и `client-overrides/`.
Серверные файлы (`env.client = unsupported`) пропускаются, пути с `../` отклоняются. Сборка занимает слот лимита версий;
если установка оборвалась — недокачанная версия удаляется.

### Вход в FrogNews без сервера

В `FrogNews.settings.json` рядом с `FrogNews.exe` можно задать локальные учётки (`LocalStaff`): логин, роль и
**PBKDF2-SHA256 хэш** пароля (открытого пароля нет ни в файле, ни в exe). Такой вход работает без backend; раздел
«Подписки» при этом только сообщает, что сервер недоступен (в режиме ролей Discord он и не нужен). Если backend доступен —
FrogNews в фоне получит и service-токен. Файл с вашими учётками в git не попадает (`.gitignore`), шаблон —
`FrogNews.settings.example.json`.

### Каталог модов

«Каталог» → версия с модлоадером → «🔍 Каталог модов»: поиск по Modrinth с фильтром по версии игры и модлоадеру
(для Quilt подходят и моды Fabric), установка с проверкой SHA1, обязательные зависимости ставятся сами
(например, Iris → Sodium). Установленные моды — списком с удалением; добавленные вручную jar тоже видны.

---

## 4. Покупка

### Тариф по ролям Discord — без своего сервера (по умолчанию)

`SubscriptionSource: "discord"`. Backend и FrogNews для подписок **не нужны**: тариф определяется ролью игрока на вашем Discord-сервере.

1. Игрок жмёт «Оформить» → заказ копируется, открывается https://discord.gg/u6zJZ7BzU5 → тикет → оплата.
2. Модератор выдаёт игроку роль (Froglet, Frog+, …). Снять роль — тариф снимается.
3. Игрок в лаунчере: «Профиль» → **«Войти через Discord»** (один раз) → «Подписка» → «⟳ Обновить».
   Дальше лаунчер сам проверяет роли при запуске и раз в 6 часов; без сети работает по кэшу до 7 дней.

**Настройка Discord Developer Portal** (приложение `1564103277010886746`):
- OAuth2 → **Redirects** → добавить `http://127.0.0.1:53134/callback` (порт = `DiscordRedirectPort`).
- OAuth2 → включить **Public Client** (вход без client secret — секрет в лаунчере не хранится; используется PKCE).

**Настройка `appsettings.json` лаунчера** (Discord → Настройки → Расширенные → «Режим разработчика»):
```json
"DiscordGuildId": "ID сервера (ПКМ по серверу → Копировать ID сервера)",
"DiscordRoles": {
  "ID роли Froglet":     "froglet",
  "ID роли Frog+":       "frog_plus",
  "ID роли Frog Pro":    "frog_pro",
  "ID роли Frog Legend": "frog_legend",
  "ID роли админов":     "admin"
}
```
ID роли: Настройки сервера → Роли → ПКМ по роли → «Копировать ID роли». Если у игрока несколько ролей —
включается самый высокий тариф. Лаунчер запрашивает только scopes `identify` и `guilds.members.read`.

> Честно о безопасности: без своего сервера проверка ролей выполняется на компьютере игрока — опытный пользователь
> может её обойти (как и любую локальную проверку). Для косметических функций лаунчера это приемлемо;
> если нужна защита платных серверных функций (облачные бэкапы и т.п.) — используйте режим `server` с Frog.Api.

### Через свой сервер и FrogNews (`SubscriptionSource: "server"`)

Тикет в Discord — так же, но тариф выдаёт сотрудник в FrogNews:

1. Игрок жмёт «Оформить» на карточке → в буфер копируется заказ (аккаунт FrogLauncher, игровой ник, тариф, период, сумма)
   и открывается https://discord.gg/u6zJZ7BzU5.
2. Игрок создаёт тикет, вставляет заказ и оплачивает удобным вам способом.
3. Сотрудник с ролью `superadmin` открывает FrogNews → «Подписки» → находит ник → **«Выдать подписку»**
   → тариф, месяц/год, номер тикета. Повторная выдача продлевает срок, снятие — «Снять подписку» (возврат).
4. Лаунчер подхватит тариф при «⟳ Обновить» или автоматически (раз в 6 часов). Чтобы тариф было к чему привязать,
   игрок должен зарегистрировать аккаунт FrogLauncher в «Профиле» (это нужно и backend'у: `ApiBaseUrl`).

API: `POST /admin/subscriptions/grant {user_id, tier_slug, period, reason}`, `POST /admin/subscriptions/revoke {user_id, reason}`.
В аудите и CSV видно, кто, кому, какой тариф и по какому тикету выдал. Сумма по прайсу попадает в историю платежей пользователя.

### Онлайн-оплата (`PurchaseMode=checkout`)

Деньги считает только сервер. Лаунчер получает `payment_url` из `POST /billing/checkout` и открывает его в браузере.
После оплаты страница `GET /billing/return` перенаправляет на `froglauncher://billing/success?sub_id=…`.
Лаунчер опрашивает `/entitlements` до 60 секунд и показывает «Подписка активирована 🐸».

### YooKassa (Россия)

1. Личный кабинет → Интеграция → HTTP-уведомления → URL:
   `https://api.froglauncher.example/billing/webhook/yookassa?token=<YooKassa:WebhookSecret>`
   События: `payment.succeeded`, `payment.canceled`.
2. Включите **автоплатежи** (сохранение карты) в договоре с YooKassa — без этого рекуррентные списания недоступны.
3. Продления списывает сам backend: воркер за 24 часа до конца периода делает платёж по `payment_method_id`.
   При отказе карты включается grace на 7 дней, за это время делается до 3 повторных попыток.

> ⚠️ **YooKassa не подписывает уведомления HMAC** — такой возможности в её API нет. Поэтому защита построена в три слоя:
> секрет в URL (сравнение за постоянное время) → IP отправителя из официального списка YooKassa →
> **статус платежа берётся повторным `GET /payments/{id}` с ключами магазина**, а не из тела уведомления.
> Подделать третий шаг без секретного ключа нельзя. За nginx укажите его IP в `KnownProxies`, иначе проверка IP увидит адрес прокси.

### Paddle (весь мир)

1. Создайте Products/Prices для каждого тира и периода и пропишите `Paddle:Prices:{tier}:{month|year}`.
2. Checkout settings → **Default payment link** (без него Paddle не выдаёт `checkout.url`).
3. Notifications → новый destination: `https://api.froglauncher.example/billing/webhook/paddle`, события
   `transaction.completed`, `transaction.payment_failed`, `subscription.created/updated/activated/past_due/canceled`.
   Secret key → `Paddle:WebhookSecret`.
4. Подпись `Paddle-Signature` (HMAC-SHA256 от `ts:body`) проверяется обязательно, старше 5 минут — отклоняется.
   Продления, dunning и прорейт при апгрейде (`prorated_immediately`) делает сам Paddle.

Идемпотентность: уникальные `(provider, event_id)` в `webhook_events`, `(provider, provider_payment_id)` в `payments`,
`(provider, provider_sub_id)` в `subscriptions`. Повторная доставка ничего не меняет.

---

## 5. Ассеты

Атласы из `assets/atlas` режутся по `assets/assets.map.json`. Фон (тёмный или «шахматка») удаляется заливкой от краёв,
затем картинка обрезается по содержимому:

```powershell
dotnet run --project tools\AssetCutter -- assets\assets.map.json assets\atlas assets\out --sheet
```

`--sheet` дополнительно создаёт `assets/out/_contact_sheet.png` для визуальной проверки. Если ассет обрезан неточно,
поправьте его `box` в карте.

---

## 6. Юридическое

- Не продаём доступ к игре и не называем лаунчер «официальным». Дисклеймер есть в «Подписке», «Помощи» и установщике.
- Публичная оферта и политика возврата — ссылки `OfferUrl`/`RefundPolicyUrl` (страницы нужно опубликовать).
- Оффлайн-режим позволяет играть без лицензии. Это стандартная функция многих лаунчеров, но при монетизации
  обсудите её с юристом; при необходимости можно требовать один вход через Microsoft перед оффлайн-игрой.
- Логи не содержат PII: email маскируется (`i***v@m***.ru`), токены вырезаются из лога игры.

Подробный чек-лист — [docs/TESTING.md](docs/TESTING.md).

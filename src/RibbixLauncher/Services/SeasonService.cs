using System.Globalization;
using System.IO;
using System.Text.Json;
using RibbixLauncher.Core;

namespace RibbixLauncher.Services;

public enum Season { Spring, Summer, Autumn, Winter, NewYear }

/// <summary>
/// Текущее время лаунчера. Для проверки сезонов без перевода системных часов:
/// RIBBIX_DATE=2026-12-31T23:59:30 — время идёт от этой точки.
/// </summary>
public static class AppClock
{
    private static readonly TimeSpan Offset = Environment.GetEnvironmentVariable("RIBBIX_DATE") is { Length: > 0 } s &&
                                              DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var fake)
        ? fake - DateTime.Now
        : TimeSpan.Zero;

    public static DateTime Now => DateTime.Now + Offset;
    public static DateOnly Today => DateOnly.FromDateTime(Now);
}

/// <summary>Сезоны: даты, ключи тем, новогодний период (20 декабря — 10 января).</summary>
public static class Seasons
{
    public const string AutoKey = "season";

    public static Season For(DateOnly d) => d switch
    {
        { Month: 12, Day: >= 20 } or { Month: 1, Day: <= 10 } => Season.NewYear,
        { Month: >= 3 and <= 5 } => Season.Spring,
        { Month: >= 6 and <= 8 } => Season.Summer,
        { Month: >= 9 and <= 11 } => Season.Autumn,
        _ => Season.Winter,
    };

    public static string KeyOf(Season s) => s switch
    {
        Season.Spring => "season-spring",
        Season.Summer => "season-summer",
        Season.Autumn => "season-autumn",
        Season.Winter => "season-winter",
        _ => "season-newyear",
    };

    public static Season? FromKey(string key) => key switch
    {
        "season-spring" => Season.Spring,
        "season-summer" => Season.Summer,
        "season-autumn" => Season.Autumn,
        "season-winter" => Season.Winter,
        "season-newyear" => Season.NewYear,
        _ => null,
    };

    /// <summary>Пасхалка: зимняя тема посреди лета.</summary>
    public static bool IsWinterInSummer(string key, DateOnly today) => key == "season-winter" && today.Month == 7;
}

public sealed class NewYearState
{
    /// <summary>«2026» — праздники с декабря 2026 по январь 2027.</summary>
    public int SeasonYear { get; set; }
    public List<DateOnly> LoginDays { get; set; } = new();
    public List<DateOnly> GiftDays { get; set; } = new();
    public int CouponPercent { get; set; }
    public bool FireworksShown { get; set; }
}

/// <summary>
/// Новогодние радости: подарок за вход раз в день (копится купон на скидку), ёлка украшается по дням заходов
/// (5 — гирлянда, 10 — звезда), фейерверк 31 декабря 23:59–00:01. Состояние — newyear.json, сбрасывается каждый сезон.
/// </summary>
public sealed class NewYearService
{
    public const int FirstGift = 3, GiftStep = 1, MaxCoupon = 15;
    public const int GarlandDays = 5, StarDays = 10;

    private static string File => Path.Combine(LauncherPaths.Root, "newyear.json");
    private NewYearState? _state;

    public event Action? Changed;

    public static int SeasonYearOf(DateOnly d) => d.Month == 1 ? d.Year - 1 : d.Year;

    public NewYearState State
    {
        get
        {
            if (_state is not null) return _state;
            try
            {
                if (System.IO.File.Exists(File)) _state = JsonSerializer.Deserialize<NewYearState>(System.IO.File.ReadAllText(File), Json.Options);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
            return _state ??= new NewYearState();
        }
    }

    /// <summary>Для тестов: работа с состоянием без файла.</summary>
    internal NewYearService(NewYearState state) => _state = state;
    public NewYearService() { }

    private void Reset(DateOnly today)
    {
        var year = SeasonYearOf(today);
        if (State.SeasonYear == year) return;
        _state = new NewYearState { SeasonYear = year };
    }

    public bool IsActive(DateOnly today) => Seasons.For(today) == Season.NewYear;

    /// <summary>Отмечаем день, когда лаунчер открывали (для украшения ёлки).</summary>
    public void RecordLogin(DateOnly today)
    {
        if (!IsActive(today)) return;
        Reset(today);
        if (State.LoginDays.Contains(today)) return;
        State.LoginDays.Add(today);
        Save();
    }

    public bool CanClaim(DateOnly today) => IsActive(today) && !(State.SeasonYear == SeasonYearOf(today) && State.GiftDays.Contains(today));

    /// <summary>Подарок дня: первый — 3%, дальше +1% за каждый день, максимум 15%. Возвращает новый купон.</summary>
    public int ClaimGift(DateOnly today)
    {
        if (!CanClaim(today)) return State.CouponPercent;
        Reset(today);
        State.GiftDays.Add(today);
        State.CouponPercent = Math.Min(MaxCoupon, State.CouponPercent == 0 ? FirstGift : State.CouponPercent + GiftStep);
        Save();
        return State.CouponPercent;
    }

    /// <summary>Купон действует только в праздники этого сезона.</summary>
    public int ActiveCoupon(DateOnly today) => IsActive(today) && State.SeasonYear == SeasonYearOf(today) ? State.CouponPercent : 0;

    public int Days(DateOnly today) => State.SeasonYear == SeasonYearOf(today) ? State.LoginDays.Count : 0;
    public static bool HasGarland(int days) => days >= GarlandDays;
    public static bool HasStar(int days) => days >= StarDays;

    /// <summary>31 декабря 23:59 — 1 января 00:01.</summary>
    public static bool IsFireworksTime(DateTime now) =>
        (now.Month == 12 && now.Day == 31 && now.Hour == 23 && now.Minute == 59) ||
        (now.Month == 1 && now.Day == 1 && now.Hour == 0 && now.Minute < 1);

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(LauncherPaths.Root);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(State, Json.Options));
        }
        catch (IOException) { }
        Changed?.Invoke();
    }
}

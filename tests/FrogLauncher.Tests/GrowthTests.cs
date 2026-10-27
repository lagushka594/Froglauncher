using System.Globalization;
using System.Text.Json;
using FrogLauncher.Core;
using FrogLauncher.ViewModels;

namespace FrogLauncher.Tests;

public class FuzzyTests
{
    [Fact]
    public void Subsequence_matches_and_missing_letters_do_not()
    {
        Assert.NotNull(Fuzzy.Score("нстр", "Настройки"));
        Assert.Null(Fuzzy.Score("ябл", "Настройки"));
    }

    [Fact]
    public void Prefix_and_word_start_rank_higher()
    {
        Assert.True(Fuzzy.Score("сер", "Серверы") > Fuzzy.Score("сер", "Пресеты и серверы"));
        Assert.True(Fuzzy.Score("ии", "Играть: Wizards") is null || Fuzzy.Score("иг", "Играть") > Fuzzy.Score("иг", "Мои игры"));
    }

    [Fact]
    public void Wrong_keyboard_layout_still_finds()
    {
        Assert.Equal("настройки", Fuzzy.SwapLayout("yfcnhjqrb"));
        Assert.NotNull(Fuzzy.Best("yfcnh", "Настройки"));
        Assert.NotNull(Fuzzy.Best("ыщвшгь", "Sodium"));
    }

    [Fact]
    public void Yo_and_e_are_the_same() => Assert.NotNull(Fuzzy.Score("ещё", "Показать еще"));
}

public class LocalizationTests
{
    [Theory]
    [InlineData("auto", "pt-PT", "pt-BR")]
    [InlineData("auto", "uk-UA", "ru")]
    [InlineData("auto", "fr-FR", "en")]
    [InlineData("auto", "de-AT", "de")]
    [InlineData("es", "ru-RU", "es")]
    [InlineData("xx", "es-MX", "es")]
    public void Language_resolution(string setting, string system, string expected) =>
        Assert.Equal(expected, L.Resolve(setting, new CultureInfo(system)));

    [Fact]
    public void Untranslated_text_falls_back_to_russian()
    {
        // Без словаря (тесты запускаются на русском) — исходная строка и форматирование как есть.
        var n = 3;
        Assert.Equal("Осталось 3 дней", L.T($"Осталось {n} дней"));
        Assert.Equal("  Ждём…", L.T("  Ждём…"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("pt-BR")]
    public void Dictionaries_are_embedded_and_keep_placeholders(string code)
    {
        var map = L.LoadDictionary(code);
        Assert.True(map.Count > 700, $"{code}: {map.Count}");
        Assert.NotEqual("Играть", map["Играть"]);
        var re = new System.Text.RegularExpressions.Regex(@"(?<!\{)\{\d+[^}]*\}(?!\})");
        foreach (var (ru, tr) in map)
            Assert.True(re.Matches(ru).Select(m => m.Value).Order().SequenceEqual(re.Matches(tr).Select(m => m.Value).Order()), $"{code}: {ru}");
    }

    [Fact]
    public void Every_template_formats_with_sample_arguments()
    {
        foreach (var code in new[] { "en", "es", "de", "pt-BR" })
            foreach (var (_, tr) in L.LoadDictionary(code))
            {
                var args = Enumerable.Range(0, 5).Select(i => (object)(i + 1.5m)).ToArray();
                var ex = Record.Exception(() => string.Format(CultureInfo.InvariantCulture, tr, args));
                Assert.True(ex is null, $"{code}: {tr} → {ex?.Message}");
            }
    }
}

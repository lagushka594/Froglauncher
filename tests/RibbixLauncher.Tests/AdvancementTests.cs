using System.IO;
using System.IO.Compression;
using System.Text.Json.Nodes;
using RibbixLauncher.Services;
using RibbixLauncher.ViewModels;

namespace RibbixLauncher.Tests;

public class AdvancementTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "frog-adv-" + Guid.NewGuid().ToString("N"));

    public AdvancementTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private string Jar(string folder)
    {
        var jar = Path.Combine(_root, folder + ".jar");
        using var z = ZipFile.Open(jar, ZipArchiveMode.Create);
        void Add(string name, string json)
        {
            using var w = new StreamWriter(z.CreateEntry($"data/minecraft/{folder}/{name}.json").Open());
            w.Write(json);
        }
        Add("story/root", """{"display":{"title":{"translate":"advancements.story.root.title"},"description":{"translate":"advancements.story.root.description"},"icon":{"id":"minecraft:grass_block"}},"criteria":{"crafting_table":{}}}""");
        Add("husbandry/balanced_diet", """{"display":{"title":{"translate":"t.diet"},"frame":"challenge"},"criteria":{"apple":{},"bread":{},"carrot":{}},"requirements":[["apple"],["bread"],["carrot"]]}""");
        Add("nether/secret", """{"display":{"title":{"translate":"t.secret"},"hidden":true,"frame":"goal"},"criteria":{"a":{},"b":{}},"requirements":[["a","b"]]}""");
        Add("recipes/misc/bread", """{"criteria":{"x":{}}}""");
        Add("story/no_display", """{"criteria":{"x":{}}}""");
        return jar;
    }

    [Theory]
    [InlineData("advancements")] // 1.12–1.20
    [InlineData("advancement")]  // 1.21+
    public void Catalog_reads_displayed_advancements_in_game_tab_order(string folder)
    {
        var c = AdvancementService.ParseCatalog(Jar(folder));
        Assert.Equal(new[] { "minecraft:story/root", "minecraft:nether/secret", "minecraft:husbandry/balanced_diet" }, c.Select(a => a.Id));
        Assert.Equal(3, c[2].Requirements.Count);
        Assert.Single(c[0].Requirements); // без requirements — каждый критерий своя группа
        Assert.True(c[1].Hidden);
        Assert.Equal("challenge", c[2].Frame);
    }

    [Fact]
    public void Progress_counts_requirement_groups_like_the_game()
    {
        var c = AdvancementService.ParseCatalog(Jar("advancement"));
        var player = (JsonObject)JsonNode.Parse("""
            {"minecraft:story/root":{"criteria":{"crafting_table":"2026-10-26 22:44:59 +0700"},"done":true},
             "minecraft:husbandry/balanced_diet":{"criteria":{"apple":"x","carrot":"x"},"done":false},
             "minecraft:nether/secret":{"criteria":{"b":"x"},"done":false},
             "DataVersion":3120}
            """)!;
        var lang = new Dictionary<string, string> { ["advancements.story.root.title"] = "Minecraft", ["t.diet"] = "Сбалансированная диета" };
        var p = AdvancementService.Progress(c, player, lang).ToDictionary(a => a.Def.Id);

        Assert.True(p["minecraft:story/root"].IsDone);
        Assert.Equal("Minecraft", p["minecraft:story/root"].Title);
        Assert.Equal((2, 3), (p["minecraft:husbandry/balanced_diet"].Done, p["minecraft:husbandry/balanced_diet"].Total));
        Assert.Equal("Сбалансированная диета", p["minecraft:husbandry/balanced_diet"].Title);
        // Одна группа ["a","b"]: хватает любого критерия.
        Assert.Equal((1, 1), (p["minecraft:nether/secret"].Done, p["minecraft:nether/secret"].Total));

        Assert.All(AdvancementService.Progress(c, null, lang), a => Assert.Equal(0, a.Done));
    }

    [Fact]
    public void Last_played_reads_naturally()
    {
        var today = new DateOnly(2026, 10, 27);
        Assert.Equal("сегодня", StatsViewModel.Ago(today, today));
        Assert.Equal("вчера", StatsViewModel.Ago(today.AddDays(-1), today));
        Assert.Equal("3 дн. назад", StatsViewModel.Ago(today.AddDays(-3), today));
        Assert.Equal("01.10.2026", StatsViewModel.Ago(new DateOnly(2026, 10, 1), today));
    }

    [Theory]
    [InlineData("ru", "ru_ru")]
    [InlineData("pt-BR", "pt_br")]
    [InlineData("en", "en_us")]
    public void Launcher_language_maps_to_game_language(string lang, string mc) =>
        Assert.Equal(mc, AdvancementService.McLanguage(lang));
}

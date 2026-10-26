using System.Text.Json;
using FrogLauncher.Core;
using FrogLauncher.Models;
using FrogLauncher.Services;
using Frog.Shared;

namespace FrogLauncher.Tests;

public class EntitlementCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static EntitlementSnapshot Paid(DateTimeOffset fetched, DateTimeOffset periodEnd) => new()
    {
        Tier = TierSlugs.FrogPlus,
        Entitlements = new() { EntitlementKeys.Base, EntitlementKeys.NoAds, EntitlementKeys.InstancesUnlimited },
        PeriodEnd = periodEnd,
        FetchedAt = fetched,
    };

    [Fact]
    public void Offline_cache_is_trusted_for_7_days()
    {
        var s = EntitlementService.ApplyLocalExpiry(Paid(Now.AddDays(-6), Now.AddDays(20)), Now);
        Assert.Equal(TierSlugs.FrogPlus, s.Tier);
    }

    [Fact]
    public void Offline_cache_older_than_7_days_falls_back_to_tadpole()
    {
        var s = EntitlementService.ApplyLocalExpiry(Paid(Now.AddDays(-8), Now.AddDays(20)), Now);
        Assert.Equal(TierSlugs.Tadpole, s.Tier);
    }

    [Fact]
    public void Expired_period_offline_enters_grace_then_tadpole()
    {
        var inGrace = EntitlementService.ApplyLocalExpiry(Paid(Now.AddDays(-3), Now.AddDays(-2)), Now);
        Assert.True(inGrace.InGrace);
        Assert.Equal(TierSlugs.FrogPlus, inGrace.Tier);

        var gone = EntitlementService.ApplyLocalExpiry(Paid(Now.AddDays(-3), Now.AddDays(-8)), Now);
        Assert.Equal(TierSlugs.Tadpole, gone.Tier);
    }

    [Fact]
    public void Instance_limits_by_tier()
    {
        Assert.Equal(25, EntitlementKeys.MaxInstances(new[] { EntitlementKeys.Base }));
        Assert.Equal(50, EntitlementKeys.MaxInstances(new[] { EntitlementKeys.Base, EntitlementKeys.Instances50 }));
        Assert.Equal("У Tadpole максимум 25 версий. Froglet — 50, Frog+ — ∞.", new InstanceLimitException(TierSlugs.Tadpole, 25).Message);
        Assert.Null(EntitlementKeys.MaxInstances(new[] { EntitlementKeys.InstancesUnlimited }));
    }
}

public class LaunchTests
{
    private const string VanillaJson = """
        {
          "id": "1.20.1", "mainClass": "net.minecraft.client.main.Main", "type": "release",
          "assetIndex": { "id": "5", "url": "https://x/5.json" },
          "arguments": {
            "game": [ "--username", "${auth_player_name}", "--uuid", "${auth_uuid}",
                      { "rules": [ { "action": "allow", "features": { "is_demo_user": true } } ], "value": "--demo" },
                      { "rules": [ { "action": "allow", "features": { "has_custom_resolution": true } } ],
                        "value": [ "--width", "${resolution_width}", "--height", "${resolution_height}" ] } ],
            "jvm": [ { "rules": [ { "action": "allow", "os": { "name": "osx" } } ], "value": "-XstartOnFirstThread" },
                     "-Djava.library.path=${natives_directory}", "-cp", "${classpath}" ]
          },
          "libraries": [
            { "name": "com.mojang:a:1.0", "downloads": { "artifact": { "path": "com/mojang/a/1.0/a-1.0.jar", "url": "u", "sha1": "s", "size": 1 } } },
            { "name": "mac:only:1.0", "rules": [ { "action": "allow", "os": { "name": "osx" } } ],
              "downloads": { "artifact": { "path": "mac/only.jar", "url": "u", "size": 1 } } }
          ]
        }
        """;

    private static VersionInfo Vanilla() => JsonSerializer.Deserialize<VersionInfo>(VanillaJson, Json.Mojang)!;

    /// <summary>Сборка команды запуска не зависит от подписки: tadpole-пользователь играет так же.</summary>
    [Fact]
    public void Builds_command_line_with_rules_and_features()
    {
        var inst = new Instance { Id = "t", Directory = @"C:\frog\t", RamMb = 3072, Demo = true, Width = 1280, Height = 720, Fullscreen = true };
        var acc = new GameAccount { Name = "Froggy", Uuid = GameAuthService.OfflineUuid("Froggy") };
        var args = LaunchService.BuildArguments(Vanilla(), inst, acc, @"C:\natives", autotune: false);

        Assert.Contains("-Xmx3072M", args);
        Assert.DoesNotContain("-XstartOnFirstThread", args);          // правило только для macOS
        Assert.Contains("-Djava.library.path=C:\\natives", args);
        Assert.Contains("--demo", args);
        Assert.Contains("1280", args);
        Assert.Contains("--fullscreen", args);
        Assert.Equal("Froggy", args[args.IndexOf("--username") + 1]);

        var cp = args[args.IndexOf("-cp") + 1];
        Assert.Contains("a-1.0.jar", cp);
        Assert.DoesNotContain("only.jar", cp);                       // библиотека для macOS отфильтрована
        Assert.EndsWith(System.IO.Path.Combine("versions", "1.20.1", "1.20.1.jar"), cp);
        Assert.Equal("net.minecraft.client.main.Main", args[args.IndexOf("-cp") + 2]);
    }

    [Fact]
    public void Offline_uuid_matches_vanilla_server()
    {
        // Значение, которое считает сам сервер Minecraft для "OfflinePlayer:Notch".
        Assert.Equal("b50ad385829d3141a2167e7d7539ba7f", GameAuthService.OfflineUuid("Notch"));
    }

    [Theory]
    [InlineData("1.7.10", 8)]
    [InlineData("1.16.5", 8)]
    [InlineData("1.17.1", 17)]
    [InlineData("1.20.4", 17)]
    [InlineData("1.20.5", 21)]
    [InlineData("1.21.1", 21)]
    public void Picks_java_by_game_version(string version, int major) =>
        Assert.Equal(major, JavaService.RequiredMajor(version));

    [Theory]
    [InlineData("1.8.0_392", 8)]
    [InlineData("17.0.9", 17)]
    [InlineData("21", 21)]
    public void Parses_java_versions(string v, int major) => Assert.Equal(major, JavaService.ParseMajor(v));

    [Theory]
    [InlineData("net.fabricmc:fabric-loader:0.16.5", "net/fabricmc/fabric-loader/0.16.5/fabric-loader-0.16.5.jar")]
    [InlineData("org.lwjgl:lwjgl:3.3.1:natives-windows", "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows.jar")]
    [InlineData("de.oceanlabs.mcp:mcp_config:1.20.1@zip", "de/oceanlabs/mcp/mcp_config/1.20.1/mcp_config-1.20.1.zip")]
    public void Maven_paths(string coord, string path) => Assert.Equal(path, Maven.ToPath(coord));

    [Theory]
    [InlineData("    <log4j:Message><![CDATA[Setting user: Froggy]]></log4j:Message>", "Setting user: Froggy")]
    [InlineData("  <log4j:Event logger=\"x\" level=\"INFO\">", null)]
    [InlineData("\tat net.minecraft.Main.main(Main.java:1)", "\tat net.minecraft.Main.main(Main.java:1)")]
    public void Cleans_log4j_xml(string line, string? expected) => Assert.Equal(expected, LaunchService.CleanLogLine(line));

    [Theory]
    [InlineData("froglauncher://billing/success?sub_id=abc", true)]
    [InlineData("https://evil.example/", false)]
    [InlineData("froglauncher:", false)]
    public void Accepts_only_own_deeplinks(string link, bool ok) => Assert.Equal(ok, UrlScheme.TryParse(link, out _));

    [Fact]
    public void Tier_cards_available_without_server()
    {
        var tiers = FrogLauncher.ViewModels.SubscriptionViewModel.LoadOfflineTiers();
        Assert.Equal(5, tiers.Count);
        Assert.DoesNotContain(tiers, t => t.Slug == TierSlugs.Admin);
        Assert.Contains(tiers, t => t.Slug == TierSlugs.FrogPlus && t.Highlighted);
    }

    [Fact]
    public void Modrinth_facets_match_version_and_loader()
    {
        var quilt = new Instance { GameVersion = "1.20.1", Loader = LoaderType.Quilt };
        Assert.Equal("[[\"project_type:mod\"],[\"versions:1.20.1\"],[\"categories:quilt\",\"categories:fabric\"]]", ModrinthService.Facets(quilt));
        Assert.Empty(ModrinthService.Loaders(LoaderType.Vanilla));
    }

    [Fact]
    public void Discord_roles_map_to_highest_tier()
    {
        var tiers = EmbeddedTiers.Load();
        var map = new Dictionary<string, string> { ["111"] = TierSlugs.Froglet, ["222"] = TierSlugs.FrogPro, ["999"] = TierSlugs.Admin };

        Assert.Equal(TierSlugs.Tadpole, DiscordRoleMapper.Resolve(Array.Empty<string>(), map, tiers).Slug);
        Assert.Equal(TierSlugs.Tadpole, DiscordRoleMapper.Resolve(new[] { "555" }, map, tiers).Slug);   // чужая роль
        Assert.Equal(TierSlugs.Froglet, DiscordRoleMapper.Resolve(new[] { "111", "555" }, map, tiers).Slug);
        Assert.Equal(TierSlugs.FrogPro, DiscordRoleMapper.Resolve(new[] { "111", "222" }, map, tiers).Slug);
        var admin = DiscordRoleMapper.Resolve(new[] { "111", "999" }, map, tiers);
        Assert.Equal(TierSlugs.Admin, admin.Slug);
        Assert.Contains(EntitlementKeys.AdminBadge, admin.Entitlements);
    }

    [Fact]
    public void Pkce_challenge_matches_rfc7636_example() =>
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            DiscordAuthService.PkceChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    /// <summary>Возврат из браузера: favicon игнорируется, код принимается только с верным state.</summary>
    [Fact]
    public async Task Discord_callback_listener_accepts_code_with_valid_state()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var wait = DiscordAuthService.WaitForCodeAsync(listener, "st4te", new CancellationTokenSource(10000).Token);
            using var http = new System.Net.Http.HttpClient();
            var favicon = await http.GetAsync($"http://127.0.0.1:{port}/favicon.ico");
            Assert.Equal(System.Net.HttpStatusCode.NotFound, favicon.StatusCode);

            var page = await http.GetStringAsync($"http://127.0.0.1:{port}/callback?code=abc123&state=st4te");
            Assert.Contains("Готово", page);
            Assert.Equal("abc123", await wait);

            var forged = DiscordAuthService.WaitForCodeAsync(listener, "st4te", new CancellationTokenSource(10000).Token);
            _ = http.GetAsync($"http://127.0.0.1:{port}/callback?code=evil&state=wrong");
            await Assert.ThrowsAsync<DiscordAuthException>(() => forged);
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData("fabric-loader", "0.15.11", LoaderType.Fabric, "0.15.11")]
    [InlineData("quilt-loader", "0.26.0", LoaderType.Quilt, "0.26.0")]
    [InlineData("forge", "47.2.0", LoaderType.Forge, "1.20.1-47.2.0")]
    [InlineData("neoforge", "20.4.237", LoaderType.NeoForge, "20.4.237")]
    public void Mrpack_dependencies_map_to_loader(string key, string value, LoaderType loader, string expected)
    {
        var (l, v, mc) = ModpackService.MapDependencies(new Dictionary<string, string> { ["minecraft"] = "1.20.1", [key] = value });
        Assert.Equal(loader, l);
        Assert.Equal(expected, v);
        Assert.Equal("1.20.1", mc);
    }

    [Theory]
    [InlineData("mods/sodium.jar", true)]
    [InlineData("config/sub/dir/x.toml", true)]
    [InlineData("../../evil.dll", false)]
    [InlineData("mods/../../../evil.dll", false)]
    [InlineData("C:/Windows/evil.dll", false)]
    public void Mrpack_paths_cannot_escape_game_dir(string path, bool ok)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "frog-test-root");
        if (ok) Assert.StartsWith(root, ModpackService.SafePath(root, path));
        else Assert.Throws<ModsException>(() => ModpackService.SafePath(root, path));
    }

    [Theory]
    [InlineData("https://cdn.modrinth.com/data/AANobbMI/versions/x/sodium.jar", true)]
    [InlineData("https://github.com/x/y/releases/download/1/a.jar", true)]
    [InlineData("http://cdn.modrinth.com/data/a.jar", false)]
    [InlineData("https://evil.example/a.jar", false)]
    public void Mrpack_download_hosts_are_whitelisted(string url, bool ok) => Assert.Equal(ok, ModpackService.IsAllowedUrl(url));

    [Fact]
    public void Mrpack_index_is_read_from_archive()
    {
        using var ms = new System.IO.MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new System.IO.StreamWriter(zip.CreateEntry("modrinth.index.json").Open());
            w.Write("""
                { "formatVersion": 1, "game": "minecraft", "versionId": "5.0", "name": "Frog Pack",
                  "files": [ { "path": "mods/a.jar", "hashes": { "sha1": "aa" }, "env": { "client": "required", "server": "unsupported" },
                               "downloads": [ "https://cdn.modrinth.com/a.jar" ], "fileSize": 10 } ],
                  "dependencies": { "minecraft": "1.20.1", "fabric-loader": "0.15.11" } }
                """);
        }
        ms.Position = 0;
        using var read = new System.IO.Compression.ZipArchive(ms);
        var index = ModpackService.ReadIndex(read);
        Assert.Equal("Frog Pack", index.Name);
        Assert.Equal("aa", Assert.Single(index.Files).Hashes["sha1"]);
        Assert.Equal("0.15.11", index.Dependencies["fabric-loader"]);
    }

    [Fact]
    public void NeoForge_version_prefix() =>
        Assert.Equal("21.1.", ModLoaderService.NeoForgePrefix("1.21.1"));

    [Fact]
    public void NeoForge_prefix_for_year_versions() =>
        Assert.Equal("26.2.", ModLoaderService.NeoForgePrefix("26.2"));
}

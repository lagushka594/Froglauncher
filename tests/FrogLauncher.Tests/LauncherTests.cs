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
    public void Options_file_keeps_unknown_keys_and_order()
    {
        var dir = System.IO.Directory.CreateTempSubdirectory("frog-opt-").FullName;
        try
        {
            System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "options.txt"), new[]
            {
                "version:3955", "sodium_mod_key:42", "renderDistance:12", "fov:0.0", "resourcePacks:[\"vanilla\",\"file/Faithful.zip\"]",
            });
            var o = OptionsFile.Load(dir);
            o.SetInt("renderDistance", 8);
            o.SetDouble("fov", McValues.FovToOption(90));
            o.SetBool("autoJump", false);
            o.Save();

            var lines = System.IO.File.ReadAllLines(System.IO.Path.Combine(dir, "options.txt"));
            Assert.Equal("version:3955", lines[0]);
            Assert.Equal("sodium_mod_key:42", lines[1]);       // ключ мода не потерялся
            Assert.Equal("renderDistance:8", lines[2]);        // порядок сохранён
            Assert.Equal("fov:0.5", lines[3]);                 // 90° = (90-70)/40
            Assert.Equal("autoJump:false", lines[^1]);         // новый ключ — в конец
            Assert.Equal(new[] { "vanilla", "file/Faithful.zip" }, OptionsFile.Load(dir).GetResourcePacks());
        }
        finally
        {
            System.IO.Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Options_quoted_values_keep_quotes()
    {
        var dir = System.IO.Directory.CreateTempSubdirectory("frog-opt-").FullName;
        try
        {
            System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "options.txt"), new[] { "renderClouds:\"true\"", "lang:ru_ru" });
            var o = OptionsFile.Load(dir);
            Assert.Equal("true", o.GetString("renderClouds"));
            o.SetString("renderClouds", "fast");
            o.SetString("lang", "en_us");
            Assert.Equal("\"fast\"", o.Get("renderClouds"));   // 1.19+: в кавычках, как было
            Assert.Equal("en_us", o.Get("lang"));               // без кавычек, как было
        }
        finally
        {
            System.IO.Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(0.0, 70)]
    [InlineData(-1.0, 30)]
    [InlineData(1.0, 110)]
    public void Fov_conversion(double option, int fov)
    {
        Assert.Equal(fov, McValues.FovFromOption(option));
        Assert.Equal(option, McValues.FovToOption(fov), 3);
    }

    [Theory]
    [InlineData(System.Windows.Input.Key.W, "key.keyboard.w", "W")]
    [InlineData(System.Windows.Input.Key.LeftShift, "key.keyboard.left.shift", "Left Shift")]
    [InlineData(System.Windows.Input.Key.F5, "key.keyboard.f5", "F5")]
    [InlineData(System.Windows.Input.Key.D3, "key.keyboard.3", "3")]
    public void Keys_map_to_minecraft_names(System.Windows.Input.Key key, string mc, string label)
    {
        Assert.Equal(mc, McValues.FromWpfKey(key));
        Assert.Equal(label, McValues.KeyLabel(mc));
    }

    [Fact]
    public void Pc_classification_and_ram_recommendation()
    {
        static HardwareInfo Pc(double ramGb, int cores, string gpu, double vramGb) => new(
            (long)(ramGb * 1024 * 1024 * 1024), 0, cores, "cpu", 3000,
            new[] { new GpuInfo(gpu, (long)(vramGb * 1024 * 1024 * 1024), HardwareService.IsIntegrated(gpu)) }, 1920, 1080);

        Assert.Equal(PresetKind.Performance, HardwareService.Classify(Pc(8, 4, "Intel(R) UHD Graphics 620", 1)));
        Assert.Equal(PresetKind.Balanced, HardwareService.Classify(Pc(16, 6, "NVIDIA GeForce GTX 1660 SUPER", 6)));
        Assert.Equal(PresetKind.Quality, HardwareService.Classify(Pc(32, 16, "NVIDIA GeForce RTX 4070", 12)));
        Assert.True(HardwareService.IsIntegrated("AMD Radeon(TM) Graphics"));
        Assert.False(HardwareService.IsIntegrated("AMD Radeon RX 7800 XT"));
        Assert.Equal(4096, HardwareService.RecommendedRamMb(16));
        // Пресеты не предлагают больше памяти, чем безопасно для ПК.
        Assert.All(HardwareService.Presets(Pc(4, 2, "Intel HD", 0.5)), p => Assert.True(p.RamMb <= 2048));
    }

    [Fact]
    public void Gc_and_min_ram_in_command_line()
    {
        var v = System.Text.Json.JsonSerializer.Deserialize<VersionInfo>("""{ "id": "1.20.1", "mainClass": "M", "libraries": [] }""", Json.Mojang)!;
        var acc = new GameAccount { Name = "F", Uuid = "0" };

        var zgc = LaunchService.BuildArguments(v, new Instance { Directory = "C:\\f", RamMb = 6144, RamMinMb = 2048, Gc = "zgc" }, acc, "n", false, 21);
        Assert.Contains("-Xms2048M", zgc);
        Assert.Contains("-XX:+UseZGC", zgc);
        Assert.Contains("-XX:+ZGenerational", zgc);

        // На Java 8 ZGC нет — остаётся G1, игра не падает.
        var old = LaunchService.BuildArguments(v, new Instance { Directory = "C:\\f", RamMb = 2048, Gc = "zgc" }, acc, "n", false, 8);
        Assert.DoesNotContain("-XX:+UseZGC", old);
        Assert.Contains("-XX:+UseG1GC", old);
    }

    [Fact]
    public void Embedded_server_list_is_valid()
    {
        var asm = typeof(ServerCatalog).Assembly;
        Assert.Contains("FrogLauncher.servers.json", asm.GetManifestResourceNames());
        using var s = asm.GetManifestResourceStream("FrogLauncher.servers.json")!;
        var list = ServerCatalog.Parse(new System.IO.StreamReader(s).ReadToEnd());
        Assert.True(list.Count >= 15);
        Assert.All(list, e => Assert.False(string.IsNullOrWhiteSpace(e.Address)));
    }

    [Theory]
    [InlineData("2.2.0", "2.1.0", true)]
    [InlineData("2.1.0", "2.1.0", false)]
    [InlineData("2.0.9", "2.1.0", false)]
    [InlineData("2.2.0", "2.2.0-beta.3", true)]    // релиз новее своей беты
    [InlineData("2.2.0-beta.1", "2.2.0", false)]
    [InlineData("2.2.0-beta.2", "2.2.0-beta.1", true)]
    [InlineData("10.0.0", "9.9.9", true)]           // числами, а не строками
    public void Update_version_comparison(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateService.IsNewer(candidate, current));

    /// <summary>Как в настоящем репозитории: GitHub отдаёт v1.0.1 раньше V2.0.0.</summary>
    [Fact]
    public void Update_picks_highest_version_regardless_of_order()
    {
        static string Rel(string tag, bool pre, string sha) => $$"""
            { "tag_name": "{{tag}}", "prerelease": {{(pre ? "true" : "false")}}, "draft": false, "html_url": "u", "body": "",
              "assets": [ { "name": "FrogLauncherSetup.exe", "browser_download_url": "https://github.com/x/{{tag}}", "digest": "sha256:{{sha}}" } ] }
            """;
        var releases = System.Text.Json.Nodes.JsonNode.Parse(
            $"[{Rel("v1.0.1", false, "aa")},{Rel("V2.0.0", false, "bb")},{Rel("v2.3.0-beta.1", true, "cc")},{Rel("v1.0.0", true, "dd")}]")!.AsArray();

        var stable = UpdateService.PickUpdate(releases, "1.0.1", beta: false);
        Assert.Equal("2.0.0", stable!.Version);
        Assert.Equal("bb", stable.Sha256);

        Assert.Equal("2.3.0-beta.1", UpdateService.PickUpdate(releases, "2.1.0", beta: true)!.Version);
        Assert.Null(UpdateService.PickUpdate(releases, "2.1.0", beta: false)); // 2.0.0 старее текущей
    }

    [Theory]
    [InlineData(System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.Control, "Ctrl+Enter")]
    [InlineData(System.Windows.Input.Key.OemComma, System.Windows.Input.ModifierKeys.Control, "Ctrl+,")]
    [InlineData(System.Windows.Input.Key.M, System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift, "Ctrl+Shift+M")]
    [InlineData(System.Windows.Input.Key.F5, System.Windows.Input.ModifierKeys.None, "F5")]
    public void Hotkey_gestures_format(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys mods, string expected)
    {
        Assert.Equal(expected, HotkeyService.Format(key, mods));
        Assert.Contains(HotkeyService.Actions, a => a.Default == expected);
    }

    [Fact]
    public void Hotkey_modifier_alone_is_not_a_gesture() =>
        Assert.Null(HotkeyService.Format(System.Windows.Input.Key.LeftCtrl, System.Windows.Input.ModifierKeys.Control));

    [Theory]
    [InlineData("mc.hypixel.net", "mc.hypixel.net", 25565)]
    [InlineData("play.example.com:25570", "play.example.com", 25570)]
    [InlineData("1.2.3.4", "1.2.3.4", 25565)]
    public void Server_address_parsing(string address, string host, int port) =>
        Assert.Equal((host, port), ServerPing.ParseAddress(address));

    [Fact]
    public void Server_motd_strips_colors_and_reads_components()
    {
        var motd = ServerPing.MotdText(System.Text.Json.Nodes.JsonNode.Parse(
            """{ "text": "§aFrog §lServer", "extra": [ { "text": "\n§eНовый режим!" } ] }"""));
        Assert.Equal("Frog Server\nНовый режим!", motd);
        Assert.Equal("1.8-26.3", ServerPing.StripColors("§f§f1.8-26.3"));
    }

    [Fact]
    public void Varint_roundtrip()
    {
        foreach (var n in new[] { 0, 1, 127, 128, 255, 25565, 2097151, -1 })
        {
            using var ms = new System.IO.MemoryStream();
            ServerPing.WriteVarInt(ms, n);
            ms.Position = 0;
            Assert.Equal(n, ServerPing.ReadVarInt(ms));
        }
    }

    [Fact]
    public void Quick_play_for_modern_versions_and_server_flag_for_old()
    {
        var acc = new GameAccount { Name = "F", Uuid = "0" };
        var modern = System.Text.Json.JsonSerializer.Deserialize<VersionInfo>("""
            { "id": "1.20.1", "mainClass": "M", "libraries": [],
              "arguments": { "game": [ "--username", "${auth_player_name}",
                { "rules": [ { "action": "allow", "features": { "is_quick_play_multiplayer": true } } ],
                  "value": [ "--quickPlayMultiplayer", "${quickPlayMultiplayer}" ] } ], "jvm": [] } }
            """, Json.Mojang)!;
        var a = LaunchService.BuildArguments(modern, new Instance { Directory = "C:\\f" }, acc, "n", false, 17, "mc.hypixel.net");
        Assert.Equal("mc.hypixel.net", a[a.IndexOf("--quickPlayMultiplayer") + 1]);
        Assert.DoesNotContain("--server", a);

        var old = System.Text.Json.JsonSerializer.Deserialize<VersionInfo>("""{ "id": "1.12.2", "mainClass": "M", "libraries": [], "minecraftArguments": "--username ${auth_player_name}" }""", Json.Mojang)!;
        var b = LaunchService.BuildArguments(old, new Instance { Directory = "C:\\f" }, acc, "n", false, 8, "play.example.com:25570");
        Assert.Equal("play.example.com", b[b.IndexOf("--server") + 1]);
        Assert.Equal("25570", b[b.IndexOf("--port") + 1]);

        // Без сервера Quick Play не включается.
        var none = LaunchService.BuildArguments(modern, new Instance { Directory = "C:\\f" }, acc, "n", false);
        Assert.DoesNotContain("--quickPlayMultiplayer", none);
    }

    [Fact]
    public void NeoForge_version_prefix() =>
        Assert.Equal("21.1.", ModLoaderService.NeoForgePrefix("1.21.1"));

    [Fact]
    public void NeoForge_prefix_for_year_versions() =>
        Assert.Equal("26.2.", ModLoaderService.NeoForgePrefix("26.2"));
}

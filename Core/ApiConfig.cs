namespace FrogLauncher.Core;

/// <summary>Все внешние API-эндпоинты в одном месте. Меняйте здесь — больше нигде.</summary>
public static class ApiConfig
{
    public const string LauncherVersion = "1.0.0";

    // --- Mojang ---
    public const string VersionManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const string ResourcesBase   = "https://resources.download.minecraft.net/";
    public const string LibrariesBase   = "https://libraries.minecraft.net/";
    public const string NewsJson        = "https://launchercontent.mojang.com/v2/javaPatchNotes.json";
    public static readonly string[] StatusHosts =
    {
        "https://api.mojang.com", "https://sessionserver.mojang.com", "https://api.minecraftservices.com"
    };

    // --- Модлоадеры ---
    public const string FabricLoaders   = "https://meta.fabricmc.net/v2/versions/loader/";
    public const string QuiltLoaders    = "https://meta.quiltmc.org/v3/versions/loader/";
    public const string ForgeMetadata   = "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml";
    public const string ForgeMaven      = "https://maven.minecraftforge.net/net/minecraftforge/forge/";
    public const string ForgeLegacyHost = "http://files.minecraftforge.net/maven/";
    public const string ForgeNewHost    = "https://maven.minecraftforge.net/";
    public const string NeoMetadata     = "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml";
    public const string NeoMaven        = "https://maven.neoforged.net/releases/net/neoforged/neoforge/";

    // --- Java (Adoptium / Temurin) ---
    public const string AdoptiumJre = "https://api.adoptium.net/v3/binary/latest/{0}/ga/windows/x64/jre/hotspot/normal/eclipse";

    // --- Microsoft / Xbox / Minecraft Services ---
    public const string MsDeviceCode = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    public const string MsToken      = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    public const string MsScope      = "XboxLive.signin offline_access";
    public const string XblAuth      = "https://user.auth.xboxlive.com/user/authenticate";
    public const string XstsAuth     = "https://xsts.auth.xboxlive.com/xsts/authorize";
    public const string McLogin      = "https://api.minecraftservices.com/authentication/login_with_xbox";
    public const string McProfile    = "https://api.minecraftservices.com/minecraft/profile";
    public const string McSkins      = "https://api.minecraftservices.com/minecraft/profile/skins";

    // --- Каталоги модов ---
    public const string ModrinthApi      = "https://api.modrinth.com/v2";
    public const string CurseForgeApi    = "https://api.curseforge.com/v1";
    public const string CurseForgeEdge   = "https://edge.forgecdn.net/files/{0}/{1}/{2}";
    public const string CurseForgeConsole = "https://console.curseforge.com/";
    public const int    CurseForgeGameId = 432;   // Minecraft
    public const int    CurseForgeModsClass = 6;  // раздел «Mods»

    public const string GithubRepoUrl = "https://github.com/lagushka594/Froglauncher";

    // --- Прочее ---
    public const string MclogsUpload = "https://api.mclo.gs/1/log";
    public const string GithubLatest = "https://api.github.com/repos/{0}/releases/latest";
}

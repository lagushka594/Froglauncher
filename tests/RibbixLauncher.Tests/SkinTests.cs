using System.IO;
using System.Text.Json.Nodes;
using RibbixLauncher.Services;

namespace RibbixLauncher.Tests;

public class SkinTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ribbix-skin-" + Guid.NewGuid().ToString("N"));

    public SkinTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static byte[] Png(int w, int h)
    {
        var b = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10, 26, 10, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(b, 0);
        b[16] = (byte)(w >> 24); b[17] = (byte)(w >> 16); b[18] = (byte)(w >> 8); b[19] = (byte)w;
        b[20] = (byte)(h >> 24); b[21] = (byte)(h >> 16); b[22] = (byte)(h >> 8); b[23] = (byte)h;
        return b;
    }

    [Fact]
    public void Only_minecraft_skin_sizes_are_accepted()
    {
        Assert.Equal((64, 64), SkinService.CheckPng(Png(64, 64)));
        Assert.Equal((64, 32), SkinService.CheckPng(Png(64, 32)));
        Assert.Throws<SkinException>(() => SkinService.CheckPng(Png(128, 128)));
        Assert.Throws<SkinException>(() => SkinService.CheckPng(new byte[40]));
    }

    [Fact]
    public void Offline_pack_replaces_every_default_skin_and_is_enabled_last()
    {
        var dir = Path.Combine(_root, ".minecraft", "resourcepacks", SkinService.PackFolder);
        SkinService.BuildPack(dir, Png(64, 64));
        Assert.True(File.Exists(Path.Combine(dir, "pack.mcmeta")));
        Assert.True(File.Exists(Path.Combine(dir, "assets", "minecraft", "textures", "entity", "player", "slim", "alex.png")));
        Assert.True(File.Exists(Path.Combine(dir, "assets", "minecraft", "textures", "entity", "steve.png")));
        Assert.Equal(20, SkinService.DefaultSkins.Length);

        File.WriteAllText(Path.Combine(_root, ".minecraft", "options.txt"), "resourcePacks:[\"vanilla\",\"file/Faithful.zip\"]\nfov:0.5\n");
        var opt = OptionsFile.Load(Path.Combine(_root, ".minecraft"));
        SkinService.SetPack(opt, enabled: true);
        Assert.Equal(new[] { "vanilla", "file/Faithful.zip", "file/" + SkinService.PackFolder }, opt.GetResourcePacks());
        Assert.Contains("file/" + SkinService.PackFolder, JsonNode.Parse(opt.Get("incompatibleResourcePacks")!)!.AsArray().Select(n => n!.GetValue<string>()));
        SkinService.SetPack(opt, enabled: false);
        Assert.Equal(new[] { "vanilla", "file/Faithful.zip" }, opt.GetResourcePacks());
        Assert.Equal("0.5", opt.Get("fov"));
    }
}

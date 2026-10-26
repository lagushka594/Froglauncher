using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FrogLauncher.Core;

namespace FrogLauncher.Models;

/// <summary>Мод в папке mods: название и картинка берутся из самого jar. Отключение — переименование в *.jar.disabled.</summary>
public sealed class ModFile : ObservableObject
{
    private string _path;
    private string? _display;
    private ImageSource? _icon;

    public ModFile(string path)
    {
        _path = path;
        Task.Run(LoadMetadata);   // читаем jar в фоне, чтобы список не «подвисал»
    }

    public string FullPath => _path;

    public string FileName
    {
        get
        {
            var n = System.IO.Path.GetFileName(_path);
            return n.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? n[..^".disabled".Length] : n;
        }
    }

    /// <summary>Красивое имя мода (из fabric.mod.json / mods.toml), иначе имя файла.</summary>
    public string Name => string.IsNullOrWhiteSpace(_display) ? FileName : _display!;
    public ImageSource? Icon { get => _icon; private set => Set(ref _icon, value); }

    public bool Enabled
    {
        get => !_path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value == Enabled) return;
            var target = value ? _path[..^".disabled".Length] : _path + ".disabled";
            try
            {
                File.Move(_path, target, true);
                _path = target;
            }
            catch (Exception ex) { Log.Error("Переключение мода", ex); Dialogs.Error("Не удалось переключить мод: " + ex.Message); }
            OnChanged(); OnChanged(nameof(FullPath));
        }
    }

    private void LoadMetadata()
    {
        try
        {
            using var zip = ZipFile.OpenRead(_path);
            string? name = null, iconPath = null;

            var fabric = zip.GetEntry("fabric.mod.json") ?? zip.GetEntry("quilt.mod.json");
            if (fabric != null)
            {
                var json = JsonNode.Parse(ReadText(fabric));
                var root = json?["quilt_loader"]?["metadata"] ?? json;
                name = (string?)root?["name"];
                var ic = root?["icon"];
                if (ic is JsonValue v) iconPath = (string?)v;
                else if (ic is JsonObject o)
                    iconPath = o.OrderByDescending(kv => int.TryParse(kv.Key, out var n) ? n : 0).Select(kv => (string?)kv.Value).FirstOrDefault();
            }
            else
            {
                var toml = zip.GetEntry("META-INF/mods.toml") ?? zip.GetEntry("META-INF/neoforge.mods.toml");
                if (toml != null)
                {
                    var t = ReadText(toml);
                    name = Regex.Match(t, "displayName\\s*=\\s*\"([^\"]+)\"").Groups[1].Value;
                    iconPath = Regex.Match(t, "logoFile\\s*=\\s*\"([^\"]+)\"").Groups[1].Value;
                }
                else if (zip.GetEntry("mcmod.info") is { } info)
                {
                    var arr = JsonNode.Parse(ReadText(info));
                    var first = arr is JsonArray a ? a.FirstOrDefault() : arr?["modList"]?[0];
                    name = (string?)first?["name"]; iconPath = (string?)first?["logoFile"];
                }
            }

            ZipArchiveEntry? iconEntry = null;
            if (!string.IsNullOrWhiteSpace(iconPath)) iconEntry = zip.GetEntry(iconPath!.TrimStart('/'));
            iconEntry ??= zip.GetEntry("pack.png") ?? zip.GetEntry("icon.png") ?? zip.GetEntry("logo.png");

            ImageSource? img = null;
            if (iconEntry != null && iconEntry.Length < 4_000_000)
            {
                using var ms = new MemoryStream();
                using (var s = iconEntry.Open()) s.CopyTo(ms);
                ms.Position = 0;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 96;
                bi.StreamSource = ms;
                bi.EndInit();
                bi.Freeze();
                img = bi;
            }

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!string.IsNullOrWhiteSpace(name)) { _display = name; OnChanged(nameof(Name)); }
                if (img != null) Icon = img;
            }));
        }
        catch (Exception ex) { Log.Warn($"Мод {Path.GetFileName(_path)}: {ex.Message}"); }
    }

    private static string ReadText(ZipArchiveEntry e)
    {
        using var sr = new StreamReader(e.Open(), System.Text.Encoding.UTF8);
        return sr.ReadToEnd();
    }
}

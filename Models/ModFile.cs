using FrogLauncher.Core;

namespace FrogLauncher.Models;

/// <summary>Мод в папке mods. Отключение — переименование в *.jar.disabled (как делают другие лаунчеры).</summary>
public sealed class ModFile : ObservableObject
{
    private string _path;
    public ModFile(string path) { _path = path; }

    public string FullPath => _path;

    public string Name
    {
        get
        {
            var n = System.IO.Path.GetFileName(_path);
            return n.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? n[..^".disabled".Length] : n;
        }
    }

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
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FrogLauncher.Services;
using FrogLauncher.ViewModels;

namespace FrogLauncher.Views;

public partial class SettingsView : UserControl
{
    /// <summary>Горячая клавиша, для которой ждём новое сочетание.</summary>
    private HotkeyItem? _capturing;

    public SettingsView() => InitializeComponent();

    private void Hotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing is not null) _capturing.IsCapturing = false;
        _capturing = (HotkeyItem)((FrameworkElement)sender).Tag;
        _capturing.IsCapturing = true;
        HotkeyService.Suspended = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_capturing is null || DataContext is not SettingsViewModel vm)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;
        if (key == Key.Escape) { Stop(); return; }
        if (key == Key.Back) { var item = _capturing; Stop(); vm.SetHotkey(item, ""); return; }
        if (HotkeyService.Format(key, Keyboard.Modifiers) is not { } gesture) return; // ждём основную клавишу

        var target = _capturing;
        Stop();
        vm.SetHotkey(target, gesture);
    }

    private void Stop()
    {
        if (_capturing is not null) _capturing.IsCapturing = false;
        _capturing = null;
        HotkeyService.Suspended = false;
    }
}

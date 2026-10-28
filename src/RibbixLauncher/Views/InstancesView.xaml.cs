using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RibbixLauncher.Services;
using RibbixLauncher.ViewModels;

namespace RibbixLauncher.Views;

public partial class InstancesView : UserControl
{
    /// <summary>Действие управления, для которого сейчас ждём нажатия клавиши или кнопки мыши.</summary>
    private KeyBindItem? _capturing;

    public InstancesView() => InitializeComponent();

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files ? files : Array.Empty<string>();

    /// <summary>Файлы, брошенные на «Каталог»: сборки → новые версии, мир → в выбранную версию.</summary>
    private async void Catalog_Drop(object sender, DragEventArgs e)
    {
        if (DataContext is not InstancesViewModel vm) return;
        foreach (var f in DroppedFiles(e)) await vm.ImportFileAsync(f);
    }

    /// <summary>Во вкладке «Миры» — только миры, в эту версию.</summary>
    private async void Worlds_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is not WorldsViewModel vm) return;
        foreach (var f in DroppedFiles(e)) await vm.ImportFileAsync(f);
    }

    private void KeyBind_Click(object sender, RoutedEventArgs e)
    {
        if (_capturing is not null) _capturing.IsCapturing = false;
        _capturing = (KeyBindItem)((FrameworkElement)sender).Tag;
        _capturing.IsCapturing = true;
        HotkeyService.Suspended = true;
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_capturing is null)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) StopCapture();
        else if (McValues.FromWpfKey(key) is { } mc)
        {
            _capturing.Value = mc;
            StopCapture();
        }
        e.Handled = true;
    }

    // Click срабатывает на отпускании, поэтому следующее нажатие мыши — уже выбор кнопки.
    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        if (_capturing is not null && McValues.FromMouse(e.ChangedButton) is { } mc)
        {
            _capturing.Value = mc;
            StopCapture();
            e.Handled = true;
            return;
        }
        base.OnPreviewMouseDown(e);
    }

    private void StopCapture()
    {
        if (_capturing is not null) _capturing.IsCapturing = false;
        _capturing = null;
        HotkeyService.Suspended = false;
    }
}

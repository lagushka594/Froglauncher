using FrogLauncher.Core;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FrogLauncher.Services;
using FrogLauncher.ViewModels;

namespace FrogLauncher.Views;

public partial class MainWindow : Window
{
    private readonly HotkeyService _hotkeys;
    private readonly TrayService _tray;
    private readonly SettingsService _settings;

    public MainWindow(HotkeyService hotkeys, TrayService tray, SettingsService settings)
    {
        _hotkeys = hotkeys;
        _tray = tray;
        _settings = settings;
        InitializeComponent();
        tray.OpenRequested += ShowFromTray;
        tray.ExitRequested += () => Dispatcher.Invoke(Close);
        tray.PlayRequested += () => Dispatcher.Invoke(() => (DataContext as MainViewModel)?.RunHotkey("play"));
        tray.StopRequested += () => Dispatcher.Invoke(() => (DataContext as MainViewModel)?.StopGame());
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not MainViewModel vm) return;
            vm.SearchRequested += () => FocusSearch(vm);
            vm.Palette.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CommandPaletteViewModel.IsOpen) && vm.Palette.IsOpen)
                    Dispatcher.BeginInvoke(() => { PaletteBox.Focus(); Keyboard.Focus(PaletteBox); }, System.Windows.Threading.DispatcherPriority.Input);
                if (e.PropertyName == nameof(CommandPaletteViewModel.Selected) && vm.Palette.Selected is { } sel)
                    PaletteList.ScrollIntoView(sel);
            };
        };
    }

    private CommandPaletteViewModel? Palette => (DataContext as MainViewModel)?.Palette;

    private void PaletteBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Palette is not { } p) return;
        switch (e.Key)
        {
            case Key.Down: p.Move(1); e.Handled = true; break;
            case Key.Up: p.Move(-1); e.Handled = true; break;
            case Key.Enter: p.ExecuteCommand.Execute(null); e.Handled = true; break;
            case Key.Escape: p.Close(); e.Handled = true; break;
        }
    }

    private void PaletteList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PaletteItem item) Palette?.ExecuteCommand.Execute(item);
    }

    private void PaletteBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => Palette?.Close();

    /// <summary>Без элемента с фокусом WPF не доставляет нажатия — тогда фокус держит само окно (для горячих клавиш).</summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (Keyboard.FocusedElement is null) Keyboard.Focus(this);
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);
        Dispatcher.BeginInvoke(() => { if (Keyboard.FocusedElement is null) Keyboard.Focus(this); });
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!HotkeyService.Suspended && DataContext is MainViewModel vm)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (_hotkeys.Match(key, Keyboard.Modifiers) is { } action)
            {
                // Без модификаторов (F5) не перехватываем ввод в текстовых полях, кроме самого F5.
                var inText = e.OriginalSource is TextBox && Keyboard.Modifiers == ModifierKeys.None && key != Key.F5;
                if (!inText)
                {
                    vm.RunHotkey(action);
                    e.Handled = true;
                    return;
                }
            }
        }
        base.OnPreviewKeyDown(e);
    }

    /// <summary>Ctrl+K: фокус в первое поле поиска текущей страницы, иначе — открыть «Сборки».</summary>
    private void FocusSearch(MainViewModel vm)
    {
        if (FindSearchBox(this) is { } box)
        {
            box.Focus();
            box.SelectAll();
            return;
        }
        vm.OpenDefaultSearch();
        Dispatcher.BeginInvoke(() => FindSearchBox(this)?.Focus(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static TextBox? FindSearchBox(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBox { IsVisible: true } tb && AutomationProperties.GetName(tb).StartsWith("Поиск", StringComparison.Ordinal)) // noloc: имя для автоматизации
                return tb;
            if (FindSearchBox(child) is { } found) return found;
        }
        return null;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public void ShowFromTray() => Dispatcher.Invoke(() =>
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    });

    /// <summary>Свернуть в трей (крестик или Ctrl+Shift+M).</summary>
    public void HideToTray()
    {
        Hide();
        if (!_settings.Current.TrayHintShown)
        {
            _settings.Current.TrayHintShown = true;
            _settings.Save();
            _tray.Notify(L.T("FrogLauncher в трее 🐸"), L.T("Лаунчер продолжает работать здесь. Выход — правой кнопкой по значку → «Выход»."));
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_tray.IsExiting && _settings.Current.MinimizeToTray)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnClosing(e);
        Application.Current.Shutdown();
    }
}

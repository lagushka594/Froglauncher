using System.Windows;
using System.Windows.Input;
using FrogLauncher.Core;
using FrogLauncher.ViewModels;

namespace FrogLauncher.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;

        MinBtn.Click += (_, _) => WindowState = WindowState.Minimized;
        CloseBtn.Click += (_, _) => Close();
        ExitBtn.Click += (_, _) => Close();

        Loaded += async (_, _) => await vm.InitAsync();

        // автопрокрутка лога игры
        // прокрутку делаем ПОСЛЕ того, как ListBox обработал изменение коллекции (иначе «ItemsControl не соответствует источнику»)
        vm.LogLines.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
            }), System.Windows.Threading.DispatcherPriority.Background);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!App.Quitting && DataContext is MainViewModel { IsRunning: true })
        {
            if (!Dialogs.Confirm("Игра ещё запущена. Закрыть лаунчер? (Игра продолжит работу.)")) e.Cancel = true;
        }
        base.OnClosing(e);
    }
}

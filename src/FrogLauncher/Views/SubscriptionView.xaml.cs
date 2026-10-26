using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FrogLauncher.ViewModels;

namespace FrogLauncher.Views;

public partial class SubscriptionView : UserControl
{
    private const int PullThreshold = 360; // ~3 щелчка колеса вверх у верхнего края
    private int _pull;

    public SubscriptionView() => InitializeComponent();

    /// <summary>
    /// Pull-to-refresh для мыши/тачпада: если страница уже наверху и пользователь продолжает
    /// крутить вверх — копим «натяжение» и обновляем статус подписки.
    /// </summary>
    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Scroller.VerticalOffset > 0 || e.Delta <= 0)
        {
            _pull = 0;
            PullHint.Visibility = Visibility.Collapsed;
            return;
        }

        _pull += e.Delta;
        PullHint.Visibility = Visibility.Visible;
        if (_pull < PullThreshold) return;

        _pull = 0;
        PullHint.Visibility = Visibility.Collapsed;
        if (DataContext is SubscriptionViewModel vm && vm.RefreshCommand.CanExecute(null))
            vm.RefreshCommand.Execute(null);
    }
}

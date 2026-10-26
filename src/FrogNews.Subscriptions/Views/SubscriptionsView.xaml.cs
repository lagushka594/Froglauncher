using System.Windows;
using System.Windows.Controls;
using FrogNews.Subscriptions.ViewModels;

namespace FrogNews.Subscriptions.Views;

public partial class SubscriptionsView : UserControl
{
    public SubscriptionsView()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is SubscriptionsViewModel vm) await vm.LoadAsync();
        };
    }
}

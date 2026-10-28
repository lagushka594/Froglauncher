using System.Windows;
using System.Windows.Controls;
using RibbixNews.Subscriptions.ViewModels;

namespace RibbixNews.Subscriptions.Views;

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

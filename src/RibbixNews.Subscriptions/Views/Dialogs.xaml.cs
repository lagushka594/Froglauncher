using System.Windows;

namespace RibbixNews.Subscriptions.Views;

public partial class GrantAdminDialog : Window
{
    public GrantAdminDialog() => InitializeComponent();

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

public partial class RevokeAdminDialog : Window
{
    public RevokeAdminDialog() => InitializeComponent();

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

public partial class GrantSubscriptionDialog : Window
{
    public GrantSubscriptionDialog() => InitializeComponent();

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

using System.Net.Http;
using System.Windows;
using RibbixNews.Subscriptions.Services;
using RibbixNews.Subscriptions.ViewModels;
using RibbixNews.Subscriptions.Views;
using Microsoft.Extensions.DependencyInjection;

namespace RibbixNews.DevHost;

public partial class MainWindow : Window
{
    private readonly IServiceProvider _services;
    private readonly ServiceTokenProvider _tokens;

    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();
        _services = services;
        _tokens = services.GetRequiredService<ServiceTokenProvider>();
        _tokens.Changed += () => Dispatcher.Invoke(UpdateMenu);
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        LoginError.Text = "";
        var login = LoginBox.Text;
        var password = PasswordBox.Password;

        // Локальная учётка (хэш в RibbixNews.settings.json) — входим сразу, без ожидания сети,
        // а service-токен для раздела «Подписки» пробуем получить у сервера в фоне.
        if (LocalStaff.Verify(login, password) is { } role)
        {
            _tokens.LoginLocal(login.Trim(), role);
            PasswordBox.Clear();
            _ = TryServerLoginAsync(login, password);
            return;
        }

        try
        {
            await _tokens.LoginAsync(login, password, CancellationToken.None);
            PasswordBox.Clear();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            LoginError.Text = "Неверный логин или пароль (сервер недоступен, проверены только локальные учётки)";
        }
        catch (AdminApiException ex)
        {
            LoginError.Text = ex.Message;
        }
    }

    private async Task TryServerLoginAsync(string login, string password)
    {
        try { await _tokens.LoginAsync(login, password, CancellationToken.None); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or AdminApiException) { }
    }

    private void UpdateMenu()
    {
        var loggedIn = _tokens.Login is not null;
        LoginPanel.Visibility = loggedIn ? Visibility.Collapsed : Visibility.Visible;
        LogoutMenu.Visibility = loggedIn ? Visibility.Visible : Visibility.Collapsed;
        // Ключевое правило: пункт виден только роли superadmin из подписанного токена.
        SubscriptionsMenu.Visibility = SubscriptionsMenu_IsVisible() ? Visibility.Visible : Visibility.Collapsed;
        if (!loggedIn) Host.Content = null;
    }

    private bool SubscriptionsMenu_IsVisible() => RibbixNews.Subscriptions.Services.SubscriptionsMenu.IsVisible(_tokens);

    private void Subscriptions_Click(object sender, RoutedEventArgs e)
    {
        if (!SubscriptionsMenu_IsVisible()) return;
        Host.Content = new SubscriptionsView { DataContext = _services.GetRequiredService<SubscriptionsViewModel>() };
    }

    private void Logout_Click(object sender, RoutedEventArgs e) => _tokens.Logout();
}

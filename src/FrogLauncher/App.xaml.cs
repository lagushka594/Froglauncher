using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using FrogLauncher.Core;
using FrogLauncher.Services;
using FrogLauncher.ViewModels;
using FrogLauncher.Views;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Polly;
using Serilog;

namespace FrogLauncher;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstance? _single;

    public static IServiceProvider Services => ((App)Current)._host!.Services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Второй запуск (ссылка froglauncher:// из браузера) — передаём ссылку первому и выходим.
        _single = new SingleInstance();
        if (!_single.IsFirst)
        {
            SingleInstance.SendToFirst(e.Args.FirstOrDefault() ?? "froglauncher://focus");
            Shutdown();
            return;
        }

        LauncherPaths.EnsureCreated();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(LauncherPaths.Logs, "launcher-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7)
            .CreateLogger();

        DispatcherUnhandledException += OnUiException;

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.AddEnvironmentVariables("FROGLAUNCHER_");
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();
        ConfigureServices(builder.Services, builder.Configuration);
        _host = builder.Build();
        await _host.StartAsync();

        try { UrlScheme.Register(Environment.ProcessPath!); }
        catch (Exception ex) { Log.Warning(ex, "Не удалось зарегистрировать froglauncher://"); }

        var main = Services.GetRequiredService<MainWindow>();
        var vm = Services.GetRequiredService<MainViewModel>();
        main.DataContext = vm;
        MainWindow = main;
        main.Show();
        await vm.InitializeAsync();

        _single.Listen(msg => Dispatcher.Invoke(() =>
        {
            main.Activate();
            if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
            if (UrlScheme.TryParse(msg, out var uri)) vm.HandleDeepLink(uri);
        }));

        if (UrlScheme.TryParse(e.Args.FirstOrDefault(), out var startUri)) vm.HandleDeepLink(startUri);
    }

    private static void ConfigureServices(IServiceCollection s, IConfiguration cfg)
    {
        s.Configure<LauncherOptions>(cfg.GetSection(LauncherOptions.Section));
        var apiBase = cfg.GetSection(LauncherOptions.Section).Get<LauncherOptions>()?.ApiBaseUrl ?? new LauncherOptions().ApiBaseUrl;

        // Все HTTP: 3 повтора, экспоненциальная задержка 2/4/8 с.
        static IAsyncPolicy<HttpResponseMessage> Retry(PolicyBuilder<HttpResponseMessage> p) =>
            p.WaitAndRetryAsync(3, i => TimeSpan.FromSeconds(Math.Pow(2, i)));

        s.AddHttpClient(DownloadService.HttpClientName, c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd($"FrogLauncher/{LaunchService.LauncherVersion} (+https://github.com/lagushka594/Froglauncher)");
            c.Timeout = TimeSpan.FromMinutes(10);
        });
        s.AddHttpClient(FrogApiClient.RawClientName, c => { c.BaseAddress = new Uri(apiBase); c.Timeout = TimeSpan.FromSeconds(20); })
            .AddTransientHttpErrorPolicy(Retry);
        s.AddTransient<AuthHandler>();
        s.AddHttpClient(FrogApiClient.ClientName, c => { c.BaseAddress = new Uri(apiBase); c.Timeout = TimeSpan.FromSeconds(20); })
            .AddHttpMessageHandler<AuthHandler>()
            .AddTransientHttpErrorPolicy(Retry);

        s.AddSingleton(TimeProvider.System);
        s.AddSingleton<SessionStore>();
        s.AddSingleton<FrogApiClient>();
        s.AddSingleton(_ => EmbeddedTiers.Load());
        s.AddSingleton<DiscordAuthService>();
        s.AddSingleton<EntitlementService>();
        s.AddSingleton<DownloadService>();
        s.AddSingleton<VersionService>();
        s.AddSingleton<JavaService>();
        s.AddSingleton<ModLoaderService>();
        s.AddSingleton<ModrinthService>();
        s.AddSingleton<ModpackService>();
        s.AddSingleton<GameAuthService>();
        s.AddSingleton<LaunchService>();
        s.AddSingleton<InstanceService>();
        s.AddSingleton<SettingsService>();
        s.AddSingleton<ThemeService>();
        s.AddSingleton<ToastService>();
        s.AddSingleton<DiscordPresenceService>();

        s.AddSingleton<MainViewModel>();
        s.AddSingleton<HomeViewModel>();
        s.AddSingleton<InstancesViewModel>();
        s.AddSingleton<ModpacksViewModel>();
        s.AddSingleton<JavaViewModel>();
        s.AddSingleton<SettingsViewModel>();
        s.AddSingleton<ProfileViewModel>();
        s.AddSingleton<SubscriptionViewModel>();
        s.AddSingleton<AboutViewModel>();
        s.AddSingleton<MainWindow>();
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Необработанная ошибка UI");
        Services.GetService<ToastService>()?.Show("Что-то пошло не так: " + e.Exception.Message, ToastKind.Error);
        e.Handled = true;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await Services.GetRequiredService<DiscordPresenceService>().DisposeAsync();
            await _host.StopAsync(TimeSpan.FromSeconds(2));
            _host.Dispose();
        }
        _single?.Dispose();
        await Log.CloseAndFlushAsync();
        base.OnExit(e);
    }
}

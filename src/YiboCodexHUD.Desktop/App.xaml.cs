using System.Windows;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YiboCodexHUD.Desktop.ViewModels;
using YiboCodexHUD.Desktop.Views;
using YiboCodexHUD.Infrastructure.Extensions;
using YiboCodexHUD.WindowsInterop.Extensions;

namespace YiboCodexHUD.Desktop;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\YiboCodexHUD.Desktop.Singleton", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        _host = Host.CreateDefaultBuilder(e.Args)
            .ConfigureAppConfiguration(config =>
            {
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddDebug();
            })
            .ConfigureServices((context, services) =>
            {
                services.AddInfrastructure(context.Configuration);
                services.AddWindowsInterop();
                services.AddSingleton<OverlayViewModel>();
                services.AddSingleton<OverlayWindow>();
                services.AddSingleton<SettingsWindow>();
            })
            .Build();

        await _host.StartAsync();

        var overlayWindow = _host.Services.GetRequiredService<OverlayWindow>();
        overlayWindow.Show();

        _ = _host.Services.GetRequiredService<OverlayViewModel>().InitializeAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;

        base.OnExit(e);
    }
}

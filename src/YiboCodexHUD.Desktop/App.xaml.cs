using System.Windows;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YiboCodexHUD.Desktop.Services;
using YiboCodexHUD.Desktop.ViewModels;
using YiboCodexHUD.Desktop.Views;
using YiboCodexHUD.Infrastructure.Extensions;
using YiboCodexHUD.WindowsInterop.Extensions;

namespace YiboCodexHUD.Desktop;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private static bool _ownsSingleInstanceMutex;
    private IHost? _host;
    private bool _cleanupCompleted;

    public static bool IsShutdownRequested { get; private set; }

    public static void RequestShutdown()
    {
        IsShutdownRequested = true;

        // Hidden WPF windows still keep the process alive. Close every window
        // explicitly before shutting down so the HUD cannot remain headless.
        foreach (Window window in Current.Windows.Cast<Window>().ToArray())
        {
            window.Close();
        }

        Current.Shutdown();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceCommandProtocol.MutexName, out var createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            await SingleInstanceCommandClient.TrySendLaunchOrFocusCodexAsync(TimeSpan.FromSeconds(2));
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
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff ";
                });
                logging.AddDebug();
            })
            .ConfigureServices((context, services) =>
            {
                services.AddInfrastructure(context.Configuration);
                services.AddWindowsInterop();
                services.AddSingleton<CodexActivationService>();
                services.AddHostedService<SingleInstanceCommandServer>();
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

    protected override void OnExit(ExitEventArgs e)
    {
        IsShutdownRequested = true;
        CleanupApplicationResources();
        base.OnExit(e);
    }

    private void CleanupApplicationResources()
    {
        if (_cleanupCompleted)
        {
            return;
        }

        _cleanupCompleted = true;
        if (_host is not null)
        {
            _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            _host.Dispose();
            _host = null;
        }

        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        _ownsSingleInstanceMutex = false;
    }
}

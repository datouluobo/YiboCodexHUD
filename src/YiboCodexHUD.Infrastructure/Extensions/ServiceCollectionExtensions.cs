using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using YiboCodexHUD.Core.Abstractions;
using YiboCodexHUD.Infrastructure.Options;
using YiboCodexHUD.Infrastructure.Services;

namespace YiboCodexHUD.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CodexAppServerOptions>(configuration.GetSection(CodexAppServerOptions.SectionName));
        services.Configure<OverlayRefreshOptions>(configuration.GetSection(OverlayRefreshOptions.SectionName));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IHudSettingsStore, HudSettingsStore>();
        services.AddSingleton<CodexAppServerProcess>();
        services.AddSingleton<CodexDesktopLauncher>();
        services.AddSingleton<CodexProtocolClient>();
        services.AddSingleton<RateLimitResetCreditWebService>();
        services.AddSingleton<IRateLimitService, RateLimitService>();
        return services;
    }
}

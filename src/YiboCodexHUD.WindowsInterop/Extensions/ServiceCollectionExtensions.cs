using Microsoft.Extensions.DependencyInjection;
using YiboCodexHUD.Core.Abstractions;
using YiboCodexHUD.WindowsInterop.Services;

namespace YiboCodexHUD.WindowsInterop.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsInterop(this IServiceCollection services)
    {
        services.AddSingleton<ICodexWindowTracker, Win32CodexWindowTracker>();
        return services;
    }
}

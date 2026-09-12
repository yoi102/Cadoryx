using Microsoft.Extensions.DependencyInjection;

namespace Cadoryx.ViewModels;

using Cadoryx.ViewModels.Toolboxes;

public static class ServiceCollectionExtension
{
    public static IServiceCollection AddViewModels(this IServiceCollection services)
    {
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<ModelTreeToolboxViewModel>();
        services.AddTransient<PropertiesToolboxViewModel>();
        services.AddTransient<MessagesToolboxViewModel>();
        return services;
    }

}

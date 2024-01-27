using AspNetCore.Ignite.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AspNetCore.Ignite;

public static class IgniteExtensions
{
    public static IServiceCollection AddIgnite(this IServiceCollection services)
    {
        services.AddSingleton<IIgniteManager, IgniteManager>();
        return services;
    }
}

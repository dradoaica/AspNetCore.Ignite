namespace AspNetCore.Ignite;

using Abstractions;
using Microsoft.Extensions.DependencyInjection;

public static class IgniteExtensions
{
    public static IServiceCollection AddIgnite(this IServiceCollection services)
    {
        services.AddSingleton<IIgniteManager, IgniteManager>();
        return services;
    }
}

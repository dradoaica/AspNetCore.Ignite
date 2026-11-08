using AspNetCore.Ignite.Abstractions;
using AspNetCore.Ignite.HealthChecks;
using AspNetCore.Ignite.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;

namespace AspNetCore.Ignite;

/// <summary>Registers the Ignite client (<see cref="IIgniteManager" />) and its health check.</summary>
public static class IgniteExtensions
{
    /// <summary>
    ///     Registers <see cref="IIgniteManager" />, configured by the flat <c>ASPNETCORE_IGNITE_*</c> keys of the
    ///     registered <see cref="IConfiguration" /> (if any), then by <paramref name="configure" />.
    /// </summary>
    public static IServiceCollection AddIgnite(
        this IServiceCollection services,
        Action<IgniteClientOptions>? configure = null
    )
    {
        services.AddOptions<IgniteClientOptions>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<IgniteClientOptions>, ConfigurationKeysSetup>()
        );
        if (configure != null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton(sp => new IgniteManager(
                sp.GetRequiredService<IOptions<IgniteClientOptions>>(),
                sp.GetService<ILoggerFactory>()
            )
        );
        services.TryAddSingleton<IIgniteManager>(sp => sp.GetRequiredService<IgniteManager>());

        return services;
    }

    /// <summary>
    ///     Registers <see cref="IIgniteManager" />, configured by a configuration section bound to
    ///     <see cref="IgniteClientOptions" /> (e.g., <c>"Ignite": { "Endpoints": [ "ignite:10800" ], ... }</c>).
    /// </summary>
    public static IServiceCollection AddIgnite(this IServiceCollection services, IConfiguration section)
    {
        services.AddIgnite();
        services.Configure<IgniteClientOptions>(section);

        return services;
    }

    /// <summary>Adds a health check that is healthy when the Ignite cluster is reachable and active.</summary>
    public static IHealthChecksBuilder AddIgnite(
        this IHealthChecksBuilder builder,
        string name = "ignite",
        HealthStatus? failureStatus = null,
        IEnumerable<string>? tags = null,
        TimeSpan? timeout = null
    ) => builder.Add(
        new HealthCheckRegistration(
            name,
            sp => new IgniteHealthCheck(
                sp.GetRequiredService<IIgniteManager>(),
                timeout ?? IgniteHealthCheck.DefaultTimeout
            ),
            failureStatus,
            tags
        )
    );

    private sealed class ConfigurationKeysSetup(IServiceProvider serviceProvider)
        : IConfigureOptions<IgniteClientOptions>
    {
        public void Configure(IgniteClientOptions options)
        {
            if (serviceProvider.GetService<IConfiguration>() is { } configuration)
            {
                options.ApplyConfigurationKeys(configuration);
            }
        }
    }
}

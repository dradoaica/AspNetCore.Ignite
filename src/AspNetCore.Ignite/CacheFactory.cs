using Apache.Ignite.Core;
using Apache.Ignite.Core.Cache;
using Apache.Ignite.Core.Cache.Configuration;
using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
using Apache.Ignite.Core.Log;
using AspNetCore.Ignite.Options;
using AspNetCore.Ignite.Utils;
using System;
using System.Linq;
using System.Security.Authentication;

namespace AspNetCore.Ignite;

public static class CacheFactory
{
    public const string DefaultEndpoint = "127.0.0.1";

    public static IgniteClientConfiguration GetIgniteClientConfiguration(
        string? endpoint = DefaultEndpoint,
        string? userName = null,
        string? password = null,
        bool useSsl = false,
        string? certificatePath = null,
        string? certificatePassword = null,
        bool skipServerCertificateValidation = false,
        bool checkCertificateRevocation = true,
        ILogger? logger = null
    ) => GetIgniteClientConfiguration(
        new IgniteClientOptions
        {
            Endpoints = string.IsNullOrWhiteSpace(endpoint) ? [] : [endpoint!,],
            UserName = userName,
            Password = password,
            UseSsl = useSsl,
            SslCertificatePath = certificatePath,
            SslCertificatePassword = certificatePassword,
            SslSkipServerCertificateValidation = skipServerCertificateValidation,
            SslCheckCertificateRevocation = checkCertificateRevocation,
        },
        logger
    );

    public static IgniteClientConfiguration GetIgniteClientConfiguration(IgniteClientOptions options, ILogger? logger = null)
    {
        var endpoints = options.Endpoints.Where(endpoint => !string.IsNullOrWhiteSpace(endpoint)).ToList();
        IgniteClientConfiguration igniteClientConfiguration = new()
        {
            Endpoints = endpoints.Count > 0 ? endpoints : [DefaultEndpoint,],
            RetryPolicy = new ClientRetryReadPolicy(),
            RetryLimit = options.RetryLimit,
            SocketTimeout = options.SocketTimeout,
            EnablePartitionAwareness = true,
            EnableHeartbeats = true,
            Logger = logger,
        };
        if (!string.IsNullOrWhiteSpace(options.UserName))
        {
            igniteClientConfiguration.UserName = options.UserName;
        }

        if (!string.IsNullOrWhiteSpace(options.Password))
        {
            igniteClientConfiguration.Password = options.Password;
        }

        if (options.UseSsl)
        {
            igniteClientConfiguration.SslStreamFactory = new SslStreamFactory
            {
                CertificatePath = options.SslCertificatePath,
                CertificatePassword = options.SslCertificatePassword,
                CheckCertificateRevocation = options.SslCheckCertificateRevocation,
                SkipServerCertificateValidation = options.SslSkipServerCertificateValidation,
                // None lets the OS choose the best protocol it supports (TLS 1.3 included), as Microsoft recommends
                SslProtocols = SslProtocols.None,
            };
        }

        return igniteClientConfiguration;
    }

    public static IIgnite ConnectAsClient(IgniteConfiguration igniteConfiguration)
    {
        igniteConfiguration.ClientMode = true;

        return Ignition.Start(igniteConfiguration);
    }

    public static IIgniteClient ConnectAsClient(IgniteClientConfiguration igniteClientConfiguration) =>
        Ignition.StartClient(igniteClientConfiguration);

    public static ICache<TKey, TData> GetOrCreateCache<TKey, TData>(
        IIgnite ignite,
        string cacheName,
        Action<CacheConfiguration>? extendConfigurationAction = null,
        IgniteCacheOptions? cacheOptions = null
    )
    {
        cacheOptions ??= new IgniteCacheOptions();
        CacheConfiguration cacheCfg = new()
        {
            Name = cacheName,
            CacheMode = CacheMode.Partitioned,
            GroupName = GetGroupName<TData>(cacheOptions),
            QueryEntities =
            [
                new QueryEntity
                {
                    KeyType = typeof(TKey),
                    ValueType = typeof(TData),
                },
            ],
            Backups = cacheOptions.Backups,
            PlatformCacheConfiguration = GetPlatformCacheConfiguration<TKey, TData>(cacheOptions),
        };
        extendConfigurationAction?.Invoke(cacheCfg);

        return ignite.GetOrCreateCache<TKey, TData>(cacheCfg);
    }

    public static ICacheClient<TKey, TData> GetOrCreateCacheClient<TKey, TData>(
        IIgniteClient ignite,
        string cacheName,
        Action<CacheClientConfiguration>? extendConfigurationAction = null,
        IgniteCacheOptions? cacheOptions = null
    )
    {
        cacheOptions ??= new IgniteCacheOptions();
        CacheClientConfiguration cacheCfg = new(
            new CacheConfiguration
            {
                PlatformCacheConfiguration = GetPlatformCacheConfiguration<TKey, TData>(cacheOptions),
            },
            true
        )
        {
            Name = cacheName,
            CacheMode = CacheMode.Partitioned,
            GroupName = GetGroupName<TData>(cacheOptions),
            QueryEntities =
            [
                new QueryEntity
                {
                    KeyType = typeof(TKey),
                    ValueType = typeof(TData),
                },
            ],
            Backups = cacheOptions.Backups,
        };
        extendConfigurationAction?.Invoke(cacheCfg);

        return ignite.GetOrCreateCache<TKey, TData>(cacheCfg);
    }

    internal static string? GetGroupName<TData>(IgniteCacheOptions cacheOptions) =>
        cacheOptions.GroupByValueType ? typeof(TData).FullNameWithoutAssemblyInfo() : null;

    internal static PlatformCacheConfiguration? GetPlatformCacheConfiguration<TKey, TData>(
        IgniteCacheOptions cacheOptions
    ) => cacheOptions.EnablePlatformCache
        ? new PlatformCacheConfiguration
        {
            KeyTypeName = typeof(TKey).FullNameWithoutAssemblyInfo(),
            ValueTypeName = typeof(TData).FullNameWithoutAssemblyInfo(),
        }
        : null;
}

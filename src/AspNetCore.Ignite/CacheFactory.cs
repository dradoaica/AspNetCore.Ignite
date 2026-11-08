using System;
using System.Linq;
using System.Security.Authentication;
using Apache.Ignite.Core;
using Apache.Ignite.Core.Cache;
using Apache.Ignite.Core.Cache.Configuration;
using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
using Apache.Ignite.Core.Log;
using AspNetCore.Ignite.Options;
using AspNetCore.Ignite.Utils;

namespace AspNetCore.Ignite;

/// <summary>Creates thin client configurations, connections and caches with the defaults of this library.</summary>
public static class CacheFactory
{
    /// <summary>The endpoint used when none is configured.</summary>
    public const string DefaultEndpoint = "127.0.0.1";

    /// <summary>A thin client configuration for one endpoint.</summary>
    /// <param name="endpoint">The endpoint (host or host:port); <see cref="DefaultEndpoint" /> when empty.</param>
    /// <param name="userName">The user name (authentication).</param>
    /// <param name="password">The password (authentication).</param>
    /// <param name="useSsl">Connects with SSL.</param>
    /// <param name="certificatePath">The client certificate (PKCS12), when the server requires one.</param>
    /// <param name="certificatePassword">The password of the client certificate.</param>
    /// <param name="skipServerCertificateValidation">Skips the server certificate validation: insecure, for testing only.</param>
    /// <param name="checkCertificateRevocation">Checks the revocation of the server certificate.</param>
    /// <param name="logger">The Ignite logger of the client (none when null).</param>
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
    )
    {
        return GetIgniteClientConfiguration(
            new IgniteClientOptions
            {
                Endpoints = string.IsNullOrWhiteSpace(endpoint) ? [] : [endpoint!],
                UserName = userName,
                Password = password,
                UseSsl = useSsl,
                SslCertificatePath = certificatePath,
                SslCertificatePassword = certificatePassword,
                SslSkipServerCertificateValidation = skipServerCertificateValidation,
                SslCheckCertificateRevocation = checkCertificateRevocation
            },
            logger
        );
    }

    /// <summary>A thin client configuration from <paramref name="options" />.</summary>
    /// <param name="options">The options.</param>
    /// <param name="logger">The Ignite logger of the client (none when null).</param>
    public static IgniteClientConfiguration GetIgniteClientConfiguration(IgniteClientOptions options,
        ILogger? logger = null)
    {
        var endpoints = options.Endpoints.Where(endpoint => !string.IsNullOrWhiteSpace(endpoint)).ToList();
        IgniteClientConfiguration igniteClientConfiguration = new()
        {
            Endpoints = endpoints.Count > 0 ? endpoints : [DefaultEndpoint],
            RetryPolicy = new ClientRetryReadPolicy(),
            RetryLimit = options.RetryLimit,
            SocketTimeout = options.SocketTimeout,
            EnablePartitionAwareness = true,
            EnableClusterDiscovery = options.EnableClusterDiscovery,
            EnableHeartbeats = true,
            Logger = logger
        };
        if (!string.IsNullOrWhiteSpace(options.UserName)) igniteClientConfiguration.UserName = options.UserName;

        if (!string.IsNullOrWhiteSpace(options.Password)) igniteClientConfiguration.Password = options.Password;

        if (options.UseSsl)
            igniteClientConfiguration.SslStreamFactory = new SslStreamFactory
            {
                CertificatePath = options.SslCertificatePath,
                CertificatePassword = options.SslCertificatePassword,
                CheckCertificateRevocation = options.SslCheckCertificateRevocation,
                SkipServerCertificateValidation = options.SslSkipServerCertificateValidation,
                // None lets the OS choose the best protocol it supports (TLS 1.3 included), as Microsoft recommends
                SslProtocols = SslProtocols.None
            };

        return igniteClientConfiguration;
    }

    /// <summary>Starts a thick client node (in this process) with <paramref name="igniteConfiguration" />, set to client mode.</summary>
    /// <param name="igniteConfiguration">The node configuration.</param>
    public static IIgnite ConnectAsClient(IgniteConfiguration igniteConfiguration)
    {
        igniteConfiguration.ClientMode = true;

        return Ignition.Start(igniteConfiguration);
    }

    /// <summary>Connects a thin client.</summary>
    /// <param name="igniteClientConfiguration">The thin client configuration.</param>
    public static IIgniteClient ConnectAsClient(IgniteClientConfiguration igniteClientConfiguration)
    {
        return Ignition.StartClient(igniteClientConfiguration);
    }

    /// <summary>Gets or creates a partitioned cache through a thick client or server node.</summary>
    /// <param name="ignite">The node.</param>
    /// <param name="cacheName">The cache name.</param>
    /// <param name="extendConfigurationAction">Adjusts the configuration of a cache that is created.</param>
    /// <param name="cacheOptions">The cache defaults (<see cref="IgniteCacheOptions" /> when null).</param>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TData">The value type.</typeparam>
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
                    ValueType = typeof(TData)
                }
            ],
            Backups = cacheOptions.Backups,
            PlatformCacheConfiguration = GetPlatformCacheConfiguration<TKey, TData>(cacheOptions)
        };
        extendConfigurationAction?.Invoke(cacheCfg);

        return ignite.GetOrCreateCache<TKey, TData>(cacheCfg);
    }

    /// <summary>Gets or creates a partitioned cache through a thin client.</summary>
    /// <param name="ignite">The thin client.</param>
    /// <param name="cacheName">The cache name.</param>
    /// <param name="extendConfigurationAction">Adjusts the configuration of a cache that is created.</param>
    /// <param name="cacheOptions">The cache defaults (<see cref="IgniteCacheOptions" /> when null).</param>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TData">The value type.</typeparam>
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
                PlatformCacheConfiguration = GetPlatformCacheConfiguration<TKey, TData>(cacheOptions)
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
                    ValueType = typeof(TData)
                }
            ],
            Backups = cacheOptions.Backups
        };
        extendConfigurationAction?.Invoke(cacheCfg);

        return ignite.GetOrCreateCache<TKey, TData>(cacheCfg);
    }

    internal static string? GetGroupName<TData>(IgniteCacheOptions cacheOptions)
    {
        return cacheOptions.GroupByValueType ? typeof(TData).FullNameWithoutAssemblyInfo() : null;
    }

    internal static PlatformCacheConfiguration? GetPlatformCacheConfiguration<TKey, TData>(
        IgniteCacheOptions cacheOptions
    )
    {
        return cacheOptions.EnablePlatformCache
            ? new PlatformCacheConfiguration
            {
                KeyTypeName = typeof(TKey).FullNameWithoutAssemblyInfo(),
                ValueTypeName = typeof(TData).FullNameWithoutAssemblyInfo()
            }
            : null;
    }
}
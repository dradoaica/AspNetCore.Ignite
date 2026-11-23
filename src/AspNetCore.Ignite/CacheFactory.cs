using Apache.Ignite.Core;
using Apache.Ignite.Core.Cache;
using Apache.Ignite.Core.Cache.Configuration;
using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
using Apache.Ignite.Core.Log;
using AspNetCore.Ignite.Options;
using AspNetCore.Ignite.Utils;
using System;

namespace AspNetCore.Ignite;

/// <summary>
///     Creates caches (partitioned, with the defaults of this library) through thin clients, thick clients and server
///     nodes. Its client configuration and connection methods moved to <see cref="IgniteClientFactory" />: the ones
///     here are obsolete and forward to it.
/// </summary>
public static class CacheFactory
{
    /// <summary>The endpoint used when none is configured.</summary>
    [Obsolete("Use IgniteClientFactory.DefaultEndpoint.")]
    public const string DefaultEndpoint = IgniteClientFactory.DefaultEndpoint;

    /// <summary>A thin client configuration for one endpoint.</summary>
    /// <param name="endpoint">The endpoint (host or host:port); the default endpoint when empty.</param>
    /// <param name="userName">The user name (authentication).</param>
    /// <param name="password">The password (authentication).</param>
    /// <param name="useSsl">Connects with SSL.</param>
    /// <param name="certificatePath">The client certificate (PKCS12), when the server requires one.</param>
    /// <param name="certificatePassword">The password of the client certificate.</param>
    /// <param name="skipServerCertificateValidation">Skips the server certificate validation: insecure, for testing only.</param>
    /// <param name="checkCertificateRevocation">Checks the revocation of the server certificate.</param>
    /// <param name="logger">The Ignite logger of the client (none when null).</param>
    [Obsolete("Use IgniteClientFactory.GetIgniteClientConfiguration.")]
    public static IgniteClientConfiguration GetIgniteClientConfiguration(
        string? endpoint = IgniteClientFactory.DefaultEndpoint,
        string? userName = null,
        string? password = null,
        bool useSsl = false,
        string? certificatePath = null,
        string? certificatePassword = null,
        bool skipServerCertificateValidation = false,
        bool checkCertificateRevocation = true,
        ILogger? logger = null
    ) => IgniteClientFactory.GetIgniteClientConfiguration(
        endpoint,
        userName,
        password,
        useSsl,
        certificatePath,
        certificatePassword,
        skipServerCertificateValidation,
        checkCertificateRevocation,
        logger
    );

    /// <summary>A thin client configuration from <paramref name="options" />.</summary>
    /// <param name="options">The options.</param>
    /// <param name="logger">The Ignite logger of the client (none when null).</param>
    [Obsolete("Use IgniteClientFactory.GetIgniteClientConfiguration.")]
    public static IgniteClientConfiguration GetIgniteClientConfiguration(
        IgniteClientOptions options,
        ILogger? logger = null
    ) => IgniteClientFactory.GetIgniteClientConfiguration(options, logger);

    /// <summary>Starts a thick client node (in this process) with <paramref name="igniteConfiguration" />.</summary>
    /// <param name="igniteConfiguration">The node configuration; its <c>ClientMode</c> is set to true.</param>
    [Obsolete("Use IgniteClientFactory.ConnectAsClient.")]
    public static IIgnite ConnectAsClient(IgniteConfiguration igniteConfiguration) =>
        IgniteClientFactory.ConnectAsClient(igniteConfiguration);

    /// <summary>Connects a thin client.</summary>
    /// <param name="igniteClientConfiguration">The thin client configuration.</param>
    [Obsolete("Use IgniteClientFactory.ConnectAsClient.")]
    public static IIgniteClient ConnectAsClient(IgniteClientConfiguration igniteClientConfiguration) =>
        IgniteClientFactory.ConnectAsClient(igniteClientConfiguration);

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
                    ValueType = typeof(TData),
                },
            ],
            Backups = cacheOptions.Backups,
            PlatformCacheConfiguration = GetPlatformCacheConfiguration<TKey, TData>(cacheOptions),
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
    /// <exception cref="NotSupportedException">
    ///     <see cref="IgniteCacheOptions.EnablePlatformCache" /> is set: a thin client
    ///     cannot configure a platform cache.
    /// </exception>
    public static ICacheClient<TKey, TData> GetOrCreateCacheClient<TKey, TData>(
        IIgniteClient ignite,
        string cacheName,
        Action<CacheClientConfiguration>? extendConfigurationAction = null,
        IgniteCacheOptions? cacheOptions = null
    )
    {
        cacheOptions ??= new IgniteCacheOptions();
        if (cacheOptions.EnablePlatformCache)
        {
            // The thin client protocol has no platform cache configuration, so it would be dropped silently.
            throw new NotSupportedException(
                "EnablePlatformCache applies only to caches created through a thick client or a server node " +
                "(CacheFactory.GetOrCreateCache); a thin client cannot configure a platform cache."
            );
        }

        CacheClientConfiguration cacheCfg = new()
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
    ) => cacheOptions.EnablePlatformCache ? new PlatformCacheConfiguration
    {
        KeyTypeName = typeof(TKey).FullNameWithoutAssemblyInfo(),
        ValueTypeName = typeof(TData).FullNameWithoutAssemblyInfo(),
    } : null;
}

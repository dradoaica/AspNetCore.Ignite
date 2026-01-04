using Apache.Ignite.Core;
using Apache.Ignite.Core.Cache;
using Apache.Ignite.Core.Cache.Configuration;
using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
using Apache.Ignite.Core.Log;
using AspNetCore.Ignite.Utils;
using System;
using System.Security.Authentication;

namespace AspNetCore.Ignite;

public static class CacheFactory
{
    public static IgniteClientConfiguration GetIgniteClientConfiguration(
        string endpoint = "127.0.0.1",
        string userName = null,
        string password = null,
        bool useSsl = false,
        string certificatePath = null,
        string certificatePassword = null
    )
    {
        IgniteClientConfiguration igniteClientConfiguration = new()
        {
            Endpoints =
            [
                endpoint,
            ],
            RetryPolicy = new ClientRetryReadPolicy(),
            RetryLimit = 5,
            SocketTimeout = TimeSpan.FromSeconds(30),
            EnablePartitionAwareness = true,
            EnableHeartbeats = true,
            // Enable trace logging to observe the discovery process.
            Logger = new ConsoleLogger
            {
                MinLevel = LogLevel.Trace,
            },
        };
        if (!string.IsNullOrWhiteSpace(userName))
        {
            igniteClientConfiguration.UserName = userName;
        }

        if (!string.IsNullOrWhiteSpace(password))
        {
            igniteClientConfiguration.Password = password;
        }

        if (useSsl)
        {
            igniteClientConfiguration.SslStreamFactory = new SslStreamFactory
            {
                CertificatePath = certificatePath,
                CertificatePassword = certificatePassword,
                CheckCertificateRevocation = true,
                SkipServerCertificateValidation = true,
                SslProtocols = SslProtocols.Tls12,
            };
        }

        return igniteClientConfiguration;
    }

    public static IIgnite ConnectAsClient(IgniteConfiguration igniteConfiguration)
    {
        Ignition.ClientMode = true;

        return Ignition.Start(igniteConfiguration);
    }

    public static IIgniteClient ConnectAsClient(IgniteClientConfiguration igniteClientConfiguration) =>
        Ignition.StartClient(igniteClientConfiguration);

    public static ICache<TKey, TData> GetOrCreateCache<TKey, TData>(
        IIgnite ignite,
        string cacheName,
        Action<CacheConfiguration> extendConfigurationAction = null
    )
    {
        CacheConfiguration cacheCfg = new()
        {
            Name = cacheName,
            CacheMode = CacheMode.Partitioned,
            GroupName = typeof(TData).FullNameWithoutAssemblyInfo(),
            QueryEntities =
            [
                new QueryEntity
                {
                    KeyType = typeof(TKey),
                    ValueType = typeof(TData),
                },
            ],
            Backups = 1,
            PlatformCacheConfiguration = new PlatformCacheConfiguration
            {
                KeyTypeName = typeof(TKey).FullNameWithoutAssemblyInfo(),
                ValueTypeName = typeof(TData).FullNameWithoutAssemblyInfo(),
            },
        };
        extendConfigurationAction?.Invoke(cacheCfg);

        return ignite.GetOrCreateCache<TKey, TData>(cacheCfg);
    }

    public static ICacheClient<TKey, TData> GetOrCreateCacheClient<TKey, TData>(
        IIgniteClient ignite,
        string cacheName,
        Action<CacheClientConfiguration> extendConfigurationAction = null
    )
    {
        CacheClientConfiguration cacheCfg = new(
            new CacheConfiguration
            {
                PlatformCacheConfiguration = new PlatformCacheConfiguration
                {
                    KeyTypeName = typeof(TKey).FullNameWithoutAssemblyInfo(),
                    ValueTypeName = typeof(TData).FullNameWithoutAssemblyInfo(),
                },
            },
            true
        )
        {
            Name = cacheName,
            CacheMode = CacheMode.Partitioned,
            GroupName = typeof(TData).FullNameWithoutAssemblyInfo(),
            QueryEntities =
            [
                new QueryEntity
                {
                    KeyType = typeof(TKey),
                    ValueType = typeof(TData),
                },
            ],
            Backups = 1,
        };
        extendConfigurationAction?.Invoke(cacheCfg);

        return ignite.GetOrCreateCache<TKey, TData>(cacheCfg);
    }
}

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

public class CacheFactory
{
    public static IgniteClientConfiguration GetIgniteClientConfiguration(string endpoint = "127.0.0.1",
        string userName = null, string password = null, bool useSsl = false,
        string certificatePath = null, string certificatePassword = null)
    {
        IgniteClientConfiguration igniteClientConfiguration = new()
        {
            Endpoints = new[] {endpoint},
            SocketTimeout = TimeSpan.FromSeconds(60),
            EnablePartitionAwareness = true,
            // Enable trace logging to observe discovery process.
            Logger = new ConsoleLogger {MinLevel = LogLevel.Trace}
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
                SslProtocols = SslProtocols.Tls12
            };
        }

        return igniteClientConfiguration;
    }

    public static IIgnite ConnectAsClient(IgniteConfiguration igniteConfiguration)
    {
        Ignition.ClientMode = true;
        return Ignition.Start(igniteConfiguration);
    }

    public static IIgniteClient ConnectAsClient(IgniteClientConfiguration igniteClientConfiguration)
    {
        return Ignition.StartClient(igniteClientConfiguration);
    }

    public static ICache<TKey, TData> GetOrCreateCache<TKey, TData>(IIgnite ignite, string cacheName,
        Action<CacheConfiguration> extendConfigurationAction = null)
    {
        CacheConfiguration cacheCfg = new()
        {
            Name = cacheName,
            CacheMode = CacheMode.Partitioned,
            GroupName = typeof(TData).FullNameWithoutAssemblyInfo(),
            QueryEntities = new[] {new QueryEntity {KeyType = typeof(TKey), ValueType = typeof(TData)}},
            PlatformCacheConfiguration = new PlatformCacheConfiguration
            {
                KeyTypeName = typeof(TKey).FullNameWithoutAssemblyInfo(),
                ValueTypeName = typeof(TData).FullNameWithoutAssemblyInfo()
            }
        };
        extendConfigurationAction?.Invoke(cacheCfg);
        ICache<TKey, TData> cache = ignite.GetOrCreateCache<TKey, TData>(cacheCfg);
        return cache;
    }

    public static ICacheClient<TKey, TData> GetOrCreateCacheClient<TKey, TData>(IIgniteClient ignite,
        string cacheName, Action<CacheClientConfiguration> extendConfigurationAction = null)
    {
        CacheClientConfiguration cacheCfg =
            new(
                new CacheConfiguration
                {
                    PlatformCacheConfiguration = new PlatformCacheConfiguration
                    {
                        KeyTypeName = typeof(TKey).FullNameWithoutAssemblyInfo(),
                        ValueTypeName = typeof(TData).FullNameWithoutAssemblyInfo()
                    }
                }, true)
            {
                Name = cacheName,
                CacheMode = CacheMode.Partitioned,
                GroupName = typeof(TData).FullNameWithoutAssemblyInfo(),
                QueryEntities = new[] {new QueryEntity {KeyType = typeof(TKey), ValueType = typeof(TData)}}
            };
        extendConfigurationAction?.Invoke(cacheCfg);
        return ignite.GetOrCreateCache<TKey, TData>(cacheCfg);
    }
}

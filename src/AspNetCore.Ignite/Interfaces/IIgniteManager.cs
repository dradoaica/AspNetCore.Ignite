namespace AspNetCore.Ignite.Interfaces;

using System;
using Apache.Ignite.Core.Client.Cache;

public interface IIgniteManager
{
    ICacheClient<TKey, TData> GetOrCreateCacheClient<TKey, TData>(string cacheName,
        Action<CacheClientConfiguration> extendConfigurationAction = null);

    void DestroyCache(string cacheName);
}

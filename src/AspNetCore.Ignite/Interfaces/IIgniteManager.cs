using Apache.Ignite.Core.Client.Cache;
using System;

namespace AspNetCore.Ignite.Interfaces;

public interface IIgniteManager
{
    ICacheClient<TKey, TData> GetOrCreateCacheClient<TKey, TData>(string cacheName,
        Action<CacheClientConfiguration> extendConfigurationAction = null);

    void DestroyCache(string cacheName);
}

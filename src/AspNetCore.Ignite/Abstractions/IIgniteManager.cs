using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
using System;

namespace AspNetCore.Ignite.Abstractions;

public interface IIgniteManager
{
    /// <summary>The thin client (connected on first use), for SQL, compute, transactions, cluster state, etc.</summary>
    IIgniteClient Client { get; }

    ICacheClient<TKey, TData> GetOrCreateCacheClient<TKey, TData>(
        string cacheName,
        Action<CacheClientConfiguration>? extendConfigurationAction = null
    );

    void DestroyCache(string cacheName);
}

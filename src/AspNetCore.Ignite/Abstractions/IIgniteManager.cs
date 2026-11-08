using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCore.Ignite.Abstractions;

/// <summary>The thin client connection to an Ignite cluster, and its caches (registered by <c>AddIgnite</c>).</summary>
public interface IIgniteManager
{
    /// <summary>The thin client (connected on first use), for SQL, compute, transactions, cluster state, etc.</summary>
    IIgniteClient Client { get; }

    /// <summary>
    ///     The thin client, connecting if needed; all callers share one connection attempt, so waiting for it (e.g., with
    ///     a timeout) does not tie up a thread per caller.
    /// </summary>
    Task<IIgniteClient> GetClientAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets the cache, creating it (partitioned, with the defaults of <c>IgniteClientOptions.Cache</c>) when it does
    ///     not exist; transient failures are retried.
    /// </summary>
    /// <param name="cacheName">The cache name.</param>
    /// <param name="extendConfigurationAction">Adjusts the configuration of a cache that is created.</param>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <typeparam name="TData">The value type.</typeparam>
    ICacheClient<TKey, TData> GetOrCreateCacheClient<TKey, TData>(
        string cacheName,
        Action<CacheClientConfiguration>? extendConfigurationAction = null
    );

    /// <summary>Destroys the cache; nothing happens when it does not exist.</summary>
    /// <param name="cacheName">The cache name.</param>
    void DestroyCache(string cacheName);
}

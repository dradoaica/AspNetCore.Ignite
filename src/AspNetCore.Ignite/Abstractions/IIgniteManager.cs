using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCore.Ignite.Abstractions;

/// <summary>The thin client connection to an Ignite cluster, and its caches (registered by <c>AddIgnite</c>).</summary>
public interface IIgniteManager
{
    /// <summary>
    ///     The thin client, for SQL, compute, transactions, cluster state, etc. The first use connects and blocks the
    ///     calling thread until it has connected, through all connection retries (about two minutes with the default
    ///     <c>ConnectRetryCount</c>). In asynchronous code, use <see cref="GetClientAsync" /> instead.
    /// </summary>
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

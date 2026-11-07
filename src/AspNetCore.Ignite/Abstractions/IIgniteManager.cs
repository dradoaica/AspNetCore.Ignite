using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCore.Ignite.Abstractions;

public interface IIgniteManager
{
    /// <summary>The thin client (connected on first use), for SQL, compute, transactions, cluster state, etc.</summary>
    IIgniteClient Client { get; }

    /// <summary>
    ///     The thin client, connecting if needed; all callers share one connection attempt, so waiting for it (e.g., with
    ///     a timeout) does not tie up a thread per caller.
    /// </summary>
    Task<IIgniteClient> GetClientAsync(CancellationToken cancellationToken = default);

    ICacheClient<TKey, TData> GetOrCreateCacheClient<TKey, TData>(
        string cacheName,
        Action<CacheClientConfiguration>? extendConfigurationAction = null
    );

    void DestroyCache(string cacheName);
}

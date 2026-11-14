using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
using AspNetCore.Ignite.Abstractions;
using AspNetCore.Ignite.Logging;
using AspNetCore.Ignite.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCore.Ignite;

/// <summary>The thin client connection (connected on first use, with retries) and its caches.</summary>
public class IgniteManager : IIgniteManager, IDisposable
{
    private readonly RetryPolicy connectRetryPolicy;
    private readonly IgniteClientConfiguration igniteClientConfiguration;
    private readonly RetryPolicy operationRetryPolicy;
    private readonly IgniteClientOptions options;
    private readonly object syncRoot = new();
    private Task<IIgniteClient>? connecting;
    private bool disposed;
    private IIgniteClient? igniteClient;

    /// <summary>Configured by the flat <c>ASPNETCORE_IGNITE_*</c> keys.</summary>
    public IgniteManager(IConfiguration configuration)
        : this(CreateOptions(configuration))
    {
    }

    /// <summary>Configured by <paramref name="options" />.</summary>
    /// <param name="options">The options.</param>
    /// <param name="loggerFactory">Where the client logs (category <c>Apache.Ignite</c>); none when null.</param>
    public IgniteManager(IOptions<IgniteClientOptions> options, ILoggerFactory? loggerFactory = null)
        : this(options.Value, loggerFactory)
    {
    }

    /// <summary>Configured by <paramref name="options" />.</summary>
    /// <param name="options">The options.</param>
    /// <param name="loggerFactory">Where the client logs (category <c>Apache.Ignite</c>); none when null.</param>
    public IgniteManager(IgniteClientOptions options, ILoggerFactory? loggerFactory = null)
    {
        this.options = options;
        igniteClientConfiguration = CacheFactory.GetIgniteClientConfiguration(
            options,
            loggerFactory == null ? null : new IgniteLoggerAdapter(loggerFactory)
        );
        connectRetryPolicy = CreateRetryPolicy(options.ConnectRetryCount);
        operationRetryPolicy = CreateRetryPolicy(options.OperationRetryCount);
    }

    /// <summary>Closes the connection.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public IIgniteClient Client => GetIgniteClient();

    /// <inheritdoc />
    // Cancelling stops only this caller's wait; the shared connection attempt goes on.
    public Task<IIgniteClient> GetClientAsync(CancellationToken cancellationToken = default) =>
        GetOrStartConnecting().WaitAsync(cancellationToken);

    /// <inheritdoc />
    public ICacheClient<TKey, TData> GetOrCreateCacheClient<TKey, TData>(
        string cacheName,
        Action<CacheClientConfiguration>? extendConfigurationAction = null
    )
    {
        // Connected first, outside the operation retries: a connection that failed after its own retries is not
        // retried again by each of them.
        var client = GetIgniteClient();
        return operationRetryPolicy.Execute(() => CacheFactory.GetOrCreateCacheClient<TKey, TData>(
                client,
                cacheName,
                extendConfigurationAction,
                options.Cache
            )
        );
    }

    /// <inheritdoc />
    public void DestroyCache(string cacheName)
    {
        try
        {
            GetIgniteClient().DestroyCache(cacheName);
        }
        catch (IgniteClientException icex) when (icex.StatusCode == ClientStatusCode.CacheDoesNotExist)
        {
            // already destroyed
        }
    }

    /// <summary>Closes the connection (when <paramref name="disposing" />).</summary>
    /// <param name="disposing">True when called by <see cref="Dispose()" />.</param>
    protected virtual void Dispose(bool disposing)
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            if (disposing)
            {
                igniteClient?.Dispose();
                igniteClient = null;
                connecting = null;
            }

            disposed = true;
        }
    }

    /// <summary>
    ///     Connects on first use (not during DI resolution); concurrent callers share one connection attempt, and a
    ///     failed attempt is retried by the next call.
    /// </summary>
    private IIgniteClient GetIgniteClient() => GetOrStartConnecting().GetAwaiter().GetResult();

    private Task<IIgniteClient> GetOrStartConnecting()
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (igniteClient != null)
            {
                return Task.FromResult(igniteClient);
            }

            if (connecting is not { IsFaulted: false, IsCanceled: false, })
            {
                // One background thread runs the connection retries, however many callers wait for the result.
                connecting = Task.Run(Connect);
            }

            return connecting;
        }
    }

    private IIgniteClient Connect()
    {
        var client = connectRetryPolicy.Execute(() => CacheFactory.ConnectAsClient(igniteClientConfiguration));
        lock (syncRoot)
        {
            if (disposed)
            {
                client.Dispose();
                throw new ObjectDisposedException(nameof(IgniteManager));
            }

            igniteClient = client;
        }

        return client;
    }

    private static IgniteClientOptions CreateOptions(IConfiguration configuration)
    {
        IgniteClientOptions options = new();
        options.ApplyConfigurationKeys(configuration);
        return options;
    }

    private static RetryPolicy CreateRetryPolicy(int retryCount) => Policy.Handle<Exception>(IsTransient)
        .WaitAndRetry(Math.Max(0, retryCount), retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

    internal static bool IsTransient(Exception ex) => ex switch
    {
        // Transient: a failed connection (socket, IO or timeout) and a cluster that is not active yet. Any other
        // answer of the server (e.g., an invalid cache configuration) would be the same on a retry.
        IgniteClientException { StatusCode: ClientStatusCode.InvalidNodeState, } => true,
        IgniteClientException { InnerException: { } inner, } => IsTransient(inner),
        IgniteClientException => false,
        AggregateException aex => aex.InnerExceptions.Count > 0 && aex.InnerExceptions.All(IsTransient),
        SocketException or IOException or TimeoutException => true,
        _ => false,
    };
}

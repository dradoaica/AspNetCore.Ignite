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

namespace AspNetCore.Ignite;

public class IgniteManager : IIgniteManager, IDisposable
{
    private readonly RetryPolicy connectRetryPolicy;
    private readonly IgniteClientConfiguration igniteClientConfiguration;
    private readonly RetryPolicy operationRetryPolicy;
    private readonly IgniteClientOptions options;
    private readonly object syncRoot = new();
    private bool disposed;
    private IIgniteClient? igniteClient;

    /// <summary>Configured by the flat <c>ASPNETCORE_IGNITE_*</c> keys.</summary>
    public IgniteManager(IConfiguration configuration) : this(CreateOptions(configuration))
    {
    }

    public IgniteManager(IOptions<IgniteClientOptions> options, ILoggerFactory? loggerFactory = null) : this(
        options.Value,
        loggerFactory
    )
    {
    }

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

    /// <inheritdoc />
    public IIgniteClient Client => GetIgniteClient();

    public ICacheClient<TKey, TData> GetOrCreateCacheClient<TKey, TData>(
        string cacheName,
        Action<CacheClientConfiguration>? extendConfigurationAction = null
    ) => operationRetryPolicy.Execute(() =>
        CacheFactory.GetOrCreateCacheClient<TKey, TData>(
            GetIgniteClient(),
            cacheName,
            extendConfigurationAction,
            options.Cache
        )
    );

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

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

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
            }

            disposed = true;
        }
    }

    /// <summary>
    ///     Connects on first use (not during DI resolution) and keeps retrying on later calls if connecting failed.
    /// </summary>
    private IIgniteClient GetIgniteClient()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(IgniteManager));
            }

            return igniteClient ??=
                connectRetryPolicy.Execute(() => CacheFactory.ConnectAsClient(igniteClientConfiguration));
        }
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
        IgniteClientException icex => icex.StatusCode is not (ClientStatusCode.AuthenticationFailed
            or ClientStatusCode.SecurityViolation),
        AggregateException aex => aex.InnerExceptions.Count > 0 && aex.InnerExceptions.All(IsTransient),
        SocketException or IOException or TimeoutException => true,
        _ => false,
    };
}

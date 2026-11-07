using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
using AspNetCore.Ignite.Abstractions;
using Microsoft.Extensions.Configuration;
using Polly;
using Polly.Retry;
using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;

namespace AspNetCore.Ignite;

public class IgniteManager : IIgniteManager, IDisposable
{
    private const int ConnectRetryCount = 6;
    private const int OperationRetryCount = 3;

    private static readonly RetryPolicy ConnectRetryPolicy = CreateRetryPolicy(ConnectRetryCount);
    private static readonly RetryPolicy OperationRetryPolicy = CreateRetryPolicy(OperationRetryCount);

    private readonly IgniteClientConfiguration igniteClientConfiguration;
    private readonly object syncRoot = new();
    private bool disposed;
    private IIgniteClient? igniteClient;

    public IgniteManager(IConfiguration configuration)
    {
        var aspNetCoreIgniteEndpoint = configuration["ASPNETCORE_IGNITE_ENDPOINT"];
        var aspNetCoreIgniteUserName = configuration["ASPNETCORE_IGNITE_USER_NAME"];
        var aspNetCoreIgnitePassword = configuration["ASPNETCORE_IGNITE_PASSWORD"];
        var aspNetCoreIgniteUseSsl = "true".Equals(
            configuration["ASPNETCORE_IGNITE_USE_SSL"],
            StringComparison.InvariantCultureIgnoreCase
        );
        var aspNetCoreIgniteSslCertificatePath = configuration["ASPNETCORE_IGNITE_SSL_CERTIFICATE_PATH"];
        var aspNetCoreIgniteSslCertificatePassword = configuration["ASPNETCORE_IGNITE_SSL_CERTIFICATE_PASSWORD"];
        var aspNetCoreIgniteSslSkipServerCertificateValidation = "true".Equals(
            configuration["ASPNETCORE_IGNITE_SSL_SKIP_SERVER_CERTIFICATE_VALIDATION"],
            StringComparison.InvariantCultureIgnoreCase
        );
        var aspNetCoreIgniteSslCheckCertificateRevocation = !"false".Equals(
            configuration["ASPNETCORE_IGNITE_SSL_CHECK_CERTIFICATE_REVOCATION"],
            StringComparison.InvariantCultureIgnoreCase
        );
        igniteClientConfiguration = CacheFactory.GetIgniteClientConfiguration(
            aspNetCoreIgniteEndpoint,
            aspNetCoreIgniteUserName,
            aspNetCoreIgnitePassword,
            aspNetCoreIgniteUseSsl,
            aspNetCoreIgniteSslCertificatePath,
            aspNetCoreIgniteSslCertificatePassword,
            aspNetCoreIgniteSslSkipServerCertificateValidation,
            aspNetCoreIgniteSslCheckCertificateRevocation
        );
    }

    public ICacheClient<TKey, TData> GetOrCreateCacheClient<TKey, TData>(
        string cacheName,
        Action<CacheClientConfiguration>? extendConfigurationAction = null
    ) => OperationRetryPolicy.Execute(() =>
        CacheFactory.GetOrCreateCacheClient<TKey, TData>(GetIgniteClient(), cacheName, extendConfigurationAction)
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
                ConnectRetryPolicy.Execute(() => CacheFactory.ConnectAsClient(igniteClientConfiguration));
        }
    }

    private static RetryPolicy CreateRetryPolicy(int retryCount) => Policy.Handle<Exception>(IsTransient)
        .WaitAndRetry(retryCount, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

    internal static bool IsTransient(Exception ex) => ex switch
    {
        IgniteClientException icex => icex.StatusCode is not (ClientStatusCode.AuthenticationFailed
            or ClientStatusCode.SecurityViolation),
        AggregateException aex => aex.InnerExceptions.Count > 0 && aex.InnerExceptions.All(IsTransient),
        SocketException or IOException or TimeoutException => true,
        _ => false,
    };
}

using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
using AspNetCore.Ignite.Abstractions;
using Microsoft.Extensions.Configuration;
using Polly;
using System;
using System.IO;

namespace AspNetCore.Ignite;

public class IgniteManager : IIgniteManager
{
    private readonly IIgniteClient igniteClient;

    public IgniteManager(IConfiguration configuration)
    {
        var aspNetCoreIgniteEndpoint = configuration["ASPNETCORE_IGNITE_ENDPOINT"];
        var aspNetCoreIgniteUserName = configuration["ASPNETCORE_IGNITE_USER_NAME"];
        var aspNetCoreIgnitePassword = configuration["ASPNETCORE_IGNITE_PASSWORD"];
        var aspNetCoreIgniteUseSsl = "true".Equals(
            configuration["ASPNETCORE_IGNITE_USE_SSL"],
            StringComparison.InvariantCultureIgnoreCase);
        var aspNetCoreIgniteSslCertificatePath = configuration["ASPNETCORE_IGNITE_SSL_CERTIFICATE_PATH"];
        var aspNetCoreIgniteSslCertificatePassword = configuration["ASPNETCORE_IGNITE_SSL_CERTIFICATE_PASSWORD"];
        var igniteClientConfiguration = CacheFactory.GetIgniteClientConfiguration(
            aspNetCoreIgniteEndpoint,
            aspNetCoreIgniteUserName,
            aspNetCoreIgnitePassword,
            aspNetCoreIgniteUseSsl,
            aspNetCoreIgniteSslCertificatePath,
            aspNetCoreIgniteSslCertificatePassword);
        igniteClient = Policy.Handle<Exception>()
            .WaitAndRetry(6, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)))
            .Execute(() => CacheFactory.ConnectAsClient(igniteClientConfiguration));
    }

    public ICacheClient<TKey, TData> GetOrCreateCacheClient<TKey, TData>(
        string cacheName,
        Action<CacheClientConfiguration> extendConfigurationAction = null) => Policy.Handle<IgniteClientException>()
        .Or<IOException>()
        .WaitAndRetry(6, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)))
        .Execute(
            () => CacheFactory.GetOrCreateCacheClient<TKey, TData>(igniteClient, cacheName, extendConfigurationAction));

    public void DestroyCache(string cacheName)
    {
        try
        {
            igniteClient.DestroyCache(cacheName);
        }
        catch (IgniteClientException icex)
        {
            if (!icex.Message.Contains("Cache does not exist"))
            {
                throw;
            }
        }
    }
}

using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
using AspNetCore.Ignite.Interfaces;
using Microsoft.Extensions.Configuration;
using Polly;
using System;
using System.IO;

namespace AspNetCore.Ignite
{
    public class IgniteManager : IIgniteManager
    {
        private readonly IIgniteClient _igniteClient;

        public IgniteManager(IConfiguration configuration)
        {
            string aspNetCoreIgniteEndpoint = configuration["ASPNETCORE_IGNITE_ENDPOINT"];
            string aspNetCoreIgniteUserName = configuration["ASPNETCORE_IGNITE_USER_NAME"];
            string aspNetCoreIgnitePassword = configuration["ASPNETCORE_IGNITE_PASSWORD"];
            bool aspNetCoreIgniteUseSsl = "true".Equals(configuration["ASPNETCORE_IGNITE_USE_SSL"],
                StringComparison.InvariantCultureIgnoreCase);
            string aspNetCoreIgniteSslCertificatePath = configuration["ASPNETCORE_IGNITE_SSL_CERTIFICATE_PATH"];
            string aspNetCoreIgniteSslCertificatePassword = configuration["ASPNETCORE_IGNITE_SSL_CERTIFICATE_PASSWORD"];
            _igniteClient = CacheFactory.ConnectAsClient(CacheFactory.GetIgniteClientConfiguration(
                aspNetCoreIgniteEndpoint, aspNetCoreIgniteUserName, aspNetCoreIgnitePassword, aspNetCoreIgniteUseSsl,
                aspNetCoreIgniteSslCertificatePath, aspNetCoreIgniteSslCertificatePassword));
        }

        public ICacheClient<TKey, TData> GetOrCreateCacheClient<TKey, TData>(string cacheName,
            Action<CacheClientConfiguration> extendConfigurationAction = null)
        {
            return Policy
                .Handle<IgniteClientException>().Or<IOException>()
                .WaitAndRetry(5, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)))
                .Execute(() =>
                    CacheFactory.GetOrCreateCacheClient<TKey, TData>(_igniteClient, cacheName,
                        extendConfigurationAction));
        }

        public void DestroyCache(string cacheName)
        {
            try
            {
                _igniteClient.DestroyCache(cacheName);
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
}

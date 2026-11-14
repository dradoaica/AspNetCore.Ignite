using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AspNetCore.Ignite.Options;

/// <summary>Options of the thin client connection made by <see cref="IgniteManager" />.</summary>
public sealed class IgniteClientOptions
{
    /// <summary>Thin client endpoints (host or host:port); <see cref="CacheFactory.DefaultEndpoint" /> when empty.</summary>
    public List<string> Endpoints { get; set; } = [];

    /// <summary>The user name (authentication).</summary>
    public string? UserName { get; set; }

    /// <summary>The password (authentication).</summary>
    public string? Password { get; set; }

    /// <summary>Connects with SSL (the OS chooses the protocol, TLS 1.3 included).</summary>
    public bool UseSsl { get; set; }

    /// <summary>The client certificate (PKCS12), when the server requires one.</summary>
    public string? SslCertificatePath { get; set; }

    /// <summary>The password of the client certificate.</summary>
    public string? SslCertificatePassword { get; set; }

    /// <summary>Skips the server certificate validation: insecure, for testing only.</summary>
    public bool SslSkipServerCertificateValidation { get; set; }

    /// <summary>Checks the revocation of the server certificate.</summary>
    public bool SslCheckCertificateRevocation { get; set; } = true;

    /// <summary>
    ///     Discovers the other server nodes of the cluster and connects to them too (partition awareness); turn it off
    ///     when their addresses are not reachable from the client (e.g., through a load balancer or port forwarding).
    /// </summary>
    public bool EnableClusterDiscovery { get; set; } = true;

    /// <summary>Retries of failed read operations by the thin client itself.</summary>
    public int RetryLimit { get; set; } = 5;

    /// <summary>The socket timeout of the thin client.</summary>
    public TimeSpan SocketTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Connection attempts (exponential backoff: 2, 4, 8, ... seconds) when the first operation connects.</summary>
    public int ConnectRetryCount { get; set; } = 6;

    /// <summary>Retries (exponential backoff) of transient failures of <see cref="IgniteManager" /> operations.</summary>
    public int OperationRetryCount { get; set; } = 3;

    /// <summary>Defaults of the caches created by <see cref="IgniteManager.GetOrCreateCacheClient{TKey,TData}" />.</summary>
    public IgniteCacheOptions Cache { get; set; } = new();

    /// <summary>
    ///     Applies the flat <c>ASPNETCORE_IGNITE_*</c> keys (environment variables); <c>ASPNETCORE_IGNITE_ENDPOINT</c>
    ///     may list several comma-separated endpoints.
    /// </summary>
    public void ApplyConfigurationKeys(IConfiguration configuration)
    {
        if (configuration["ASPNETCORE_IGNITE_ENDPOINT"] is { } endpoints && !string.IsNullOrWhiteSpace(endpoints))
        {
            Endpoints = endpoints.Split(',')
                .Select(endpoint => endpoint.Trim())
                .Where(endpoint => endpoint.Length > 0)
                .ToList();
        }

        UserName = configuration["ASPNETCORE_IGNITE_USER_NAME"] ?? UserName;
        Password = configuration["ASPNETCORE_IGNITE_PASSWORD"] ?? Password;
        UseSsl = GetBool(configuration, "ASPNETCORE_IGNITE_USE_SSL") ?? UseSsl;
        SslCertificatePath = configuration["ASPNETCORE_IGNITE_SSL_CERTIFICATE_PATH"] ?? SslCertificatePath;
        SslCertificatePassword = configuration["ASPNETCORE_IGNITE_SSL_CERTIFICATE_PASSWORD"] ?? SslCertificatePassword;
        SslSkipServerCertificateValidation =
            GetBool(configuration, "ASPNETCORE_IGNITE_SSL_SKIP_SERVER_CERTIFICATE_VALIDATION") ??
            SslSkipServerCertificateValidation;
        SslCheckCertificateRevocation = GetBool(configuration, "ASPNETCORE_IGNITE_SSL_CHECK_CERTIFICATE_REVOCATION") ??
                                        SslCheckCertificateRevocation;
        EnableClusterDiscovery = GetBool(configuration, "ASPNETCORE_IGNITE_ENABLE_CLUSTER_DISCOVERY") ??
                                 EnableClusterDiscovery;
    }

    private static bool? GetBool(IConfiguration configuration, string key) =>
        bool.TryParse(configuration[key], out var value) ? value : null;
}

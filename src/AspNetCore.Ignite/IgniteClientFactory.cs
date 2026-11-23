using Apache.Ignite.Core;
using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Log;
using AspNetCore.Ignite.Options;
using System.Linq;
using System.Security.Authentication;

namespace AspNetCore.Ignite;

/// <summary>
///     Creates thin client configurations and connects clients (thin, or thick in this process) with the defaults of
///     this library; <see cref="CacheFactory" /> creates their caches.
/// </summary>
public static class IgniteClientFactory
{
    /// <summary>The endpoint used when none is configured.</summary>
    public const string DefaultEndpoint = "127.0.0.1";

    /// <summary>A thin client configuration for one endpoint.</summary>
    /// <param name="endpoint">The endpoint (host or host:port); <see cref="DefaultEndpoint" /> when empty.</param>
    /// <param name="userName">The user name (authentication).</param>
    /// <param name="password">The password (authentication).</param>
    /// <param name="useSsl">Connects with SSL.</param>
    /// <param name="certificatePath">The client certificate (PKCS12), when the server requires one.</param>
    /// <param name="certificatePassword">The password of the client certificate.</param>
    /// <param name="skipServerCertificateValidation">Skips the server certificate validation: insecure, for testing only.</param>
    /// <param name="checkCertificateRevocation">Checks the revocation of the server certificate.</param>
    /// <param name="logger">The Ignite logger of the client (none when null).</param>
    public static IgniteClientConfiguration GetIgniteClientConfiguration(
        string? endpoint = DefaultEndpoint,
        string? userName = null,
        string? password = null,
        bool useSsl = false,
        string? certificatePath = null,
        string? certificatePassword = null,
        bool skipServerCertificateValidation = false,
        bool checkCertificateRevocation = true,
        ILogger? logger = null
    ) => GetIgniteClientConfiguration(
        new IgniteClientOptions
        {
            Endpoints = string.IsNullOrWhiteSpace(endpoint) ? [] : [endpoint!,],
            UserName = userName,
            Password = password,
            UseSsl = useSsl,
            SslCertificatePath = certificatePath,
            SslCertificatePassword = certificatePassword,
            SslSkipServerCertificateValidation = skipServerCertificateValidation,
            SslCheckCertificateRevocation = checkCertificateRevocation,
        },
        logger
    );

    /// <summary>A thin client configuration from <paramref name="options" />.</summary>
    /// <param name="options">The options.</param>
    /// <param name="logger">The Ignite logger of the client (none when null).</param>
    public static IgniteClientConfiguration GetIgniteClientConfiguration(
        IgniteClientOptions options,
        ILogger? logger = null
    )
    {
        var endpoints = options.Endpoints.Where(endpoint => !string.IsNullOrWhiteSpace(endpoint)).ToList();
        IgniteClientConfiguration igniteClientConfiguration = new()
        {
            Endpoints = endpoints.Count > 0 ? endpoints : [DefaultEndpoint,],
            RetryPolicy = new ClientRetryReadPolicy(),
            RetryLimit = options.RetryLimit,
            SocketTimeout = options.SocketTimeout,
            EnablePartitionAwareness = true,
            EnableClusterDiscovery = options.EnableClusterDiscovery,
            EnableHeartbeats = true,
            Logger = logger,
        };
        if (!string.IsNullOrWhiteSpace(options.UserName))
        {
            igniteClientConfiguration.UserName = options.UserName;
        }

        if (!string.IsNullOrWhiteSpace(options.Password))
        {
            igniteClientConfiguration.Password = options.Password;
        }

        if (options.UseSsl)
        {
            igniteClientConfiguration.SslStreamFactory = new SslStreamFactory
            {
                CertificatePath = options.SslCertificatePath,
                CertificatePassword = options.SslCertificatePassword,
                CheckCertificateRevocation = options.SslCheckCertificateRevocation,
                SkipServerCertificateValidation = options.SslSkipServerCertificateValidation,
                // None lets the OS choose the best protocol it supports (TLS 1.3 included), as Microsoft recommends
                SslProtocols = SslProtocols.None,
            };
        }

        return igniteClientConfiguration;
    }

    /// <summary>Starts a thick client node (in this process) with <paramref name="igniteConfiguration" />.</summary>
    /// <param name="igniteConfiguration">The node configuration; its <c>ClientMode</c> is set to true.</param>
    public static IIgnite ConnectAsClient(IgniteConfiguration igniteConfiguration)
    {
        igniteConfiguration.ClientMode = true;

        return Ignition.Start(igniteConfiguration);
    }

    /// <summary>Connects a thin client.</summary>
    /// <param name="igniteClientConfiguration">The thin client configuration.</param>
    public static IIgniteClient ConnectAsClient(IgniteClientConfiguration igniteClientConfiguration) =>
        Ignition.StartClient(igniteClientConfiguration);
}

using Apache.Ignite.Core;
using Apache.Ignite.Core.Cache.Query;
using Apache.Ignite.Core.Client;
using AspNetCore.Ignite;
using Serilog;
using System;

namespace AspNetCore.IgniteServer;

/// <summary>Makes the configured password the password of the default <c>ignite</c> user of a node.</summary>
internal sealed class IgniteUserPasswordManager(IgniteServerRunnerOptions options, ILogger logger)
{
    internal const string DefaultUserName = "ignite";
    internal const string DefaultPassword = "ignite";
    private const int DefaultThinClientPort = 10800;
    private const string DdlCacheName = "alterUserSqlDdlCommand";

    /// <summary>
    ///     Nothing to do when <paramref name="password" /> is already accepted; otherwise it replaces the previous
    ///     password (rotation) or the default one (first start). Logs an error when none of them is accepted.
    /// </summary>
    public void Apply(IIgnite node, string password)
    {
        var endpoint = GetThinClientEndpoint(node);
        if (CanAuthenticate(endpoint, password))
        {
            return;
        }

        foreach (var currentPassword in new[] { options.IgniteUserPreviousPassword, DefaultPassword, })
        {
            if (string.IsNullOrWhiteSpace(currentPassword) || !CanAuthenticate(endpoint, currentPassword))
            {
                continue;
            }

            using var igniteClient = Connect(endpoint, currentPassword);
            var ddlCache = CacheFactory.GetOrCreateCacheClient<string, string>(igniteClient, DdlCacheName);
            ddlCache.Query(new SqlFieldsQuery(BuildAlterUserPasswordSql(DefaultUserName, password))).GetAll();
            igniteClient.DestroyCache(DdlCacheName);
            logger.Information(
                "The password of the '{UserName}' user was changed (from the {PasswordKind} password).",
                DefaultUserName,
                ReferenceEquals(currentPassword, DefaultPassword) ? "default" : "previous"
            );
            return;
        }

        logger.Error(
            "IGNITE_USER_PASSWORD was not applied: neither it, IGNITE_USER_PREVIOUS_PASSWORD nor the default password is accepted for the '{UserName}' user.",
            DefaultUserName
        );
    }

    internal static string BuildAlterUserPasswordSql(string userName, string password) =>
        $"ALTER USER \"{userName.Replace("\"", "\"\"", StringComparison.Ordinal)}\" WITH PASSWORD '{password.Replace("'", "''", StringComparison.Ordinal)}';";

    /// <summary>The loopback endpoint of the node's thin client connector (as configured, e.g., by -ConfigFile).</summary>
    private static string GetThinClientEndpoint(IIgnite node)
    {
        var port = node.GetConfiguration().ClientConnectorConfiguration?.Port ?? DefaultThinClientPort;
        return $"127.0.0.1:{port}";
    }

    private bool CanAuthenticate(string endpoint, string password)
    {
        try
        {
            using var igniteClient = Connect(endpoint, password);
            return true;
        }
        catch (IgniteClientException icex) when (icex.StatusCode == ClientStatusCode.AuthenticationFailed)
        {
            return false;
        }
    }

    private IIgniteClient Connect(string endpoint, string password) => CacheFactory.ConnectAsClient(
        CacheFactory.GetIgniteClientConfiguration(
            endpoint,
            DefaultUserName,
            password,
            options.UseClientSsl,
            options.SslClientCertificatePath,
            options.SslClientCertificatePassword,
            // loopback connection to this very node, whose certificate is not issued for 127.0.0.1
            skipServerCertificateValidation: true
        )
    );
}

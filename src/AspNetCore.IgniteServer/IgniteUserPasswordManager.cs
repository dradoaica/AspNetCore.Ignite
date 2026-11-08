using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Apache.Ignite.Core;
using Apache.Ignite.Core.Cache.Query;
using Apache.Ignite.Core.Client;
using AspNetCore.Ignite;
using AspNetCore.Ignite.Options;
using Serilog;

namespace AspNetCore.IgniteServer;

/// <summary>Makes the configured password the password of the default <c>ignite</c> user of a node.</summary>
internal sealed class IgniteUserPasswordManager(IgniteServerRunnerOptions options, ILogger logger)
{
    internal const string DefaultUserName = "ignite";
    internal const string DefaultPassword = "ignite";
    private const int DefaultThinClientPort = 10800;
    private const string DdlCacheName = "alterUserSqlDdlCommand";

    // a temporary cache: no backups, and not in the cache group of the user caches of string values
    private static readonly IgniteCacheOptions DdlCacheOptions = new() { Backups = 0, GroupByValueType = false };

    /// <summary>
    ///     Nothing to do when <paramref name="password" /> is already accepted; otherwise it replaces the previous
    ///     password (rotation) or the default one (first start). Nodes that start together apply it at the same time: a
    ///     password another node applied first counts as applied. Logs an error when none of them is accepted.
    /// </summary>
    public void Apply(IIgnite node, string password, bool newCluster = false)
    {
        var endpoint = GetThinClientEndpoint(node);
        // every failed login is logged by the node as an error, so try the likely password first: the default one on
        // a new cluster, the configured one otherwise
        if (!newCluster && CanAuthenticate(endpoint, password)) return;

        var ddlCacheName = GetDdlCacheName(node.GetCluster().GetLocalNode().ConsistentId);
        foreach (var currentPassword in GetCurrentPasswordCandidates(password, newCluster))
        {
            if (!CanAuthenticate(endpoint, currentPassword)) continue;

            // another node changed it since it was accepted
            if (!TryChangePassword(endpoint, currentPassword, password, ddlCacheName)) break;

            logger.Information(
                "The password of the '{UserName:l}' user was changed (from the {PasswordKind:l} password).",
                DefaultUserName,
                currentPassword == DefaultPassword ? "default" : "previous"
            );
            return;
        }

        // applied by another node meanwhile
        if (CanAuthenticate(endpoint, password)) return;

        logger.Error(
            "IGNITE_USER_PASSWORD was not applied: neither it, IGNITE_USER_PREVIOUS_PASSWORD nor the default password is accepted for the '{UserName:l}' user.",
            DefaultUserName
        );
    }

    /// <summary>The passwords that may be the current one, most likely first.</summary>
    internal IEnumerable<string> GetCurrentPasswordCandidates(string password, bool newCluster)
    {
        string?[] candidates = newCluster
            ? [DefaultPassword, options.IgniteUserPreviousPassword]
            : [options.IgniteUserPreviousPassword, DefaultPassword];
        return candidates.Where(candidate => !string.IsNullOrWhiteSpace(candidate) && candidate != password)
            .Select(candidate => candidate!)
            .Distinct();
    }

    /// <summary>The temporary cache of this node (other nodes may change the password at the same time).</summary>
    internal static string GetDdlCacheName(object consistentId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(consistentId.ToString() ?? string.Empty));
        return $"{DdlCacheName}-{Convert.ToHexStringLower(hash.AsSpan(0, 8))}";
    }

    internal static string BuildAlterUserPasswordSql(string userName, string password)
    {
        return
            $"ALTER USER \"{userName.Replace("\"", "\"\"", StringComparison.Ordinal)}\" WITH PASSWORD '{password.Replace("'", "''", StringComparison.Ordinal)}';";
    }

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

    /// <summary>False when <paramref name="currentPassword" /> is no longer accepted (another node changed it).</summary>
    private bool TryChangePassword(string endpoint, string currentPassword, string password, string ddlCacheName)
    {
        IIgniteClient igniteClient;
        try
        {
            igniteClient = Connect(endpoint, currentPassword);
        }
        catch (IgniteClientException icex) when (icex.StatusCode == ClientStatusCode.AuthenticationFailed)
        {
            return false;
        }

        // once connected, the session stays authenticated whatever another node changes
        using (igniteClient)
        {
            var ddlCache =
                CacheFactory.GetOrCreateCacheClient<string, string>(igniteClient, ddlCacheName, null, DdlCacheOptions);
            ddlCache.Query(new SqlFieldsQuery(BuildAlterUserPasswordSql(DefaultUserName, password))).GetAll();
            igniteClient.DestroyCache(ddlCacheName);
        }

        return true;
    }

    private IIgniteClient Connect(string endpoint, string password)
    {
        return CacheFactory.ConnectAsClient(
            CacheFactory.GetIgniteClientConfiguration(
                endpoint,
                DefaultUserName,
                password,
                options.UseClientSsl,
                options.SslClientCertificatePath,
                options.SslClientCertificatePassword,
                // loopback connection to this very node, whose certificate is not issued for 127.0.0.1
                true
            )
        );
    }
}
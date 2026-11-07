using Apache.Ignite.Core;
using Apache.Ignite.Core.Cache.Query;
using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Cluster;
using Apache.Ignite.Core.Communication.Tcp;
using Apache.Ignite.Core.Configuration;
using Apache.Ignite.Core.Deployment;
using Apache.Ignite.Core.Discovery.Tcp;
using Apache.Ignite.Core.Discovery.Tcp.Static;
using Apache.Ignite.Core.Events;
using Apache.Ignite.Core.Ssl;
using AspNetCore.Ignite;
using AspNetCore.IgniteServer.Listeners;
using AspNetCore.IgniteServer.Utils;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCore.IgniteServer;

internal sealed class IgniteServerRunner : IDisposable
{
    private const string DefaultIgniteUserName = "ignite";
    private const string DefaultIgniteUserPassword = "ignite";
    private const int DefaultThinClientPort = 10800;
    private static readonly TimeSpan ActivationPollInterval = TimeSpan.FromSeconds(5);

    private readonly IgniteConfiguration igniteConfiguration;
    private readonly ILogger logger;
    private readonly IgniteServerRunnerOptions options;
    private volatile bool disposed;
    private IIgnite? ignite;
    private volatile bool stopRequested;

    public IgniteServerRunner(IgniteServerRunnerOptions options, ILogger? logger = null)
    {
        this.options = options;
        this.logger = logger ?? Serilog.Core.Logger.None;
        igniteConfiguration = string.IsNullOrWhiteSpace(options.ConfigurationFile) ? GetDefaultConfiguration()
            : LoadConfiguration(options.ConfigurationFile);
        if (!string.IsNullOrWhiteSpace(options.SpringConfigPath))
        {
            igniteConfiguration.SpringConfigUrl = options.SpringConfigPath;
        }

        // the JVM must not handle SIGTERM/SIGINT itself (its shutdown hook would stop the node behind the host's back):
        // the .NET host stops the node gracefully
        igniteConfiguration.JvmOptions = [..igniteConfiguration.JvmOptions ?? [], ..ReduceSignalUsageJvmOptions(igniteConfiguration.JvmOptions)];
        igniteConfiguration.MetricsExpireTime = options.MetricsExpireTime;
        igniteConfiguration.MetricsLogFrequency = options.MetricsLogFrequency;
        igniteConfiguration.MetricsUpdateFrequency = options.MetricsUpdateFrequency;
        if (options.AuthenticationEnabled)
        {
            igniteConfiguration.AuthenticationEnabled = true;
            SetPersistence(true);
        }

        if (options.UseSsl)
        {
            igniteConfiguration.SslContextFactory = new SslContextFactory
            {
                KeyStoreFilePath = options.SslKeyStoreFilePath,
                KeyStorePassword = options.SslKeyStorePassword,
                TrustStoreFilePath = options.SslTrustStoreFilePath,
                TrustStorePassword = options.SslTrustStorePassword,
                Protocol = options.SslProtocol,
            };
        }

        igniteConfiguration.Logger = new SerilogIgniteLogger(this.logger);
    }

    internal IgniteConfiguration Configuration => igniteConfiguration;

    private static IEnumerable<string> ReduceSignalUsageJvmOptions(ICollection<string>? jvmOptions) =>
        jvmOptions?.Contains("-Xrs") == true ? [] : ["-Xrs",];

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        stopRequested = true;
        StopIgnite(true);
        disposed = true;
    }

    private static IgniteConfiguration LoadConfiguration(string filename)
    {
        var configStr = File.ReadAllText(filename);
        var igniteConfiguration = IgniteConfiguration.FromXml(configStr);
        return igniteConfiguration;
    }

    private static IgniteConfiguration GetDefaultConfiguration()
    {
        IgniteConfiguration cfg = new()
        {
            AutoGenerateIgniteInstanceName = true,
            JvmOptions =
            [
                "-Djava.net.preferIPv4Stack=true", "-DIGNITE_QUIET=false", "-DIGNITE_WAL_MMAP=false",
                "-XX:MaxMetaspaceSize=256m", "-XX:ReservedCodeCacheSize=240m", "-XX:+AlwaysPreTouch",
                "-XX:+UseG1GC", "-XX:+DisableExplicitGC",
                "--add-opens=java.base/jdk.internal.access=ALL-UNNAMED",
                "--add-opens=java.base/jdk.internal.misc=ALL-UNNAMED", "--add-opens=java.base/sun.nio.ch=ALL-UNNAMED",
                "--add-opens=java.base/sun.util.calendar=ALL-UNNAMED",
                "--add-opens=java.management/com.sun.jmx.mbeanserver=ALL-UNNAMED",
                "--add-opens=jdk.internal.jvmstat/sun.jvmstat.monitor=ALL-UNNAMED",
                "--add-opens=java.base/sun.reflect.generics.reflectiveObjects=ALL-UNNAMED",
                "--add-opens=jdk.management/com.sun.management.internal=ALL-UNNAMED",
                "--add-opens=java.base/java.io=ALL-UNNAMED", "--add-opens=java.base/java.nio=ALL-UNNAMED",
                "--add-opens=java.base/java.net=ALL-UNNAMED", "--add-opens=java.base/java.util=ALL-UNNAMED",
                "--add-opens=java.base/java.util.concurrent=ALL-UNNAMED",
                "--add-opens=java.base/java.util.concurrent.locks=ALL-UNNAMED",
                "--add-opens=java.base/java.util.concurrent.atomic=ALL-UNNAMED",
                "--add-opens=java.base/java.lang=ALL-UNNAMED", "--add-opens=java.base/java.lang.invoke=ALL-UNNAMED",
                "--add-opens=java.base/java.math=ALL-UNNAMED", "--add-opens=java.sql/java.sql=ALL-UNNAMED",
                "--add-opens=java.base/java.lang.reflect=ALL-UNNAMED", "--add-opens=java.base/java.time=ALL-UNNAMED",
                "--add-opens=java.base/java.text=ALL-UNNAMED", "--add-opens=java.management/sun.management=ALL-UNNAMED",
                "--add-opens=java.desktop/java.awt.font=ALL-UNNAMED",
            ],
            PeerAssemblyLoadingMode = PeerAssemblyLoadingMode.CurrentAppDomain,
            DataStorageConfiguration = new DataStorageConfiguration
            {
                WalSegmentSize = 256 * 1024 * 1024,
            },
            WorkDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "work"),
            CommunicationSpi = new TcpCommunicationSpi
            {
                MessageQueueLimit = 2048,
                SlowClientQueueLimit = 2048,
                SocketWriteTimeout = 5000,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            },
            FailureDetectionTimeout = TimeSpan.FromSeconds(30),
            ClientFailureDetectionTimeout = TimeSpan.FromSeconds(60),
            NetworkTimeout = TimeSpan.FromSeconds(10),
            IncludedEventTypes =
            [
                EventType.NodeJoined, EventType.NodeLeft, EventType.NodeFailed, EventType.CacheRebalancePartDataLost,
            ],
        };

        return cfg;
    }

    public void SetClusterEndpoints(ICollection<string> values)
    {
        if (ignite != null)
        {
            throw new InvalidOperationException("Cannot configure running instances.");
        }

        switch (igniteConfiguration.DiscoverySpi)
        {
            case TcpDiscoverySpi tcpDiscoverySpi:
                tcpDiscoverySpi.IpFinder = new TcpDiscoveryStaticIpFinder
                {
                    Endpoints = values,
                };
                break;
            case null:
                igniteConfiguration.DiscoverySpi = new TcpDiscoverySpi
                {
                    IpFinder = new TcpDiscoveryStaticIpFinder
                    {
                        Endpoints = values,
                    },
                };
                break;
        }
    }

    public void SetServerPort(int value)
    {
        if (ignite != null)
        {
            throw new InvalidOperationException("Cannot configure running instances.");
        }

        switch (igniteConfiguration.DiscoverySpi)
        {
            case TcpDiscoverySpi tcpDiscoverySpi:
                tcpDiscoverySpi.LocalPort = value;
                break;
            case null:
                igniteConfiguration.DiscoverySpi = new TcpDiscoverySpi
                {
                    LocalPort = value,
                };
                break;
        }
    }

    public void SetOnHeapMemoryLimit(int value)
    {
        if (ignite != null)
        {
            throw new InvalidOperationException("Cannot configure running instances.");
        }

        igniteConfiguration.JvmInitialMemoryMb = value / 2;
        igniteConfiguration.JvmMaxMemoryMb = value;
    }

    public void SetOffHeapMemoryLimit(int value)
    {
        if (ignite != null)
        {
            throw new InvalidOperationException("Cannot configure running instances.");
        }

        igniteConfiguration.DataStorageConfiguration ??= new DataStorageConfiguration
        {
            WalSegmentSize = 256 * 1024 * 1024,
        };
        if (igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration != null)
        {
            igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration.MaxSize =
                (long)value * 1024 * 1024;
        }
        else
        {
            igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration = new DataRegionConfiguration
            {
                Name = "default",
                MaxSize = (long)value * 1024 * 1024,
            };
        }

        igniteConfiguration.DataStorageConfiguration.MetricsEnabled = options.EnableOffHeapMetrics;
        igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration.MetricsEnabled =
            options.EnableOffHeapMetrics;
        igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration.PageEvictionMode =
            DataPageEvictionMode.Random2Lru;
    }

    public void SetPersistence(bool value)
    {
        if (ignite != null)
        {
            throw new InvalidOperationException("Cannot configure running instances.");
        }

        igniteConfiguration.DataStorageConfiguration ??= new DataStorageConfiguration
        {
            WalSegmentSize = 256 * 1024 * 1024,
        };
        if (igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration != null)
        {
            igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration.PersistenceEnabled = value;
        }
        else
        {
            igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration = new DataRegionConfiguration
            {
                Name = "default",
                PersistenceEnabled = value,
            };
        }
    }

    public void SetConsistentId(string cid)
    {
        if (ignite != null)
        {
            throw new InvalidOperationException("Cannot configure running instances.");
        }

        igniteConfiguration.ConsistentId = cid;
    }

    public async Task Run()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        TaskCompletionSource<string?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (stopRequested)
        {
            return;
        }

        logger.Information("Starting Ignite server runner...");
        var current = Ignition.Start(igniteConfiguration);
        ignite = current;
        current.Stopped += (s, _) => tcs.TrySetResult(s?.ToString());
        if (stopRequested)
        {
            // Stop() ran while starting, before this node could be stopped
            StopIgnite(false);
        }

        if (!ReferenceEquals(Volatile.Read(ref ignite), current))
        {
            // stopped before the handler was attached
            tcs.TrySetResult(current.Name);
        }

        var cluster = current.GetCluster();
        var persistenceEnabled = igniteConfiguration.DataStorageConfiguration?.DefaultDataRegionConfiguration
            ?.PersistenceEnabled == true;
        if (persistenceEnabled)
        {
            await WaitForServerNodes(cluster, tcs.Task).ConfigureAwait(false);
        }

        if (tcs.Task.IsCompleted)
        {
            logger.Information("{IgniteInstance} stopped before activation.", tcs.Task.Result);
            return;
        }

        if (!cluster.IsActive())
        {
            cluster.SetActive(true);
        }

        cluster.SetBaselineAutoAdjustEnabledFlag(true);
        cluster.SetBaselineAutoAdjustTimeout((long)options.BaselineAutoAdjustTimeout.TotalMilliseconds);
        if (persistenceEnabled && !string.IsNullOrWhiteSpace(options.IgniteUserPassword))
        {
            ApplyIgniteUserPassword(GetThinClientEndpoint(current), options.IgniteUserPassword);
        }

        CacheRebalancingEventListener cacheRebalancingEventListener = new(
            current,
            logger,
            options.AutoResetLostPartitions
        );
        current.GetEvents().LocalListen(cacheRebalancingEventListener, EventType.CacheRebalancePartDataLost);
        DiscoveryEventListener discoveryEventListener = new(logger);
        current.GetEvents()
            .LocalListen(discoveryEventListener, EventType.NodeJoined, EventType.NodeLeft, EventType.NodeFailed);

        var discoverySpiLocalPort = (current.GetConfiguration().DiscoverySpi as TcpDiscoverySpi)?.LocalPort;
        logger.Information(
            "Ignite server runner is running (DiscoverySpi LocalPort={DiscoverySpiLocalPort}), press CTRL+C to terminate.",
            discoverySpiLocalPort
        );
        var stoppedInstance = await tcs.Task.ConfigureAwait(false);
        logger.Information("'{IgniteInstance}' stopped.", stoppedInstance);
    }

    /// <summary>
    ///     A persistent cluster records its baseline topology on activation, so wait for the configured number of server
    ///     nodes before activating it (unless it is already active, e.g., when resuming).
    /// </summary>
    private async Task WaitForServerNodes(ICluster cluster, Task stopped)
    {
        while (!stopped.IsCompleted && !cluster.IsActive())
        {
            var serverNodes = cluster.ForServers().GetNodes().Count;
            if (serverNodes >= options.ActivationMinServerNodes)
            {
                return;
            }

            logger.Information(
                "Waiting for {ActivationMinServerNodes} server nodes before activating the cluster ({ServerNodes} joined)...",
                options.ActivationMinServerNodes,
                serverNodes
            );
            await Task.WhenAny(stopped, Task.Delay(ActivationPollInterval)).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Makes <paramref name="password" /> the password of the default user: nothing to do when it is already accepted,
    ///     otherwise it replaces the previous password (rotation) or the default one (first start).
    /// </summary>
    private void ApplyIgniteUserPassword(string endpoint, string password)
    {
        if (CanAuthenticate(endpoint, password))
        {
            return;
        }

        foreach (var currentPassword in new[] { options.IgniteUserPreviousPassword, DefaultIgniteUserPassword, })
        {
            if (string.IsNullOrWhiteSpace(currentPassword) || !CanAuthenticate(endpoint, currentPassword))
            {
                continue;
            }

            using var igniteClient = ConnectAsDefaultUser(endpoint, currentPassword);
            var alterUserSqlDdlCommand =
                CacheFactory.GetOrCreateCacheClient<string, string>(igniteClient, "alterUserSqlDdlCommand");
            alterUserSqlDdlCommand.Query(new SqlFieldsQuery(BuildAlterUserPasswordSql(DefaultIgniteUserName, password)))
                .GetAll();
            igniteClient.DestroyCache("alterUserSqlDdlCommand");
            logger.Information(
                "The password of the '{UserName}' user was changed (from the {PasswordKind} password).",
                DefaultIgniteUserName,
                ReferenceEquals(currentPassword, DefaultIgniteUserPassword) ? "default" : "previous"
            );
            return;
        }

        logger.Error(
            "IGNITE_USER_PASSWORD was not applied: neither it, IGNITE_USER_PREVIOUS_PASSWORD nor the default password is accepted for the '{UserName}' user.",
            DefaultIgniteUserName
        );
    }

    private bool CanAuthenticate(string endpoint, string password)
    {
        try
        {
            using var igniteClient = ConnectAsDefaultUser(endpoint, password);
            return true;
        }
        catch (IgniteClientException icex) when (icex.StatusCode == ClientStatusCode.AuthenticationFailed)
        {
            return false;
        }
    }

    private IIgniteClient ConnectAsDefaultUser(string endpoint, string password) =>
        CacheFactory.ConnectAsClient(
            CacheFactory.GetIgniteClientConfiguration(
                endpoint,
                DefaultIgniteUserName,
                password,
                options.UseClientSsl,
                options.SslClientCertificatePath,
                options.SslClientCertificatePassword,
                // loopback connection to this very node, whose certificate is not issued for 127.0.0.1
                skipServerCertificateValidation: true
            )
        );

    /// <summary>The loopback endpoint of this node's thin client connector (as configured, e.g., by -ConfigFile).</summary>
    private static string GetThinClientEndpoint(IIgnite current)
    {
        var port = current.GetConfiguration().ClientConnectorConfiguration?.Port ?? DefaultThinClientPort;
        return $"127.0.0.1:{port}";
    }

    internal static string BuildAlterUserPasswordSql(string userName, string password) =>
        $"ALTER USER \"{userName.Replace("\"", "\"\"", StringComparison.Ordinal)}\" WITH PASSWORD '{password.Replace("'", "''", StringComparison.Ordinal)}';";

    /// <summary>Stops the node gracefully so that <see cref="Run" /> can start it again (e.g., with renewed certificates).</summary>
    public void Restart()
    {
        logger.Information("Restarting Ignite server runner...");
        StopIgnite(false);
    }

    /// <summary>Stops the node gracefully; afterward, <see cref="Run" /> does not start it again.</summary>
    public void Stop()
    {
        stopRequested = true;
        logger.Information("Stopping Ignite server runner...");
        if (StopIgnite(false))
        {
            logger.Information("Ignite server runner stopped.");
        }
    }

    private bool StopIgnite(bool cancel)
    {
        var current = Interlocked.Exchange(ref ignite, null);
        if (current == null)
        {
            return false;
        }

        Ignition.Stop(current.Name, cancel);
        return true;
    }
}

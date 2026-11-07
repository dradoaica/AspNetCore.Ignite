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
using Apache.Ignite.NLog;
using AspNetCore.Ignite;
using AspNetCore.IgniteServer.Listeners;
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
    private static readonly TimeSpan ActivationPollInterval = TimeSpan.FromSeconds(5);

    private readonly IgniteConfiguration igniteConfiguration;
    private readonly IgniteServerRunnerOptions options;
    private bool disposed;
    private IIgnite? ignite;

    public IgniteServerRunner(IgniteServerRunnerOptions options)
    {
        this.options = options;
        igniteConfiguration = string.IsNullOrWhiteSpace(options.ConfigurationFile) ? GetDefaultConfiguration()
            : LoadConfiguration(options.ConfigurationFile);
        if (!string.IsNullOrWhiteSpace(options.SpringConfigPath))
        {
            igniteConfiguration.SpringConfigUrl = options.SpringConfigPath;
        }

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
                Protocol = "TLSv1.2",
            };
        }

        igniteConfiguration.Logger = new IgniteNLogLogger();
    }

    internal IgniteConfiguration Configuration => igniteConfiguration;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

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
                "-XX:+UseG1GC", "-XX:+ScavengeBeforeFullGC", "-XX:+DisableExplicitGC",
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
        Program.Logger?.Information("Starting Ignite server runner...");
        var current = Ignition.Start(igniteConfiguration);
        ignite = current;
        current.Stopped += (s, _) => tcs.TrySetResult(s?.ToString());
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
            Program.Logger?.Information("{IgniteInstance} stopped before activation.", tcs.Task.Result);
            return;
        }

        if (!cluster.IsActive())
        {
            cluster.SetActive(true);
        }

        cluster.SetBaselineAutoAdjustEnabledFlag(true);
        cluster.SetBaselineAutoAdjustTimeout(30000);
        if (persistenceEnabled && !string.IsNullOrWhiteSpace(options.IgniteUserPassword))
        {
            ChangeDefaultUserPassword(options.IgniteUserPassword);
        }

        CacheRebalancingEventListener cacheRebalancingEventListener = new(
            current,
            Program.Logger,
            options.AutoResetLostPartitions
        );
        current.GetEvents().LocalListen(cacheRebalancingEventListener, EventType.CacheRebalancePartDataLost);
        DiscoveryEventListener discoveryEventListener = new(Program.Logger);
        current.GetEvents()
            .LocalListen(discoveryEventListener, EventType.NodeJoined, EventType.NodeLeft, EventType.NodeFailed);

        var discoverySpiLocalPort = (current.GetConfiguration().DiscoverySpi as TcpDiscoverySpi)?.LocalPort;
        Program.Logger?.Information(
            "Ignite server runner is running (DiscoverySpi LocalPort={DiscoverySpiLocalPort}), press CTRL+C to terminate.",
            discoverySpiLocalPort
        );
        var stoppedInstance = await tcs.Task.ConfigureAwait(false);
        Program.Logger?.Information("'{IgniteInstance}' stopped.", stoppedInstance);
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

            Program.Logger?.Information(
                "Waiting for {ActivationMinServerNodes} server nodes before activating the cluster ({ServerNodes} joined)...",
                options.ActivationMinServerNodes,
                serverNodes
            );
            await Task.WhenAny(stopped, Task.Delay(ActivationPollInterval)).ConfigureAwait(false);
        }
    }

    private void ChangeDefaultUserPassword(string newPassword)
    {
        try
        {
            using var igniteClient = CacheFactory.ConnectAsClient(
                CacheFactory.GetIgniteClientConfiguration(
                    userName: DefaultIgniteUserName,
                    password: DefaultIgniteUserPassword,
                    useSsl: options.UseClientSsl,
                    certificatePath: options.SslClientCertificatePath,
                    certificatePassword: options.SslClientCertificatePassword,
                    // loopback connection to this very node, whose certificate is not issued for 127.0.0.1
                    skipServerCertificateValidation: true
                )
            );
            var alterUserSqlDdlCommand =
                CacheFactory.GetOrCreateCacheClient<string, string>(igniteClient, "alterUserSqlDdlCommand");
            alterUserSqlDdlCommand.Query(new SqlFieldsQuery(BuildAlterUserPasswordSql(DefaultIgniteUserName, newPassword)))
                .GetAll();
            igniteClient.DestroyCache("alterUserSqlDdlCommand");
        }
        catch (IgniteClientException icex) when (icex.StatusCode == ClientStatusCode.AuthenticationFailed)
        {
            // the default password was already changed on a previous start
        }
    }

    internal static string BuildAlterUserPasswordSql(string userName, string password) =>
        $"ALTER USER \"{userName.Replace("\"", "\"\"", StringComparison.Ordinal)}\" WITH PASSWORD '{password.Replace("'", "''", StringComparison.Ordinal)}';";

    /// <summary>Stops the node immediately (cancelling ongoing jobs); <see cref="Run" /> may be called again afterward.</summary>
    public void Terminate()
    {
        Program.Logger?.Information("Terminating Ignite server runner...");
        if (StopIgnite(true))
        {
            Program.Logger?.Information("Ignite server runner terminated.");
        }
    }

    /// <summary>Stops the node gracefully; <see cref="Run" /> may be called again afterward.</summary>
    public void Stop()
    {
        Program.Logger?.Information("Stopping Ignite server runner...");
        if (StopIgnite(false))
        {
            Program.Logger?.Information("Ignite server runner stopped.");
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

using Apache.Ignite.Core;
using Apache.Ignite.Core.Cache.Query;
using Apache.Ignite.Core.Client;
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
using System.Threading.Tasks;

namespace AspNetCore.IgniteServer;

internal sealed class IgniteServerRunner : IDisposable
{
    private readonly bool enableOffHeapMetrics;
    private readonly IgniteConfiguration igniteConfiguration;
    private readonly string? igniteUserPassword;
    private readonly string? sslClientCertificatePassword;
    private readonly string? sslClientCertificatePath;
    private readonly bool useClientSsl;
    private bool disposed;
    private IIgnite? ignite;

    public IgniteServerRunner(
        TimeSpan metricsExpireTime,
        TimeSpan metricsLogFrequency,
        TimeSpan metricsUpdateFrequency,
        bool enableOffHeapMetrics,
        bool authenticationEnabled,
        string? igniteUserPassword = null,
        string? configurationFile = null,
        bool useSsl = false,
        string? sslKeyStoreFilePath = null,
        string? sslKeyStorePassword = null,
        string? sslTrustStoreFilePath = null,
        string? sslTrustStorePassword = null,
        bool useClientSsl = false,
        string? sslClientCertificatePath = null,
        string? sslClientCertificatePassword = null
    )
    {
        this.enableOffHeapMetrics = enableOffHeapMetrics;
        this.useClientSsl = useClientSsl;
        this.sslClientCertificatePath = sslClientCertificatePath;
        this.sslClientCertificatePassword = sslClientCertificatePassword;
        this.igniteUserPassword = igniteUserPassword;
        igniteConfiguration = string.IsNullOrWhiteSpace(configurationFile) ? GetDefaultConfiguration()
            : LoadConfiguration(configurationFile);
        igniteConfiguration.SpringConfigUrl = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "config",
            this.useClientSsl ? "spring-config-client-with-ssl.xml" : "spring-config.xml"
        );
        igniteConfiguration.MetricsExpireTime = metricsExpireTime;
        igniteConfiguration.MetricsLogFrequency = metricsLogFrequency;
        igniteConfiguration.MetricsUpdateFrequency = metricsUpdateFrequency;
        if (authenticationEnabled)
        {
            igniteConfiguration.AuthenticationEnabled = true;
            SetPersistence(true);
        }

        if (useSsl)
        {
            igniteConfiguration.SslContextFactory = new SslContextFactory
            {
                KeyStoreFilePath = sslKeyStoreFilePath,
                KeyStorePassword = sslKeyStorePassword,
                TrustStoreFilePath = sslTrustStoreFilePath,
                TrustStorePassword = sslTrustStorePassword,
                Protocol = "TLSv1.2",
            };
        }

        igniteConfiguration.Logger = new IgniteNLogLogger();
    }

    public void Dispose() => Dispose(true);

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
                "-server", "-XX:MaxMetaspaceSize=256m", "-XX:ReservedCodeCacheSize=240m", "-XX:+AlwaysPreTouch",
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

    private void Dispose(bool disposing)
    {
        if (!disposed)
        {
            if (disposing)
            {
                if (ignite != null)
                {
                    ignite.Dispose();
                    ignite = null;
                }
            }

            disposed = true;
        }
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

        igniteConfiguration.DataStorageConfiguration.MetricsEnabled = enableOffHeapMetrics;
        igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration.MetricsEnabled =
            enableOffHeapMetrics;
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
        TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Program.Logger?.Information("Starting Ignite server runner...");
        ignite = Ignition.Start(igniteConfiguration);
        ignite!.GetCluster().SetActive(true);
        ignite.GetCluster().SetBaselineAutoAdjustEnabledFlag(true);
        ignite.GetCluster().SetBaselineAutoAdjustTimeout(30000);
        var persistenceEnabled = igniteConfiguration.DataStorageConfiguration?.DefaultDataRegionConfiguration
            ?.PersistenceEnabled;
        if (persistenceEnabled.HasValue && persistenceEnabled.Value)
        {
            if (!string.IsNullOrWhiteSpace(igniteUserPassword))
            {
                try
                {
                    using var igniteClient = CacheFactory.ConnectAsClient(
                        CacheFactory.GetIgniteClientConfiguration(
                            userName: "ignite",
                            password: "ignite",
                            useSsl: useClientSsl,
                            certificatePath: sslClientCertificatePath,
                            certificatePassword: sslClientCertificatePassword
                        )
                    );
                    var alterUserSqlDdlCommand =
                        CacheFactory.GetOrCreateCacheClient<string, string>(igniteClient, "alterUserSqlDdlCommand");
                    alterUserSqlDdlCommand.Query(
                        new SqlFieldsQuery($"ALTER USER \"ignite\" WITH PASSWORD '{igniteUserPassword}';")
                    );
                    igniteClient.DestroyCache("alterUserSqlDdlCommand");
                }
                catch (IgniteClientException icex)
                {
                    if (icex.Message != "The user name or password is incorrect [userName=ignite]")
                    {
                        throw;
                    }
                }
            }
        }

        CacheRebalancingEventListener cacheRebalancingEventListener = new(ignite, Program.Logger);
        ignite.GetEvents().LocalListen(cacheRebalancingEventListener, EventType.CacheRebalancePartDataLost);
        DiscoveryEventListener discoveryEventListener = new(Program.Logger);
        ignite.GetEvents()
            .LocalListen(discoveryEventListener, EventType.NodeJoined, EventType.NodeLeft, EventType.NodeFailed);

        ignite.Stopped += (s, _) => tcs.SetResult(s?.ToString());
        var discoverySpiLocalPort = ((TcpDiscoverySpi)ignite.GetConfiguration().DiscoverySpi).LocalPort;
        Program.Logger?.Information(
            $"Ignite server runner is running (DiscoverySpi LocalPort={discoverySpiLocalPort}), press CTRL+C to terminate."
        );
        await tcs.Task.ConfigureAwait(false);
        Program.Logger?.Information($"'{tcs.Task.Result}' stopped.");
    }

    public void Terminate()
    {
        if (ignite == null)
        {
            return;
        }

        Program.Logger?.Information("Terminating Ignite server runner...");
        Ignition.Stop(ignite.Name, true);
        Dispose();
        Program.Logger?.Information("Ignite server runner terminated.");
    }

    public void Stop()
    {
        if (ignite == null)
        {
            return;
        }

        Program.Logger?.Information("Stopping Ignite server runner...");
        Ignition.Stop(ignite.Name, false);
        Dispose();
        Program.Logger?.Information("Ignite server runner stopped.");
    }
}

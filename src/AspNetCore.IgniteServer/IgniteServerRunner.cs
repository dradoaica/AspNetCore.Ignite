namespace AspNetCore.IgniteServer;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
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
using Ignite;
using Listeners;
using Serilog;
using Serilog.Core;

internal sealed class IgniteServerRunner : IDisposable
{
    private static readonly Logger Logger;
    private readonly bool enableOffHeapMetrics;
    private readonly IgniteConfiguration igniteConfiguration;
    private readonly string? igniteUserPassword;
    private readonly string? sslClientCertificatePassword;
    private readonly string? sslClientCertificatePath;
    private readonly bool useClientSsl;
    private bool disposed;

    static IgniteServerRunner()
    {
        var loggerConfiguration =
            new LoggerConfiguration().ReadFrom.Configuration(Program.Configuration);
        Logger = loggerConfiguration.CreateLogger();
        Log.Logger = Logger;
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    public IgniteServerRunner(TimeSpan metricsExpireTime, TimeSpan metricsLogFrequency,
        TimeSpan metricsUpdateFrequency,
        bool enableOffHeapMetrics, bool authenticationEnabled, string? igniteUserPassword = null,
        string? configurationFile = null, bool useSsl = false,
        string? sslKeyStoreFilePath = null, string? sslKeyStorePassword = null,
        string? sslTrustStoreFilePath = null,
        string? sslTrustStorePassword = null,
        bool useClientSsl = false, string? sslClientCertificatePath = null,
        string? sslClientCertificatePassword = null)
    {
        this.enableOffHeapMetrics = enableOffHeapMetrics;
        this.useClientSsl = useClientSsl;
        this.sslClientCertificatePath = sslClientCertificatePath;
        this.sslClientCertificatePassword = sslClientCertificatePassword;
        this.igniteUserPassword = igniteUserPassword;
        this.igniteConfiguration = string.IsNullOrWhiteSpace(configurationFile)
            ? GetDefaultConfiguration()
            : LoadConfiguration(configurationFile);
        this.igniteConfiguration.SpringConfigUrl = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config",
            this.useClientSsl ? "spring-config-client-with-ssl.xml" : "spring-config.xml");
        this.igniteConfiguration.MetricsExpireTime = metricsExpireTime;
        this.igniteConfiguration.MetricsLogFrequency = metricsLogFrequency;
        this.igniteConfiguration.MetricsUpdateFrequency = metricsUpdateFrequency;
        if (authenticationEnabled)
        {
            this.igniteConfiguration.AuthenticationEnabled = true;
            this.SetPersistence(true);
        }

        if (useSsl)
        {
            this.igniteConfiguration.SslContextFactory = new SslContextFactory
            {
                KeyStoreFilePath = sslKeyStoreFilePath,
                KeyStorePassword = sslKeyStorePassword,
                TrustStoreFilePath = sslTrustStoreFilePath,
                TrustStorePassword = sslTrustStorePassword,
                Protocol = "TLSv1.2"
            };
        }

        this.igniteConfiguration.Logger = new IgniteNLogLogger();
    }

    public IIgnite? Ignite { get; private set; }

    public void Dispose() => this.Dispose(true);

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
                new[]
                {
                    "-XX:+AlwaysPreTouch", "-XX:+UseG1GC", "-XX:+ScavengeBeforeFullGC", "-XX:+DisableExplicitGC",
                    "-Djava.net.preferIPv4Stack=true", "-DIGNITE_QUIET=false", "-DIGNITE_WAL_MMAP=false",
                    "-DIGNITE_WAIT_FOR_BACKUPS_ON_SHUTDOWN=true",
                    "--add-opens=java.base/jdk.internal.access=ALL-UNNAMED",
                    "--add-opens=java.base/jdk.internal.misc=ALL-UNNAMED",
                    "--add-opens=java.base/sun.nio.ch=ALL-UNNAMED",
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
                    "--add-opens=java.base/java.lang=ALL-UNNAMED",
                    "--add-opens=java.base/java.lang.invoke=ALL-UNNAMED",
                    "--add-opens=java.base/java.math=ALL-UNNAMED", "--add-opens=java.sql/java.sql=ALL-UNNAMED",
                    "--add-opens=java.base/java.lang.reflect=ALL-UNNAMED",
                    "--add-opens=java.base/java.time=ALL-UNNAMED", "--add-opens=java.base/java.text=ALL-UNNAMED",
                    "--add-opens=java.management/sun.management=ALL-UNNAMED"
                },
            PeerAssemblyLoadingMode = PeerAssemblyLoadingMode.CurrentAppDomain,
            DataStorageConfiguration = new DataStorageConfiguration {WalSegmentSize = 256 * 1024 * 1024},
            WorkDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "work"),
            CommunicationSpi =
                new TcpCommunicationSpi
                {
                    MessageQueueLimit = 2048,
                    SlowClientQueueLimit = 2048,
                    SocketWriteTimeout = 5000,
                    ConnectTimeout = TimeSpan.FromSeconds(10)
                },
            FailureDetectionTimeout = TimeSpan.FromSeconds(30),
            ClientFailureDetectionTimeout = TimeSpan.FromSeconds(60),
            NetworkTimeout = TimeSpan.FromSeconds(10),
            IncludedEventTypes = new[]
            {
                EventType.NodeJoined, EventType.NodeLeft, EventType.NodeFailed, EventType.CacheRebalancePartDataLost
            }
        };
        return cfg;
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Logger.Error(ex, "UnhandledException");
        }
        else
        {
            var msg = "";
            if (e.ExceptionObject != null)
            {
                msg = e.ExceptionObject.ToString();
            }

            var exCode = Marshal.GetLastWin32Error();
            if (exCode != 0)
            {
                msg += " ErrorCode: " + exCode.ToString("X16");
            }

            Logger.Error($"Unhandled External Exception: {msg}");
        }
    }

    private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
    {
        Logger.Error(e.Exception, "ERROR: UNOBSERVED TASK EXCEPTION");
        e.SetObserved();
    }

    private void Dispose(bool disposing)
    {
        if (!this.disposed)
        {
            if (disposing)
            {
                this.Ignite?.Dispose();
            }

            this.disposed = true;
        }
    }

    public void SetClusterEndpoints(ICollection<string> values)
    {
        if (this.Ignite != null)
        {
            throw new InvalidOperationException("Cannot configure running instances.");
        }

        switch (this.igniteConfiguration.DiscoverySpi)
        {
            case TcpDiscoverySpi tcpDiscoverySpi:
                tcpDiscoverySpi.IpFinder = new TcpDiscoveryStaticIpFinder {Endpoints = values};
                break;
            case null:
                this.igniteConfiguration.DiscoverySpi = new TcpDiscoverySpi
                {
                    IpFinder = new TcpDiscoveryStaticIpFinder {Endpoints = values}
                };
                break;
        }
    }

    public void SetServerPort(int value)
    {
        if (this.Ignite != null)
        {
            throw new InvalidOperationException("Cannot configure running instances.");
        }

        switch (this.igniteConfiguration.DiscoverySpi)
        {
            case TcpDiscoverySpi tcpDiscoverySpi:
                tcpDiscoverySpi.LocalPort = value;
                break;
            case null:
                this.igniteConfiguration.DiscoverySpi = new TcpDiscoverySpi {LocalPort = value};
                break;
        }
    }

    public void SetOnHeapMemoryLimit(int value)
    {
        if (this.Ignite != null)
        {
            throw new InvalidOperationException("Cannot configure running instances.");
        }

        this.igniteConfiguration.JvmInitialMemoryMb = value / 2;
        this.igniteConfiguration.JvmMaxMemoryMb = value;
    }

    public void SetOffHeapMemoryLimit(int value)
    {
        if (this.Ignite != null)
        {
            throw new InvalidOperationException("Cannot configure running instances.");
        }

        if (this.igniteConfiguration.DataStorageConfiguration == null)
        {
            this.igniteConfiguration.DataStorageConfiguration = new DataStorageConfiguration
            {
                WalSegmentSize = 256 * 1024 * 1024
            };
        }

        if (this.igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration != null)
        {
            this.igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration.MaxSize =
                (long)value * 1024 * 1024;
        }
        else
        {
            this.igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration =
                new DataRegionConfiguration {Name = "default", MaxSize = (long)value * 1024 * 1024};
        }

        this.igniteConfiguration.DataStorageConfiguration.MetricsEnabled = this.enableOffHeapMetrics;
        this.igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration.MetricsEnabled =
            this.enableOffHeapMetrics;
        this.igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration.PageEvictionMode =
            DataPageEvictionMode.Random2Lru;
    }

    public void SetPersistence(bool value)
    {
        if (this.Ignite != null)
        {
            throw new InvalidOperationException("Cannot configure running instances.");
        }

        if (this.igniteConfiguration.DataStorageConfiguration == null)
        {
            this.igniteConfiguration.DataStorageConfiguration = new DataStorageConfiguration
            {
                WalSegmentSize = 256 * 1024 * 1024
            };
        }

        if (this.igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration != null)
        {
            this.igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration.PersistenceEnabled =
                value;
        }
        else
        {
            this.igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration =
                new DataRegionConfiguration {Name = "default", PersistenceEnabled = value};
        }
    }

    public void SetConsistentId(string cid)
    {
        if (this.Ignite != null)
        {
            throw new InvalidOperationException("Cannot configure running instances.");
        }

        this.igniteConfiguration.ConsistentId = cid;
    }

    public async Task Run()
    {
        TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Logger.Information("Starting Ignite Server...");
        this.Ignite = Ignition.Start(this.igniteConfiguration);
        this.Ignite!.GetCluster().SetActive(true);
        this.Ignite.GetCluster().SetBaselineAutoAdjustEnabledFlag(true);
        this.Ignite.GetCluster().SetBaselineAutoAdjustTimeout(30000);
        var persistenceEnabled = this.igniteConfiguration.DataStorageConfiguration?.DefaultDataRegionConfiguration
            ?.PersistenceEnabled;
        if (persistenceEnabled.HasValue && persistenceEnabled.Value)
        {
            if (!string.IsNullOrWhiteSpace(this.igniteUserPassword))
            {
                try
                {
                    using var igniteClient = CacheFactory.ConnectAsClient(
                        CacheFactory.GetIgniteClientConfiguration(userName: "ignite", password: "ignite",
                            useSsl: this.useClientSsl,
                            certificatePath: this.sslClientCertificatePath,
                            certificatePassword: this.sslClientCertificatePassword));
                    var alterUserSqlDmlCommand =
                        CacheFactory.GetOrCreateCacheClient<string, string>(igniteClient, "alterUserSqlDmlCommand");
                    alterUserSqlDmlCommand.Query(
                        new SqlFieldsQuery($"ALTER USER \"ignite\" WITH PASSWORD '{this.igniteUserPassword}';"));
                    igniteClient.DestroyCache("alterUserSqlDmlCommand");
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

        CacheRebalancingEventListener cacheRebalancingEventListener = new(this.Ignite, Logger);
        this.Ignite.GetEvents().LocalListen(cacheRebalancingEventListener, EventType.CacheRebalancePartDataLost);
        DiscoveryEventListener discoveryEventListener = new(Logger);
        this.Ignite.GetEvents().LocalListen(discoveryEventListener, EventType.NodeJoined, EventType.NodeLeft,
            EventType.NodeFailed);

        CancellationTokenSource cts = new();
        this.Ignite.Stopped += (s, e) => tcs.SetResult(e.ToString());
        var localSpidPort = ((TcpDiscoverySpi)this.Ignite.GetConfiguration().DiscoverySpi).LocalPort;
        Logger.Information(
            $"Ignite Server is running (Local SpiDiscovery Port={localSpidPort}), press CTRL+C to terminate.");
        await tcs.Task.ConfigureAwait(false);
        Logger.Information("Ignite Server stopped.");
    }

    public void Terminate()
    {
        if (this.Ignite != null)
        {
            Ignition.Stop(this.Ignite.Name, true);
            this.Ignite = null;
        }
    }

    public void Stop()
    {
        if (this.Ignite != null)
        {
            Ignition.Stop(this.Ignite.Name, false);
            this.Ignite = null;
        }
    }
}

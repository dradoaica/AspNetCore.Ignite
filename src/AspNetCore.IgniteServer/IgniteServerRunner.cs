using Apache.Ignite.Core;
using Apache.Ignite.Core.Cache.Query;
using Apache.Ignite.Core.Client;
using Apache.Ignite.Core.Client.Cache;
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
using Serilog;
using Serilog.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCore.IgniteServer
{
    internal sealed class IgniteServerRunner : IDisposable
    {
        private static readonly Logger _logger;
        private readonly bool _enableOffHeapMetrics;
        private readonly IgniteConfiguration _igniteConfiguration;
        private readonly string _igniteUserPassword;
        private readonly string _sslClientCertificatePassword;
        private readonly string _sslClientCertificatePath;
        private readonly bool _useClientSsl;
        private bool _disposed;

        static IgniteServerRunner()
        {
            LoggerConfiguration loggerConfiguration =
                new LoggerConfiguration().ReadFrom.Configuration(Program.Configuration);
            _logger = loggerConfiguration.CreateLogger();
            Log.Logger = _logger;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        public IgniteServerRunner(TimeSpan metricsExpireTime, TimeSpan metricsLogFrequency,
            TimeSpan metricsUpdateFrequency,
            bool enableOffHeapMetrics, bool authenticationEnabled, string igniteUserPassword = null,
            string configurationFile = null, bool useSsl = false,
            string sslKeyStoreFilePath = null, string sslKeyStorePassword = null, string sslTrustStoreFilePath = null,
            string sslTrustStorePassword = null,
            bool useClientSsl = false, string sslClientCertificatePath = null,
            string sslClientCertificatePassword = null)
        {
            _enableOffHeapMetrics = enableOffHeapMetrics;
            _useClientSsl = useClientSsl;
            _sslClientCertificatePath = sslClientCertificatePath;
            _sslClientCertificatePassword = sslClientCertificatePassword;
            _igniteUserPassword = igniteUserPassword;
            _igniteConfiguration = string.IsNullOrWhiteSpace(configurationFile)
                ? GetDefaultConfiguration()
                : LoadConfiguration(configurationFile);
            _igniteConfiguration.SpringConfigUrl = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                _useClientSsl ? "config/spring-config-client-with-ssl.xml" : "config/spring-config.xml");;
            _igniteConfiguration.MetricsExpireTime = metricsExpireTime;
            _igniteConfiguration.MetricsLogFrequency = metricsLogFrequency;
            _igniteConfiguration.MetricsUpdateFrequency = metricsUpdateFrequency;
            if (authenticationEnabled)
            {
                _igniteConfiguration.AuthenticationEnabled = true;
                SetPersistence(true);
            }

            if (useSsl)
            {
                _igniteConfiguration.SslContextFactory = new SslContextFactory
                {
                    KeyStoreFilePath = sslKeyStoreFilePath,
                    KeyStorePassword = sslKeyStorePassword,
                    TrustStoreFilePath = sslTrustStoreFilePath,
                    TrustStorePassword = sslTrustStorePassword,
                    Protocol = "TLSv1.2"
                };
            }

            _igniteConfiguration.Logger = new IgniteNLogLogger();
        }

        public IIgnite Ignite { get; private set; }

        private static IgniteConfiguration LoadConfiguration(string filename)
        {
            string configStr = File.ReadAllText(filename);
            IgniteConfiguration igniteConfiguration = IgniteConfiguration.FromXml(configStr);
            return igniteConfiguration;
        }

        private IgniteConfiguration GetDefaultConfiguration()
        {
            IgniteConfiguration cfg = new()
            {
                AutoGenerateIgniteInstanceName = true,
                JvmOptions =
                    new[]
                    {
                        "-XX:+AlwaysPreTouch", "-XX:+UseG1GC", "-XX:+ScavengeBeforeFullGC",
                        "-XX:+DisableExplicitGC", "-Djava.net.preferIPv4Stack=true", "-DIGNITE_QUIET=false",
                        "-DIGNITE_WAL_MMAP=false", "-DIGNITE_WAIT_FOR_BACKUPS_ON_SHUTDOWN=true",
                        "--add-exports=java.base/jdk.internal.misc=ALL-UNNAMED",
                        "--add-exports=java.base/sun.nio.ch=ALL-UNNAMED",
                        "--add-exports=java.management/com.sun.jmx.mbeanserver=ALL-UNNAMED",
                        "--add-exports=jdk.internal.jvmstat/sun.jvmstat.monitor=ALL-UNNAMED",
                        "--add-exports=java.base/sun.reflect.generics.reflectiveObjects=ALL-UNNAMED",
                        "--add-opens=jdk.management/com.sun.management.internal=ALL-UNNAMED",
                        "--illegal-access=permit"
                    },
                PeerAssemblyLoadingMode = PeerAssemblyLoadingMode.CurrentAppDomain,
                DataStorageConfiguration = new DataStorageConfiguration
                {
                    WalSegmentSize = 256 * 1024 * 1024
                },
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
                    EventType.NodeJoined, EventType.NodeLeft, EventType.NodeFailed,
                    EventType.CacheRebalancePartDataLost
                }
            };
            return cfg;
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                _logger?.Error(ex, "UnhandledException");
            }
            else
            {
                string msg = "";
                if (e.ExceptionObject != null)
                {
                    msg = e.ExceptionObject.ToString();
                }

                int exCode = Marshal.GetLastWin32Error();
                if (exCode != 0)
                {
                    msg += " ErrorCode: " + exCode.ToString("X16");
                }

                _logger?.Error(string.Format("Unhandled External Exception: {0}", msg));
            }
        }

        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            _logger?.Error(e.Exception, "ERROR: UNOBSERVED TASK EXCEPTION");
            e.SetObserved();
        }

        private void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Ignite?.Dispose();
                }

                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        public void SetClusterEnpoints(ICollection<string> values)
        {
            if (Ignite != null)
            {
                throw new InvalidOperationException("Cannot configure running instances.");
            }

            switch (_igniteConfiguration.DiscoverySpi)
            {
                case TcpDiscoverySpi tcpDiscoverySpi:
                    tcpDiscoverySpi.IpFinder = new TcpDiscoveryStaticIpFinder { Endpoints = values };
                    break;
                case null:
                    _igniteConfiguration.DiscoverySpi = new TcpDiscoverySpi
                    {
                        IpFinder = new TcpDiscoveryStaticIpFinder { Endpoints = values }
                    };
                    break;
            }
        }

        public void SetServerPort(int value)
        {
            if (Ignite != null)
            {
                throw new InvalidOperationException("Cannot configure running instances.");
            }

            switch (_igniteConfiguration.DiscoverySpi)
            {
                case TcpDiscoverySpi tcpDiscoverySpi:
                    tcpDiscoverySpi.LocalPort = value;
                    break;
                case null:
                    _igniteConfiguration.DiscoverySpi = new TcpDiscoverySpi { LocalPort = value };
                    break;
            }
        }

        public void SetOnHeapMemoryLimit(int value)
        {
            if (Ignite != null)
            {
                throw new InvalidOperationException("Cannot configure running instances.");
            }

            _igniteConfiguration.JvmInitialMemoryMb = value / 2;
            _igniteConfiguration.JvmMaxMemoryMb = value;
        }

        public void SetOffHeapMemoryLimit(int value)
        {
            if (Ignite != null)
            {
                throw new InvalidOperationException("Cannot configure running instances.");
            }

            if (_igniteConfiguration.DataStorageConfiguration == null)
            {
                _igniteConfiguration.DataStorageConfiguration = new DataStorageConfiguration
                {
                    WalSegmentSize = 256 * 1024 * 1024
                };
            }

            if (_igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration != null)
            {
                _igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration.MaxSize =
                    (long)value * 1024 * 1024;
            }
            else
            {
                _igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration =
                    new DataRegionConfiguration { Name = "default", MaxSize = (long)value * 1024 * 1024 };
            }

            _igniteConfiguration.DataStorageConfiguration.MetricsEnabled = _enableOffHeapMetrics;
            _igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration.MetricsEnabled =
                _enableOffHeapMetrics;
            _igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration.PageEvictionMode =
                DataPageEvictionMode.Random2Lru;
        }

        public void SetPersistence(bool value)
        {
            if (Ignite != null)
            {
                throw new InvalidOperationException("Cannot configure running instances.");
            }

            if (_igniteConfiguration.DataStorageConfiguration == null)
            {
                _igniteConfiguration.DataStorageConfiguration = new DataStorageConfiguration
                {
                    WalSegmentSize = 256 * 1024 * 1024
                };
            }

            if (_igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration != null)
            {
                _igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration.PersistenceEnabled = value;
            }
            else
            {
                _igniteConfiguration.DataStorageConfiguration.DefaultDataRegionConfiguration =
                    new DataRegionConfiguration { Name = "default", PersistenceEnabled = value };
            }
        }

        public void SetConsistentId(string cid)
        {
            if (Ignite != null)
            {
                throw new InvalidOperationException("Cannot configure running instances.");
            }

            _igniteConfiguration.ConsistentId = cid;
        }

        public async Task Run()
        {
            TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _logger.Information("Starting Ignite Server...");
            Ignite = Ignition.Start(_igniteConfiguration);
            Ignite.GetCluster().SetActive(true);
            Ignite.GetCluster().SetBaselineAutoAdjustEnabledFlag(true);
            Ignite.GetCluster().SetBaselineAutoAdjustTimeout(30000);
            bool? persistenceEnabled = _igniteConfiguration.DataStorageConfiguration?.DefaultDataRegionConfiguration
                ?.PersistenceEnabled;
            if (persistenceEnabled.HasValue && persistenceEnabled.Value)
            {
                if (!string.IsNullOrWhiteSpace(_igniteUserPassword))
                {
                    try
                    {
                        using IIgniteClient igniteClient = CacheFactory.ConnectAsClient(
                            CacheFactory.GetIgniteClientConfiguration(userName: "ignite", password: "ignite",
                                useSsl: _useClientSsl,
                                certificatePath: _sslClientCertificatePath,
                                certificatePassword: _sslClientCertificatePassword));
                        ICacheClient<string, string> alterUserSqlDmlCommand =
                            CacheFactory.GetOrCreateCacheClient<string, string>(igniteClient, "alterUserSqlDmlCommand");
                        alterUserSqlDmlCommand.Query(
                            new SqlFieldsQuery($"ALTER USER \"ignite\" WITH PASSWORD '{_igniteUserPassword}';"));
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

            CacheRebalancingEventListener cacheRebalancingEventListener = new(Ignite, _logger);
            Ignite.GetEvents().LocalListen(cacheRebalancingEventListener, EventType.CacheRebalancePartDataLost);
            DiscoveryEventListener discoveryEventListener = new(Ignite, _logger);
            Ignite.GetEvents().LocalListen(discoveryEventListener, EventType.NodeJoined, EventType.NodeLeft,
                EventType.NodeFailed);

            CancellationTokenSource cts = new();
            Ignite.Stopped += (s, e) => tcs.SetResult(e.ToString());
            int localSpidPort = (Ignite.GetConfiguration().DiscoverySpi as TcpDiscoverySpi).LocalPort;
            _logger.Information(
                $"Ignite Server is running (Local SpiDiscovery Port={localSpidPort}), press CTRL+C to terminate.");
            await tcs.Task.ConfigureAwait(false);
            _logger.Information("Ignite Server stopped.");
        }

        public void Terminate()
        {
            if (Ignite != null)
            {
                Ignition.Stop(Ignite.Name, true);
                Ignite = null;
            }
        }

        public void Stop()
        {
            if (Ignite != null)
            {
                Ignition.Stop(Ignite.Name, false);
                Ignite = null;
            }
        }
    }
}

using AspNetCore.IgniteServer.Utils;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;
using NLog;
using NLog.Config;
using NLog.Targets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AspNetCore.IgniteServer
{
    internal class Program
    {
        private enum EventIds : int
        {
            EVT_METRICS,
            EVT_IGNITE_STATUS
        };

        private const int DEFAULT_OFF_HEAP_MEMORY = 4096;
        private const int DEFAULT_ON_HEAP_MEMORY = 1024;
        private static readonly List<string> _defaultClusterEnpoints = new List<string> { $"{DnsUtils.GetLocalIPAddress()}:47500" };
        private static readonly MemoryCache _memoryCache = new MemoryCache(new MemoryCacheOptions { ExpirationScanFrequency = TimeSpan.FromSeconds(5) });
        private static volatile bool _shouldStart = true;
        private static IgniteServerRunner _server;

        public static IConfiguration Configuration { get; private set; }

        private static void Main(string[] args)
        {
            Environment.SetEnvironmentVariable("OPTION_LIBS", "ignite-kubernetes, ignite-rest-http");
            Configuration = CreateConfiguration();
            SetupIgniteLogging();
            CommandLineApplication commandLineApplication = new CommandLineApplication(true)
            {
                Name = "AspNetCore.IgniteServer"
            };
            commandLineApplication.HelpOption("-?|-Help");
            CommandOption configFileArgument = commandLineApplication.Option("-ConfigFile", "XML configuration file. If not file is specified then default configuration is used.", CommandOptionType.SingleValue);
            CommandOption offheapArgument = commandLineApplication.Option("-Offheap", "Size of off-heap memory given in megabytes.", CommandOptionType.SingleValue);
            CommandOption onheapArgument = commandLineApplication.Option("-Onheap", "Size of on-heap memory given in megabytes.", CommandOptionType.SingleValue);
            CommandOption leaderNodeArgument = commandLineApplication.Option("-SetLeader", "Set this node as the leader of the cluster.", CommandOptionType.NoValue);
            CommandOption serverPortArgument = commandLineApplication.Option("-SpiPort", "Specifies port for Discovery Spi.", CommandOptionType.SingleValue);
            CommandOption clusterEnpointArgument = commandLineApplication.Option("-Cluster", "Specifies IP address and port of a cluster node. Multiple nodes can be specified.", CommandOptionType.MultipleValue);
            CommandOption consistentIdArgument = commandLineApplication.Option("-ConsistentId", "Specifies as a consistent id of the node. This value is used in topology.", CommandOptionType.SingleValue);
            CommandOption persistenceEnabled = commandLineApplication.Option("-PersistenceEnabled", "If set, it enables persistence mode.", CommandOptionType.NoValue);
            commandLineApplication.OnExecute(async () =>
            {
                bool useTcpDiscoveryStaticIpFinder = "true".Equals(Configuration["USE_TCP_DISCOVERY_STATIC_IP_FINDER"], StringComparison.InvariantCultureIgnoreCase);
                bool enableAuthentication = "true".Equals(Configuration["ENABLE_AUTHENTICATION"], StringComparison.InvariantCultureIgnoreCase);
                string k8sNamespace = Configuration["K8S_NAMESPACE"];
                string k8sServiceName = Configuration["K8S_SERVICE_NAME"];
                string igniteUserPassword = Configuration["IGNITE_USER_PASSWORD"];
                bool useSsl = "true".Equals(Configuration["USE_SSL"], StringComparison.InvariantCultureIgnoreCase);
                string sslKeyStoreFilePath = Configuration["SSL_KEY_STORE_FILE_PATH"];
                string sslKeyStorePassword = Configuration["SSL_KEY_STORE_PASSWORD"];
                string sslTrustStoreFilePath = Configuration["SSL_TRUST_STORE_FILE_PATH"];
                string sslTrustStorePassword = Configuration["SSL_TRUST_STORE_PASSWORD"];
                bool useClientSsl = "true".Equals(Configuration["USE_CLIENT_SSL"], StringComparison.InvariantCultureIgnoreCase);
                string sslClientCertificatePath = Configuration["SSL_CLIENT_CERTIFICATE_PATH"];
                string sslClientCertificatePassword = Configuration["SSL_CLIENT_CERTIFICATE_PASSWORD"];
                string springConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config", useClientSsl ? "spring-config-client-with-ssl.xml" : "spring-config.xml");
                string springConfigText = File.ReadAllText(springConfigPath, Encoding.UTF8);
                springConfigText = springConfigText?.Replace("K8S_NAMESPACE", k8sNamespace)?.Replace("K8S_SERVICE_NAME", k8sServiceName);
                if (useClientSsl)
                {
                    springConfigText = springConfigText?.Replace("SSL_KEY_STORE_FILE_PATH", sslKeyStoreFilePath)?.Replace("SSL_KEY_STORE_PASSWORD", sslKeyStorePassword)
                        ?.Replace("SSL_TRUST_STORE_FILE_PATH", sslTrustStoreFilePath)?.Replace("SSL_TRUST_STORE_PASSWORD", sslTrustStorePassword);
                }

                File.WriteAllText(springConfigPath, springConfigText, Encoding.UTF8);
                string configFile = configFileArgument.HasValue() ? configFileArgument.Value() : null;
                _server = new IgniteServerRunner(enableAuthentication, igniteUserPassword, configFile, useSsl,
                    sslKeyStoreFilePath, sslKeyStorePassword, sslTrustStoreFilePath, sslTrustStorePassword,
                    useClientSsl, sslClientCertificatePath, sslClientCertificatePassword);
                if (offheapArgument.HasValue())
                {
                    _server.SetOffHeapMemoryLimit(int.Parse(offheapArgument.Value()));
                }
                else
                {
                    _server.SetOffHeapMemoryLimit(DEFAULT_OFF_HEAP_MEMORY);
                }

                if (onheapArgument.HasValue())
                {
                    _server.SetOnHeapMemoryLimit(int.Parse(onheapArgument.Value()));
                }
                else
                {
                    _server.SetOnHeapMemoryLimit(DEFAULT_ON_HEAP_MEMORY);
                }

                if (serverPortArgument.HasValue())
                {
                    _server.SetServerPort(int.Parse(serverPortArgument.Value()));
                }

                if (clusterEnpointArgument.HasValue())
                {
                    _server.SetClusterEnpoints(clusterEnpointArgument.Values);
                }
                else if (useTcpDiscoveryStaticIpFinder)
                {
                    _server.SetClusterEnpoints(_defaultClusterEnpoints);
                }

                if (consistentIdArgument.HasValue())
                {
                    _server.SetConsistentId(consistentIdArgument.Value());
                }

                if (persistenceEnabled.HasValue())
                {
                    _server.SetPersistence(true);
                }

                FileSystemWatcher sslKeyStoreFsw = new FileSystemWatcher
                {
                    Filter = Path.GetFileName(sslKeyStoreFilePath),
                    Path = Path.GetDirectoryName(sslKeyStoreFilePath),
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                sslKeyStoreFsw.Created += OnSslFileCreatedOrChanged;
                sslKeyStoreFsw.Changed += OnSslFileCreatedOrChanged;
                FileSystemWatcher sslTrustStoreFsw = new FileSystemWatcher
                {
                    Filter = Path.GetFileName(sslTrustStoreFilePath),
                    Path = Path.GetDirectoryName(sslTrustStoreFilePath),
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                sslTrustStoreFsw.Created += OnSslFileCreatedOrChanged;
                sslTrustStoreFsw.Changed += OnSslFileCreatedOrChanged;
                FileSystemWatcher sslClientCertificateFsw = new FileSystemWatcher
                {
                    Filter = Path.GetFileName(sslClientCertificatePath),
                    Path = Path.GetDirectoryName(sslClientCertificatePath),
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true
                };
                sslClientCertificateFsw.Created += OnSslFileCreatedOrChanged;
                sslClientCertificateFsw.Changed += OnSslFileCreatedOrChanged;
                try
                {
                    while (_shouldStart)
                    {
                        _shouldStart = false;
                        await _server.Run();
                    }
                }
                finally
                {
                    sslKeyStoreFsw.Created -= OnSslFileCreatedOrChanged;
                    sslKeyStoreFsw.Changed -= OnSslFileCreatedOrChanged;
                    sslTrustStoreFsw.Created -= OnSslFileCreatedOrChanged;
                    sslTrustStoreFsw.Changed -= OnSslFileCreatedOrChanged;
                    sslClientCertificateFsw.Created -= OnSslFileCreatedOrChanged;
                    sslClientCertificateFsw.Changed -= OnSslFileCreatedOrChanged;
                }

                return 0;
            });

            try
            {
                commandLineApplication.Execute(args);
            }
            catch (CommandParsingException e)
            {
                commandLineApplication.Error.WriteLine($"ERROR: {e.Message}");
                commandLineApplication.ShowHelp();
            }
            catch (ArgumentException e)
            {
                commandLineApplication.Error.WriteLine($"ERROR: {e.Message}");
                commandLineApplication.ShowHelp();
            }
            finally
            {
                _server?.Dispose();
            }
        }

        private static void OnSslFileCreatedOrChanged(object sender, FileSystemEventArgs e)
        {
            TaskCompletionSource<bool> taskCompletionSource = new TaskCompletionSource<bool>();
            const int absoluteExpirationRelativeToNowInSeconds = 10;
            CancellationChangeToken expirationToken = new CancellationChangeToken(new CancellationTokenSource(TimeSpan.FromSeconds(absoluteExpirationRelativeToNowInSeconds + .01)).Token);
            MemoryCacheEntryOptions memoryCacheEntryOptions = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(absoluteExpirationRelativeToNowInSeconds)
            };
            memoryCacheEntryOptions.AddExpirationToken(expirationToken);
            memoryCacheEntryOptions.PostEvictionCallbacks.Add(new PostEvictionCallbackRegistration
            {
                EvictionCallback = (k, _, r, __) =>
                {
                    if (k is string kStr && kStr == nameof(OnSslFileCreatedOrChanged) && (r == EvictionReason.Expired || r == EvictionReason.TokenExpired) && !_shouldStart)
                    {
                        _shouldStart = true;
                        _server.Terminate();
                    }
                }
            });
            _memoryCache.Set(nameof(OnSslFileCreatedOrChanged), taskCompletionSource, memoryCacheEntryOptions);
        }

        private static void SetupIgniteLogging()
        {
            // Step 1. Create configuration object 
            LoggingConfiguration config = new LoggingConfiguration();
            // Step 2. Create targets
            ColoredConsoleTarget consoleTarget = new ColoredConsoleTarget("console")
            {
                Layout = @"[${date:format=HH\:mm\:ss}] ${level}: ${message} ${exception}"
            };
            config.AddTarget(consoleTarget);
            SerilogTarget serilogTarget = new SerilogTarget("serilog");
            config.AddTarget(serilogTarget);
            // Step 3. Define rules            
            config.AddRule(LogLevel.Info, LogLevel.Fatal, consoleTarget); // all to console
            config.AddRule(LogLevel.Info, LogLevel.Fatal, serilogTarget); // all to serilog
            // Step 4. Activate the configuration
            LogManager.Configuration = config;
        }

        private static IConfiguration CreateConfiguration()
        {
            IConfigurationBuilder configurationBuilder = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddJsonFile(Path.Combine("config", "serilog.config.json"), optional: false, reloadOnChange: true)
                .AddEnvironmentVariables();
            IConfigurationRoot configuration = configurationBuilder.Build();
            return configuration;
        }
    }
}
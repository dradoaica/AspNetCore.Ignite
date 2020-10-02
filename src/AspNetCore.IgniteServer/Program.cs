using AspNetCore.IgniteServer.Utils;
using Microsoft.Extensions.CommandLineUtils;
using Microsoft.Extensions.Configuration;
using NLog;
using NLog.Config;
using NLog.Targets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AspNetCore.IgniteServer
{
    internal class Program
    {
        private const int DEFAULT_OFF_HEAP_MEMORY = 4096;
        private const int DEFAULT_ON_HEAP_MEMORY = 1024;
        private static readonly List<string> _defaultClusterEnpoints = new List<string> { $"{DnsUtils.GetLocalIPAddress()}:47500" };
        private enum EventIds : int { EVT_METRICS, EVT_IGNITE_STATUS };

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
                using (IgniteServerRunner server = new IgniteServerRunner(enableAuthentication, igniteUserPassword, configFile, useSsl,
                    sslKeyStoreFilePath, sslKeyStorePassword, sslTrustStoreFilePath, sslTrustStorePassword,
                    useClientSsl, sslClientCertificatePath, sslClientCertificatePassword))
                {
                    if (offheapArgument.HasValue())
                    {
                        server.SetOffHeapMemoryLimit(int.Parse(offheapArgument.Value()));
                    }
                    else
                    {
                        server.SetOffHeapMemoryLimit(DEFAULT_OFF_HEAP_MEMORY);
                    }

                    if (onheapArgument.HasValue())
                    {
                        server.SetOnHeapMemoryLimit(int.Parse(onheapArgument.Value()));
                    }
                    else
                    {
                        server.SetOnHeapMemoryLimit(DEFAULT_ON_HEAP_MEMORY);
                    }

                    if (serverPortArgument.HasValue())
                    {
                        server.SetServerPort(int.Parse(serverPortArgument.Value()));
                    }

                    if (clusterEnpointArgument.HasValue())
                    {
                        server.SetClusterEnpoints(clusterEnpointArgument.Values);
                    }
					else if (useTcpDiscoveryStaticIpFinder)
                    {
                         server.SetClusterEnpoints(_defaultClusterEnpoints);
                    }

                    if (consistentIdArgument.HasValue())
                    {
                        server.SetConsistentId(consistentIdArgument.Value());
                    }

                    if (persistenceEnabled.HasValue())
                    {
                        server.SetPersistence(true);
                    }

                    await server.Run();
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
            SerilogTarget serilogTarget = new SerilogTarget();
            config.AddTarget(nameof(SerilogTarget), serilogTarget);
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

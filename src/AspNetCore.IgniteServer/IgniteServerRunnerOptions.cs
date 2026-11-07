using Apache.Ignite.Core.Configuration;
using System;
using System.Collections.Generic;

namespace AspNetCore.IgniteServer;

internal sealed record IgniteServerRunnerOptions
{
    public TimeSpan MetricsExpireTime { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan MetricsLogFrequency { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan MetricsUpdateFrequency { get; init; } = TimeSpan.FromSeconds(60);
    public bool EnableOffHeapMetrics { get; init; }
    public bool AuthenticationEnabled { get; init; }
    public string? IgniteUserPassword { get; init; }

    /// <summary>The password to replace with <see cref="IgniteUserPassword" /> when rotating it on an existing cluster.</summary>
    public string? IgniteUserPreviousPassword { get; init; }
    public string? ConfigurationFile { get; init; }

    /// <summary>Loads .NET assemblies sent by other nodes (and so runs their code): off unless the cluster is trusted.</summary>
    public bool PeerAssemblyLoading { get; init; }
    public string? SpringConfigPath { get; init; }

    /// <summary>Persistent clusters are activated only once this many server nodes have joined.</summary>
    public int ActivationMinServerNodes { get; init; } = 1;

    public TimeSpan BaselineAutoAdjustTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The node is ready only once the caches it receives partitions of are rebalanced, so that rolling restarts wait
    ///     for the data to be back before stopping the next node; at most <see cref="RebalanceReadinessMaxWait" />.
    /// </summary>
    public bool ReadyWhenRebalanced { get; init; } = true;

    public TimeSpan RebalanceReadinessMaxWait { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>When set, lost partitions are reset automatically (the lost data is NOT recovered).</summary>
    public bool AutoResetLostPartitions { get; init; } = true;

    public bool UseSsl { get; init; }

    /// <summary>The Java SSL protocol; "TLS" negotiates the highest version both sides support (TLS 1.3 on Java 17).</summary>
    public string SslProtocol { get; init; } = "TLS";
    public string SslKeyStoreType { get; init; } = "JKS";
    public string SslTrustStoreType { get; init; } = "JKS";
    public string SslKeyAlgorithm { get; init; } = "SunX509";
    public string? SslKeyStoreFilePath { get; init; }
    public string? SslKeyStorePassword { get; init; }
    public string? SslTrustStoreFilePath { get; init; }
    public string? SslTrustStorePassword { get; init; }
    public bool UseClientSsl { get; init; }
    public string? SslClientCertificatePath { get; init; }
    public string? SslClientCertificatePassword { get; init; }

    // null: keep the value of the default configuration (or of -ConfigFile)
    public int? JvmMaxMetaspaceMb { get; init; }
    public int? JvmReservedCodeCacheMb { get; init; }
    public int? JvmMaxDirectMemoryMb { get; init; }
    public int? JvmG1HeapRegionMb { get; init; }
    public IReadOnlyList<string> ExtraJvmOptions { get; init; } = [];
    public string? ExtraClasspath { get; init; }
    public TimeSpan? FailureDetectionTimeout { get; init; }
    public TimeSpan? ClientFailureDetectionTimeout { get; init; }
    public TimeSpan? NetworkTimeout { get; init; }
    public int? PublicThreadPoolSize { get; init; }
    public int? SystemThreadPoolSize { get; init; }
    public int? QueryThreadPoolSize { get; init; }
    public int? ServiceThreadPoolSize { get; init; }
    public int? StripedThreadPoolSize { get; init; }
    public int? DataStreamerThreadPoolSize { get; init; }
    public string? StoragePath { get; init; }
    public string? WalPath { get; init; }
    public string? WalArchivePath { get; init; }
    public bool? WalCompactionEnabled { get; init; }
    public WalMode? WalMode { get; init; }
    public DataPageEvictionMode? PageEvictionMode { get; init; }
}

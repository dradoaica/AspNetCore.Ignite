using System;

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

    /// <summary>When set, lost partitions are reset automatically (the lost data is NOT recovered).</summary>
    public bool AutoResetLostPartitions { get; init; } = true;

    public bool UseSsl { get; init; }

    /// <summary>The Java SSL protocol; "TLS" negotiates the highest version both sides support (TLS 1.3 on Java 17).</summary>
    public string SslProtocol { get; init; } = "TLS";
    public string? SslKeyStoreFilePath { get; init; }
    public string? SslKeyStorePassword { get; init; }
    public string? SslTrustStoreFilePath { get; init; }
    public string? SslTrustStorePassword { get; init; }
    public bool UseClientSsl { get; init; }
    public string? SslClientCertificatePath { get; init; }
    public string? SslClientCertificatePassword { get; init; }
}

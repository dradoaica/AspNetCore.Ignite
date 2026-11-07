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
    public string? ConfigurationFile { get; init; }
    public string? SpringConfigPath { get; init; }

    /// <summary>Persistent clusters are activated only once this many server nodes have joined.</summary>
    public int ActivationMinServerNodes { get; init; } = 1;

    /// <summary>When set, lost partitions are reset automatically (the lost data is NOT recovered).</summary>
    public bool AutoResetLostPartitions { get; init; } = true;

    public bool UseSsl { get; init; }
    public string? SslKeyStoreFilePath { get; init; }
    public string? SslKeyStorePassword { get; init; }
    public string? SslTrustStoreFilePath { get; init; }
    public string? SslTrustStorePassword { get; init; }
    public bool UseClientSsl { get; init; }
    public string? SslClientCertificatePath { get; init; }
    public string? SslClientCertificatePassword { get; init; }
}

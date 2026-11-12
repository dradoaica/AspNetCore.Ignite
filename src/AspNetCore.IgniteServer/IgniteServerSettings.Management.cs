using Microsoft.Extensions.Configuration;
using System.Collections.Generic;

namespace AspNetCore.IgniteServer;

// Health, metrics, REST, readiness and shutdown.
internal sealed partial class IgniteServerSettings
{
    [ConfigurationKeyName("REST_ENABLED")]
    public bool RestEnabled { get; set; }

    [ConfigurationKeyName("READY_WHEN_REBALANCED")]
    public bool ReadyWhenRebalanced { get; set; } = true;

    [ConfigurationKeyName("REBALANCE_READINESS_MAX_WAIT_IN_SECONDS")]
    public int RebalanceReadinessMaxWaitInSeconds { get; set; } = 600;

    [ConfigurationKeyName("METRICS_EXPIRE_TIME_IN_HOURS")]
    public int MetricsExpireTimeInHours { get; set; } = 24;

    [ConfigurationKeyName("METRICS_LOG_FREQUENCY_IN_MINUTES")]
    public int MetricsLogFrequencyInMinutes { get; set; } = 5;

    [ConfigurationKeyName("METRICS_UPDATE_FREQUENCY_IN_SECONDS")]
    public int MetricsUpdateFrequencyInSeconds { get; set; } = 60;

    /// <summary>The ignite_cache_* metrics: one series per cache, so they can be turned off with many caches.</summary>
    [ConfigurationKeyName("METRICS_PER_CACHE")]
    public bool MetricsPerCache { get; set; } = true;

    [ConfigurationKeyName("ENABLE_OFF_HEAP_METRICS")]
    public bool EnableOffHeapMetrics { get; set; } = true;

    [ConfigurationKeyName("MANAGEMENT_ENABLED")]
    public bool ManagementEnabled { get; set; } = true;

    [ConfigurationKeyName("MANAGEMENT_PORT")]
    public int ManagementPort { get; set; } = 9464;

    [ConfigurationKeyName("GRACEFUL_STOP_TIMEOUT_IN_SECONDS")]
    public int GracefulStopTimeoutInSeconds { get; set; } = 20;

    [ConfigurationKeyName("SHUTDOWN_TIMEOUT_IN_SECONDS")]
    public int ShutdownTimeoutInSeconds { get; set; } = 30;

    private void ValidateManagement(List<string> errors)
    {
        RequirePositive(errors, "REBALANCE_READINESS_MAX_WAIT_IN_SECONDS", RebalanceReadinessMaxWaitInSeconds);
        RequirePositive(errors, "SHUTDOWN_TIMEOUT_IN_SECONDS", ShutdownTimeoutInSeconds);
        if (GracefulStopTimeoutInSeconds <= 0 || GracefulStopTimeoutInSeconds >= ShutdownTimeoutInSeconds)
        {
            errors.Add(
                "GRACEFUL_STOP_TIMEOUT_IN_SECONDS must be positive and less than SHUTDOWN_TIMEOUT_IN_SECONDS (the " +
                "host waits for the stop)."
            );
        }

        if (ManagementPort is <= 0 or > 65535)
        {
            errors.Add("MANAGEMENT_PORT must be a valid port.");
        }
    }
}

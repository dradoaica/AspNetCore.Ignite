using Microsoft.Extensions.Configuration;

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
}

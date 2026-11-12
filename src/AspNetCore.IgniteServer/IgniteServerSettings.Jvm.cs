using Microsoft.Extensions.Configuration;
using System.Collections.Generic;

namespace AspNetCore.IgniteServer;

// Memory, JVM options, timeouts and thread pools. A nullable setting applies only when it is set; otherwise the
// default configuration (or -ConfigFile) decides.
internal sealed partial class IgniteServerSettings
{
    [ConfigurationKeyName("DEFAULT_ON_HEAP_MEMORY")]
    public int OnHeapMemory { get; set; } = 1024;

    [ConfigurationKeyName("DEFAULT_OFF_HEAP_MEMORY")]
    public int OffHeapMemory { get; set; } = 2048;

    [ConfigurationKeyName("JVM_MAX_METASPACE_MB")]
    public int? JvmMaxMetaspaceMb { get; set; }

    [ConfigurationKeyName("JVM_RESERVED_CODE_CACHE_MB")]
    public int? JvmReservedCodeCacheMb { get; set; }

    [ConfigurationKeyName("JVM_MAX_DIRECT_MEMORY_MB")]
    public int? JvmMaxDirectMemoryMb { get; set; }

    [ConfigurationKeyName("JVM_G1_HEAP_REGION_MB")]
    public int? JvmG1HeapRegionMb { get; set; }

    /// <summary>Extra JVM options, separated by spaces.</summary>
    [ConfigurationKeyName("EXTRA_JVM_OPTS")]
    public string? ExtraJvmOpts { get; set; }

    /// <summary>
    ///     Extra Java classpath entries separated by ':' (a directory adds its *.jar files), e.g., extra modules.
    /// </summary>
    [ConfigurationKeyName("EXTRA_CLASSPATH")]
    public string? ExtraClasspath { get; set; }

    [ConfigurationKeyName("FAILURE_DETECTION_TIMEOUT_MS")]
    public int? FailureDetectionTimeoutMs { get; set; }

    [ConfigurationKeyName("CLIENT_FAILURE_DETECTION_TIMEOUT_MS")]
    public int? ClientFailureDetectionTimeoutMs { get; set; }

    [ConfigurationKeyName("NETWORK_TIMEOUT_MS")]
    public int? NetworkTimeoutMs { get; set; }

    [ConfigurationKeyName("PUBLIC_THREAD_POOL_SIZE")]
    public int? PublicThreadPoolSize { get; set; }

    [ConfigurationKeyName("SYSTEM_THREAD_POOL_SIZE")]
    public int? SystemThreadPoolSize { get; set; }

    [ConfigurationKeyName("QUERY_THREAD_POOL_SIZE")]
    public int? QueryThreadPoolSize { get; set; }

    [ConfigurationKeyName("SERVICE_THREAD_POOL_SIZE")]
    public int? ServiceThreadPoolSize { get; set; }

    [ConfigurationKeyName("STRIPED_THREAD_POOL_SIZE")]
    public int? StripedThreadPoolSize { get; set; }

    [ConfigurationKeyName("DATA_STREAMER_THREAD_POOL_SIZE")]
    public int? DataStreamerThreadPoolSize { get; set; }

    private void ValidateJvm(List<string> errors)
    {
        RequirePositive(errors, "DEFAULT_ON_HEAP_MEMORY (-OnHeap)", OnHeapMemory);
        RequirePositive(errors, "DEFAULT_OFF_HEAP_MEMORY (-OffHeap)", OffHeapMemory);
        RequirePositive(errors, "JVM_MAX_METASPACE_MB", JvmMaxMetaspaceMb);
        RequirePositive(errors, "JVM_RESERVED_CODE_CACHE_MB", JvmReservedCodeCacheMb);
        RequirePositive(errors, "JVM_MAX_DIRECT_MEMORY_MB", JvmMaxDirectMemoryMb);
        RequirePositive(errors, "JVM_G1_HEAP_REGION_MB", JvmG1HeapRegionMb);
        RequirePositive(errors, "FAILURE_DETECTION_TIMEOUT_MS", FailureDetectionTimeoutMs);
        RequirePositive(errors, "CLIENT_FAILURE_DETECTION_TIMEOUT_MS", ClientFailureDetectionTimeoutMs);
        RequirePositive(errors, "NETWORK_TIMEOUT_MS", NetworkTimeoutMs);
        RequirePositive(errors, "PUBLIC_THREAD_POOL_SIZE", PublicThreadPoolSize);
        RequirePositive(errors, "SYSTEM_THREAD_POOL_SIZE", SystemThreadPoolSize);
        RequirePositive(errors, "QUERY_THREAD_POOL_SIZE", QueryThreadPoolSize);
        RequirePositive(errors, "SERVICE_THREAD_POOL_SIZE", ServiceThreadPoolSize);
        RequirePositive(errors, "STRIPED_THREAD_POOL_SIZE", StripedThreadPoolSize);
        RequirePositive(errors, "DATA_STREAMER_THREAD_POOL_SIZE", DataStreamerThreadPoolSize);
        if (JvmG1HeapRegionMb is { } regionMb && (regionMb > 32 || (regionMb & (regionMb - 1)) != 0))
        {
            errors.Add("JVM_G1_HEAP_REGION_MB must be a power of 2 from 1 to 32.");
        }

        if (ClientFailureDetectionTimeoutMs < MetricsUpdateFrequencyInSeconds * 1000L)
        {
            errors.Add(
                "CLIENT_FAILURE_DETECTION_TIMEOUT_MS must not be less than METRICS_UPDATE_FREQUENCY_IN_SECONDS " +
                "(Ignite requires it)."
            );
        }
    }
}

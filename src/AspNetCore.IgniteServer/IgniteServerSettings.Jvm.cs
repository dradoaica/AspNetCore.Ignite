using Microsoft.Extensions.Configuration;

namespace AspNetCore.IgniteServer;

// Memory, JVM options, timeouts and thread pools; the nullable ones apply only when set (their defaults are those of the default configuration, or of -ConfigFile).
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

    /// <summary>Extra Java classpath entries separated by ':' (a directory adds its *.jar files), e.g., extra modules.</summary>
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
}
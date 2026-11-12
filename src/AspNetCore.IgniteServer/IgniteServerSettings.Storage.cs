using Apache.Ignite.Core.Configuration;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;

namespace AspNetCore.IgniteServer;

// Persistence, data and WAL storage. A nullable setting applies only when it is set.
internal sealed partial class IgniteServerSettings
{
    [ConfigurationKeyName("PERSISTENCE_ENABLED")]
    public bool PersistenceEnabled { get; set; }

    /// <summary>Data and indexes (persistence); a dedicated volume is recommended.</summary>
    [ConfigurationKeyName("STORAGE_PATH")]
    public string? StoragePath { get; set; }

    /// <summary>The write-ahead log; a dedicated volume (disk) is recommended.</summary>
    [ConfigurationKeyName("WAL_PATH")]
    public string? WalPath { get; set; }

    [ConfigurationKeyName("WAL_ARCHIVE_PATH")]
    public string? WalArchivePath { get; set; }

    /// <summary>
    ///     Page eviction of in-memory regions when full: Disabled, RandomLru or Random2Lru (the default); persistent
    ///     regions write pages to disk instead.
    /// </summary>
    [ConfigurationKeyName("PAGE_EVICTION_MODE")]
    public string? PageEvictionMode { get; set; }

    [ConfigurationKeyName("WAL_COMPACTION_ENABLED")]
    public bool? WalCompactionEnabled { get; set; }

    /// <summary>Fsync, LogOnly (Ignite's default), Background or None (LOG_ONLY style names work too).</summary>
    [ConfigurationKeyName("WAL_MODE")]
    public string? WalMode { get; set; }

    private void ValidateStorage(List<string> errors)
    {
        if (PageEvictionMode != null && ParseEnum<DataPageEvictionMode>(PageEvictionMode) == null)
        {
            errors.Add("PAGE_EVICTION_MODE must be one of Disabled, RandomLru, Random2Lru.");
        }

        if (WalMode != null && ParseEnum<WalMode>(WalMode) == null)
        {
            errors.Add("WAL_MODE must be one of Fsync, LogOnly, Background, None.");
        }
    }
}

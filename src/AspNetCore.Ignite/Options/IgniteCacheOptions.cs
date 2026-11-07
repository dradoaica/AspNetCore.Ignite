namespace AspNetCore.Ignite.Options;

/// <summary>Defaults of the caches created by <see cref="CacheFactory" />.</summary>
public sealed class IgniteCacheOptions
{
    /// <summary>Backup copies of each partition.</summary>
    public int Backups { get; set; } = 1;

    /// <summary>
    ///     Keeps a deserialized copy of every entry in the .NET memory of the (thick, .NET) server nodes, which speeds up
    ///     reads on those nodes but roughly doubles the memory each entry takes. Off by default.
    /// </summary>
    public bool EnablePlatformCache { get; set; }

    /// <summary>
    ///     Puts every cache of the same value type into one cache group (named after the type), so they share partitions
    ///     and storage. When disabled, each cache is its own group.
    /// </summary>
    public bool GroupByValueType { get; set; } = true;
}
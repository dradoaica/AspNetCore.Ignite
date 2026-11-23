using Apache.Ignite.Core.Client;
using AspNetCore.Ignite.Options;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class CacheFactoryTests
{
    [Fact]
    public void CacheDefaults_PlatformCacheIsOptIn()
    {
        Assert.Null(CacheFactory.GetPlatformCacheConfiguration<int, string>(new IgniteCacheOptions()));

        var platformCache = CacheFactory.GetPlatformCacheConfiguration<int, string>(
            new IgniteCacheOptions
            {
                EnablePlatformCache = true,
            }
        );

        Assert.NotNull(platformCache);
        Assert.Equal("System.String", platformCache.ValueTypeName);
    }

    [Fact]
    public void CacheDefaults_GroupByValueType()
    {
        Assert.Equal("System.String", CacheFactory.GetGroupName<string>(new IgniteCacheOptions()));
        Assert.Null(
            CacheFactory.GetGroupName<string>(
                new IgniteCacheOptions
                {
                    GroupByValueType = false,
                }
            )
        );
    }

    [Fact]
    public void GetOrCreateCacheClient_WithPlatformCache_IsRefused()
    {
        // a thin client cannot configure a platform cache: the setting must not be dropped silently
        var client = Fake.Of<IIgniteClient>((member, _) => throw new NotSupportedException(member));

        var exception = Assert.Throws<NotSupportedException>(() => CacheFactory.GetOrCreateCacheClient<int, string>(
                client,
                "c",
                cacheOptions: new IgniteCacheOptions
                {
                    EnablePlatformCache = true,
                }
            )
        );
        Assert.Contains("EnablePlatformCache", exception.Message, StringComparison.Ordinal);
    }
}

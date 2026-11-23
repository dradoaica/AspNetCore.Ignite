using Apache.Ignite.Core.Client;
using AspNetCore.Ignite.Options;
using System.Diagnostics;
using System.Net.Sockets;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class IgniteManagerTests
{
    public static TheoryData<Exception, bool> TransientCases => new()
    {
        {
            new SocketException(), true
        },
        {
            new IOException(), true
        },
        {
            new TimeoutException(), true
        },
        {
            new AggregateException(new SocketException(), new IOException()), true
        },
        {
            new IgniteClientException("x", new AggregateException(new SocketException()), ClientStatusCode.Fail), true
        },
        {
            new IgniteClientException("x", null, ClientStatusCode.InvalidNodeState), true
        },
        {
            new IgniteClientException("invalid cache configuration", null, ClientStatusCode.Fail), false
        },
        {
            new IgniteClientException("x", null, ClientStatusCode.AuthenticationFailed), false
        },
        {
            new IgniteClientException("x", null, ClientStatusCode.SecurityViolation), false
        },
        {
            new AggregateException(new SocketException(), new ArgumentException()), false
        },
        {
            new AggregateException(), false
        },
        {
            new InvalidOperationException(), false
        },
    };

    [Theory]
    [MemberData(nameof(TransientCases))]
    public void IsTransient_ClassifiesExceptions(Exception exception, bool expected) =>
        Assert.Equal(expected, IgniteManager.IsTransient(exception));

    [Fact]
    public async Task ConcurrentCallers_ShareOneConnectionAttempt()
    {
        // nothing listens on port 1: each attempt fails fast, and no retries are configured
        using IgniteManager igniteManager = new(
            new IgniteClientOptions
            {
                Endpoints = ["127.0.0.1:1",],
                ConnectRetryCount = 0,
                SocketTimeout = TimeSpan.FromSeconds(1),
            }
        );

        var attempts = Enumerable.Range(0, 10).Select(_ => igniteManager.GetClientAsync()).ToList();

        _ = Assert.Single(attempts.Distinct());
        await Assert.ThrowsAnyAsync<Exception>(() => attempts[0]);
        // a failed attempt is not cached: the next call starts a new one
        var nextAttempt = igniteManager.GetClientAsync();
        Assert.NotSame(attempts[0], nextAttempt);
        await Assert.ThrowsAnyAsync<Exception>(() => nextAttempt);
    }

    [Fact]
    public void OperationRetries_DoNotRetryAFailedConnection()
    {
        // nothing listens on port 1; the 3 operation retries would wait 2 + 4 + 8 s if they connected again
        using IgniteManager igniteManager = new(
            new IgniteClientOptions
            {
                Endpoints = ["127.0.0.1:1",],
                ConnectRetryCount = 0,
                OperationRetryCount = 3,
                SocketTimeout = TimeSpan.FromSeconds(1),
            }
        );
        var stopwatch = Stopwatch.StartNew();

        Assert.ThrowsAny<Exception>(() => igniteManager.GetOrCreateCacheClient<string, string>("c"));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task GetOrCreateCacheClientAsync_DoesNotRetryAFailedConnection()
    {
        using IgniteManager igniteManager = new(
            new IgniteClientOptions
            {
                Endpoints = ["127.0.0.1:1",],
                ConnectRetryCount = 0,
                OperationRetryCount = 3,
                SocketTimeout = TimeSpan.FromSeconds(1),
            }
        );
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<Exception>(() => igniteManager.GetOrCreateCacheClientAsync<string, string>("c"));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task GetOrCreateCacheClientAsync_StopsWaitingWhenCancelled()
    {
        // the connection retries would take 2 + 4 + 8 + ... s; cancelling ends the wait, not the shared attempt
        using IgniteManager igniteManager = new(
            new IgniteClientOptions
            {
                Endpoints = ["127.0.0.1:1",],
                ConnectRetryCount = 6,
                SocketTimeout = TimeSpan.FromSeconds(1),
            }
        );
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(200));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            igniteManager.GetOrCreateCacheClientAsync<string, string>("c", cancellationToken: cancellation.Token)
        );

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }
}

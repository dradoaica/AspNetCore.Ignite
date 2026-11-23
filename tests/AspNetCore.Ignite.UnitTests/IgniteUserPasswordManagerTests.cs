using Apache.Ignite.Core.Client;
using AspNetCore.IgniteServer;
using Serilog.Core;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class IgniteUserPasswordManagerTests
{
    [Fact]
    public void BuildAlterUserPasswordSql_EscapesQuotes() => Assert.Equal(
        "ALTER USER \"ig\"\"nite\" WITH PASSWORD 'it''s; DROP';",
        IgniteUserPasswordManager.BuildAlterUserPasswordSql("ig\"nite", "it's; DROP")
    );

    [Theory]
    [InlineData(true, "ignite", "old")]
    [InlineData(false, "old", "ignite")]
    public void PasswordCandidates_MostLikelyFirst(bool newCluster, string first, string second)
    {
        IgniteUserPasswordManager manager = new(
            new IgniteServerSettings
            {
                IgniteUserPreviousPassword = "old",
            },
            Logger.None
        );

        Assert.Equal([first, second,], manager.GetCurrentPasswordCandidates("new", newCluster));
    }

    [Fact]
    public void PasswordCandidates_NeverTheConfiguredPassword()
    {
        IgniteUserPasswordManager manager = new(new IgniteServerSettings(), Logger.None);

        Assert.Empty(manager.GetCurrentPasswordCandidates("ignite", true));
    }

    [Fact]
    public void PasswordClient_ConnectsToTheLocalNodeOnly()
    {
        IgniteUserPasswordManager manager = new(
            new IgniteServerSettings
            {
                UseClientSsl = true,
                SslClientCertificatePath = "client.pfx",
            },
            Logger.None
        );

        var cfg = manager.GetClientConfiguration("127.0.0.1:10800", "secret");

        // the other nodes would get the password too, over connections whose certificates are not validated
        Assert.False(cfg.EnableClusterDiscovery);
        Assert.Equal(["127.0.0.1:10800",], cfg.Endpoints);
        Assert.Equal(IgniteUserPasswordManager.DefaultUserName, cfg.UserName);
        Assert.Equal("secret", cfg.Password);
        Assert.Equal("client.pfx", Assert.IsType<SslStreamFactory>(cfg.SslStreamFactory).CertificatePath);
    }

    [Fact]
    public void DdlCacheName_IsStablePerNode()
    {
        var name = IgniteUserPasswordManager.GetDdlCacheName("ignite-0");

        // nodes that apply the password at the same time do not share (and destroy) one cache
        Assert.Equal(name, IgniteUserPasswordManager.GetDdlCacheName("ignite-0"));
        Assert.NotEqual(name, IgniteUserPasswordManager.GetDdlCacheName("ignite-1"));
        Assert.Matches("^alterUserSqlDdlCommand-[0-9a-f]{16}$", name);
    }
}

using Microsoft.Extensions.Configuration;

namespace AspNetCore.IgniteServer;

// SSL between nodes and for thin clients, and certificate rotation.
internal sealed partial class IgniteServerSettings
{
    [ConfigurationKeyName("USE_SSL")]
    public bool UseSsl { get; set; }

    [ConfigurationKeyName("SSL_PROTOCOL")]
    public string SslProtocol { get; set; } = "TLS";

    [ConfigurationKeyName("SSL_KEY_STORE_TYPE")]
    public string SslKeyStoreType { get; set; } = "JKS";

    [ConfigurationKeyName("SSL_TRUST_STORE_TYPE")]
    public string SslTrustStoreType { get; set; } = "JKS";

    [ConfigurationKeyName("SSL_KEY_ALGORITHM")]
    public string SslKeyAlgorithm { get; set; } = "SunX509";

    /// <summary>Thin clients must present a certificate the trust store trusts (USE_CLIENT_SSL).</summary>
    [ConfigurationKeyName("SSL_CLIENT_AUTH")]
    public bool SslClientAuth { get; set; } = true;

    [ConfigurationKeyName("SSL_KEY_STORE_FILE_PATH")]
    public string? SslKeyStoreFilePath { get; set; }

    [ConfigurationKeyName("SSL_KEY_STORE_PASSWORD")]
    public string? SslKeyStorePassword { get; set; }

    [ConfigurationKeyName("SSL_TRUST_STORE_FILE_PATH")]
    public string? SslTrustStoreFilePath { get; set; }

    [ConfigurationKeyName("SSL_TRUST_STORE_PASSWORD")]
    public string? SslTrustStorePassword { get; set; }

    [ConfigurationKeyName("USE_CLIENT_SSL")]
    public bool UseClientSsl { get; set; }

    [ConfigurationKeyName("SSL_CLIENT_CERTIFICATE_PATH")]
    public string? SslClientCertificatePath { get; set; }

    [ConfigurationKeyName("SSL_CLIENT_CERTIFICATE_PASSWORD")]
    public string? SslClientCertificatePassword { get; set; }

    [ConfigurationKeyName("RESTART_ON_SSL_FILE_CHANGE")]
    public bool RestartOnSslFileChange { get; set; } = true;

    [ConfigurationKeyName("SSL_RESTART_MAX_JITTER_IN_SECONDS")]
    public int SslRestartMaxJitterInSeconds { get; set; } = 60;
}

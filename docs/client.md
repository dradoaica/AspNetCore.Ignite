# Client library (`AspNetCore.Ignite`)

The package targets .NET 10 (`net10.0`), like the server.

```csharp
services.AddIgnite();                                         // the ASPNETCORE_IGNITE_* settings below
services.AddIgnite(configuration.GetSection("Ignite"));       // or a section bound to IgniteClientOptions
services.AddIgnite(options => options.Cache.Backups = 2);     // and/or code
services.AddHealthChecks().AddIgnite();                       // healthy when the cluster is reachable and active
```

`IIgniteManager` connects on first use (with retries), not when the service is resolved; concurrent callers share one
connection attempt (`GetClientAsync` waits for it without blocking a thread, which the health check relies on). It
creates and destroys caches and exposes the thin client (`IIgniteManager.Client`) for SQL, compute, transactions, etc.
The client logs through `Microsoft.Extensions.Logging` (category `Apache.Ignite`). For LINQ queries, add the
`Apache.Ignite.Linq` package.

| Setting | `IgniteClientOptions` | Default | Description |
|---|---|---|---|
| `ASPNETCORE_IGNITE_ENDPOINT` | `Endpoints` | `127.0.0.1` | Thin client endpoints (comma-separated). |
| `ASPNETCORE_IGNITE_USER_NAME` / `ASPNETCORE_IGNITE_PASSWORD` | `UserName` / `Password` | _(empty)_ | Credentials. |
| `ASPNETCORE_IGNITE_USE_SSL` | `UseSsl` | `false` | Enables SSL (the OS chooses the protocol, TLS 1.3 included). |
| `ASPNETCORE_IGNITE_SSL_CERTIFICATE_PATH` / `ASPNETCORE_IGNITE_SSL_CERTIFICATE_PASSWORD` | `SslCertificatePath` / `SslCertificatePassword` | _(empty)_ | Client certificate. |
| `ASPNETCORE_IGNITE_SSL_SKIP_SERVER_CERTIFICATE_VALIDATION` | `SslSkipServerCertificateValidation` | `false` | Skips server certificate validation (insecure, testing only). |
| `ASPNETCORE_IGNITE_SSL_CHECK_CERTIFICATE_REVOCATION` | `SslCheckCertificateRevocation` | `true` | Checks the revocation of the server certificate. |
| | `RetryLimit`, `SocketTimeout` | `5`, `30 s` | Thin client retries of read operations and socket timeout. |
| | `ConnectRetryCount`, `OperationRetryCount` | `6`, `3` | Retries (exponential backoff) of connecting and of transient operation failures. |
| | `Cache.Backups` | `1` | Backups of created caches. |
| | `Cache.EnablePlatformCache` | `false` | Keeps a deserialized copy of every entry in the .NET memory of the (.NET) server nodes: faster reads there, but roughly double the memory per entry. |
| | `Cache.GroupByValueType` | `true` | Puts the caches of the same value type into one cache group (shared partitions and storage). |

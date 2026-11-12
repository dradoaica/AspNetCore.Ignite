# Client library (`AspNetCore.Ignite`)

The package targets .NET 10 (`net10.0`), like the server. Releases publish it to GitHub Packages, which needs a token
to install even a public package (a classic personal access token with `read:packages`), and to nuget.org once the
release workflow has its key (`NUGET_API_KEY`):

```
dotnet nuget add source https://nuget.pkg.github.com/dradoaica/index.json --name dradoaica \
  --username <user> --password <token> --store-password-in-clear-text
dotnet add package AspNetCore.Ignite
```

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

The options (`IgniteClientOptions`), each with the flat setting (e.g., an environment variable) that also sets it, where
there is one:

- `Endpoints` (setting `ASPNETCORE_IGNITE_ENDPOINT`, default `127.0.0.1`): Thin client endpoints (comma-separated).
- `UserName` / `Password` (setting `ASPNETCORE_IGNITE_USER_NAME` / `ASPNETCORE_IGNITE_PASSWORD`, default _(empty)_):
  Credentials.
- `UseSsl` (setting `ASPNETCORE_IGNITE_USE_SSL`, default `false`): Enables SSL (the OS chooses the protocol, TLS 1.3
  included).
- `SslCertificatePath` / `SslCertificatePassword` (setting `ASPNETCORE_IGNITE_SSL_CERTIFICATE_PATH` /
  `ASPNETCORE_IGNITE_SSL_CERTIFICATE_PASSWORD`, default _(empty)_): Client certificate.
- `SslSkipServerCertificateValidation` (setting `ASPNETCORE_IGNITE_SSL_SKIP_SERVER_CERTIFICATE_VALIDATION`, default
  `false`): Skips server certificate validation (insecure, testing only).
- `SslCheckCertificateRevocation` (setting `ASPNETCORE_IGNITE_SSL_CHECK_CERTIFICATE_REVOCATION`, default `true`): Checks
  the revocation of the server certificate.
- `EnableClusterDiscovery` (setting `ASPNETCORE_IGNITE_ENABLE_CLUSTER_DISCOVERY`, default `true`): Connects to the other
  server nodes too (partition awareness): turn it off when their addresses are not reachable.
- `RetryLimit`, `SocketTimeout` (default `5`, `30 s`): Thin client retries of read operations and socket timeout.
- `ConnectRetryCount`, `OperationRetryCount` (default `6`, `3`): Retries (exponential backoff) of connecting and of
  transient failures (no connection, cluster not active).
- `Cache.Backups` (default `1`): Backups of created caches.
- `Cache.EnablePlatformCache` (default `false`): Keeps a deserialized copy of every entry in the .NET memory of the
  (.NET) server nodes: faster reads there, but roughly double the memory per entry.
- `Cache.GroupByValueType` (default `true`): Puts the caches of the same value type into one cache group (shared
  partitions and storage).

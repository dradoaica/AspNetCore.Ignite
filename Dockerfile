# The build stage runs on the build platform (the output is framework-dependent and portable), the other stages on the
# target platform (linux/amd64, linux/arm64). BuildKit sets BUILDPLATFORM; the legacy builder needs
# --build-arg BUILDPLATFORM=linux/amd64 (or linux/arm64).
ARG BUILDPLATFORM
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY ["NuGet.Config", "global.json", "Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["src/Directory.Build.props", "src/"]
COPY ["src/AspNetCore.Ignite/AspNetCore.Ignite.csproj", "src/AspNetCore.Ignite/"]
COPY ["src/AspNetCore.IgniteServer/AspNetCore.IgniteServer.csproj", "src/AspNetCore.IgniteServer/JavaLibs.props", "src/AspNetCore.IgniteServer/"]
RUN dotnet restore "src/AspNetCore.IgniteServer/AspNetCore.IgniteServer.csproj"
# the Java modules of JavaLibs.props (SHA-256 verified), in their own layer: downloaded again only when it changes
RUN dotnet msbuild "src/AspNetCore.IgniteServer/AspNetCore.IgniteServer.csproj" -t:RestoreJavaLibs -p:Configuration=Release -nologo -v:minimal
COPY . .
# the release workflow passes the tag (e.g., 2.19.0); otherwise the version of src/Directory.Build.props
ARG VERSION=
RUN dotnet publish "src/AspNetCore.IgniteServer/AspNetCore.IgniteServer.csproj" -c Release -o /app/publish --no-restore \
    ${VERSION:+-p:Version=$VERSION}

# A Java 17 runtime with just the modules Ignite needs (jlink): the standard java.* modules (java.se) and the JDK
# modules for Unsafe (jdk.unsupported), management/metrics, TLS (jdk.crypto.ec) and DNS (Kubernetes discovery)
FROM eclipse-temurin:17-jdk-noble AS jre
RUN jlink \
        --add-modules java.se,jdk.unsupported,jdk.management,jdk.management.agent,jdk.internal.jvmstat,jdk.crypto.ec,jdk.crypto.cryptoki,jdk.naming.dns,jdk.zipfs,jdk.httpserver,jdk.jfr \
        --strip-debug --no-man-pages --no-header-files --compress=2 \
        --output /jre

# Runtime image: ASP.NET Core runtime (Kestrel for the management endpoints) + the Java runtime above; the Ignite jars
# come with the application (/app/libs)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime

COPY --from=jre /jre /usr/lib/jvm/java-17
ENV JAVA_HOME=/usr/lib/jvm/java-17
# fix for 'Stack smashing detected: dotnet terminated' (read by the runtime at startup, so it must be set here;
# the legacy COMPlus_ name is the one Ignite.NET checks)
ENV COMPlus_EnableAlternateStackCheck=1
# logs as JSON lines (with the rendered message) for log collectors
ENV Serilog__WriteTo__0__Args__formatter="Serilog.Formatting.Compact.RenderedCompactJsonFormatter, Serilog.Formatting.Compact"

WORKDIR /app
COPY --from=build /app/publish .

# work/ (persistence, WAL) is pre-created so that volumes mounted there are writable by the non-root user
RUN mkdir -p /app/work && chown -R 1001:0 /app && chmod -R g=u /app

USER 1001

# outside Kubernetes (which uses its own probes): /health/liveness on the management port, with bash only (no curl here)
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/${MANAGEMENT_PORT:-9464} && printf 'GET /health/liveness HTTP/1.0\\r\\n\\r\\n' >&3 && head -n 1 <&3 | grep -q ' 200 '"]

ENTRYPOINT ["dotnet", "AspNetCore.IgniteServer.dll"]

# thin clients, communication, discovery, management (/health/*, /metrics); REST (8080) and the TCP binary protocol
# (11211) only with REST_ENABLED
EXPOSE 10800 47100 47500 9464

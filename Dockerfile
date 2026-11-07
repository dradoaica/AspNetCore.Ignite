# The build stage runs on the build platform (the output is framework-dependent and portable), the runtime stage on
# the target platform (linux/amd64, linux/arm64). BuildKit sets BUILDPLATFORM; the legacy builder needs
# --build-arg BUILDPLATFORM=linux/amd64 (or linux/arm64).
ARG BUILDPLATFORM
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY ["NuGet.Config", "global.json", "Directory.Packages.props", "./"]
COPY ["src/Directory.Build.props", "src/"]
COPY ["src/AspNetCore.Ignite/AspNetCore.Ignite.csproj", "src/AspNetCore.Ignite/"]
COPY ["src/AspNetCore.IgniteServer/AspNetCore.IgniteServer.csproj", "src/AspNetCore.IgniteServer/JavaLibs.props", "src/AspNetCore.IgniteServer/"]
RUN dotnet restore "src/AspNetCore.IgniteServer/AspNetCore.IgniteServer.csproj"
COPY . .
# also downloads the Java modules of JavaLibs.props (SHA-256 verified)
RUN dotnet publish "src/AspNetCore.IgniteServer/AspNetCore.IgniteServer.csproj" -c Release -o /app/publish --no-restore

# Build runtime image: .NET runtime + Java 17 (signed Ubuntu packages); the Ignite jars come with the application
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble AS runtime

RUN apt-get update \
    && apt-get install -y --no-install-recommends openjdk-17-jre-headless \
    && rm -rf /var/lib/apt/lists/* \
    # an architecture-independent JAVA_HOME (java-17-openjdk-amd64, java-17-openjdk-arm64, ...)
    && ln -s "$(dirname "$(dirname "$(readlink -f "$(command -v java)")")")" /usr/lib/jvm/java-17

ENV JAVA_HOME=/usr/lib/jvm/java-17
# fix for 'Stack smashing detected: dotnet terminated' (read by the runtime at startup, so it must be set here;
# the legacy COMPlus_ name is the one Ignite.NET checks)
ENV COMPlus_EnableAlternateStackCheck=1

WORKDIR /app
COPY --from=build /app/publish .

# work/ (persistence, WAL) is pre-created so that volumes mounted there are writable by the non-root user
RUN mkdir -p /app/work && chown -R 1001:0 /app && chmod -R g=u /app

USER 1001

ENTRYPOINT ["dotnet", "AspNetCore.IgniteServer.dll"]

EXPOSE 11211 47100 47500 49112 10800 8080 10900

# Build with docker build -t aspnetcore.ignite-server .
FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["NuGet.Config", "."]
COPY ["src/AspNetCore.IgniteServer/AspNetCore.IgniteServer.csproj", "src/AspNetCore.IgniteServer/"]
COPY ["src/AspNetCore.Ignite/AspNetCore.Ignite.csproj", "src/AspNetCore.Ignite/"]
RUN dotnet restore "src/AspNetCore.IgniteServer/AspNetCore.IgniteServer.csproj"
COPY . .
WORKDIR "/src/src/AspNetCore.IgniteServer"
RUN dotnet build "AspNetCore.IgniteServer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "AspNetCore.IgniteServer.csproj" -c Release -o /app/publish --no-restore

# Build runtime image
FROM openjdk:11

RUN export DEBIAN_FRONTEND=noninteractive \
    apt-get update \
    # Install prerequisites
    && apt-get install -y --no-install-recommends \
       wget \
       ca-certificates \
    \
    # Install Microsoft package feed
    && wget -q https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb \
    && rm packages-microsoft-prod.deb \
    \
    # Install .NET
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        dotnet-runtime-7.0 \
    \
    # Cleanup
    && rm -rf /var/lib/apt/lists/*

# Install IGNITE
ENV IGNITE_VERSION 2.15.0

# Ignite home
ENV IGNITE_HOME /opt/ignite/apache-ignite-${IGNITE_VERSION}-bin

# Do not rely on anything provided by base image(s), but be explicit, if they are installed already it is noop then
RUN apt-get update && apt-get install -y --no-install-recommends \
        unzip \
        curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/ignite

RUN curl https://dist.apache.org/repos/dist/release/ignite/${IGNITE_VERSION}/apache-ignite-${IGNITE_VERSION}-bin.zip -o ignite.zip \
    && unzip ignite.zip \
    && rm ignite.zip

RUN mv apache-ignite-${IGNITE_VERSION}-bin/libs/optional/ignite-rest-http apache-ignite-${IGNITE_VERSION}-bin/libs/ignite-rest-http

RUN mv apache-ignite-${IGNITE_VERSION}-bin/libs/optional/ignite-kubernetes apache-ignite-${IGNITE_VERSION}-bin/libs/ignite-kubernetes

RUN mv apache-ignite-${IGNITE_VERSION}-bin/libs/optional/ignite-opencensus apache-ignite-${IGNITE_VERSION}-bin/libs/ignite-opencensus
RUN curl https://repo1.maven.org/maven2/io/prometheus/simpleclient_common/0.6.0/simpleclient_common-0.6.0.jar -o apache-ignite-${IGNITE_VERSION}-bin/libs/ignite-opencensus/simpleclient_common-0.6.0.jar
RUN curl https://repo1.maven.org/maven2/io/prometheus/simpleclient/0.6.0/simpleclient-0.6.0.jar -o apache-ignite-${IGNITE_VERSION}-bin/libs/ignite-opencensus/simpleclient-0.6.0.jar
RUN curl https://repo1.maven.org/maven2/io/opencensus/opencensus-exporter-metrics-util/0.20.0/opencensus-exporter-metrics-util-0.20.0.jar -o apache-ignite-${IGNITE_VERSION}-bin/libs/ignite-opencensus/opencensus-exporter-metrics-util-0.20.0.jar
RUN curl https://repo1.maven.org/maven2/io/opencensus/opencensus-exporter-stats-prometheus/0.22.0/opencensus-exporter-stats-prometheus-0.22.0.jar -o apache-ignite-${IGNITE_VERSION}-bin/libs/ignite-opencensus/opencensus-exporter-stats-prometheus-0.22.0.jar
RUN curl https://repo1.maven.org/maven2/io/prometheus/simpleclient_httpserver/0.6.0/simpleclient_httpserver-0.6.0.jar -o apache-ignite-${IGNITE_VERSION}-bin/libs/ignite-opencensus/simpleclient_httpserver-0.6.0.jar

RUN rm -r apache-ignite-${IGNITE_VERSION}-bin/benchmarks
RUN rm -r apache-ignite-${IGNITE_VERSION}-bin/examples
RUN rm -r apache-ignite-${IGNITE_VERSION}-bin/libs/optional
RUN rm -r apache-ignite-${IGNITE_VERSION}-bin/platforms

WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "AspNetCore.IgniteServer.dll"]

EXPOSE 10800 47100 47500 49112 8080 3000 9000

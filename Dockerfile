FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src
COPY ["NuGet.Config", "."]
COPY ["global.json", "."]
COPY ["src/AspNetCore.Ignite/AspNetCore.Ignite.csproj", "src/AspNetCore.Ignite/"]
COPY ["src/AspNetCore.IgniteServer/AspNetCore.IgniteServer.csproj", "src/AspNetCore.IgniteServer/"]
RUN dotnet restore "src/AspNetCore.IgniteServer/AspNetCore.IgniteServer.csproj"
COPY . .
WORKDIR "/src/src/AspNetCore.IgniteServer"

FROM build AS publish
RUN dotnet publish "AspNetCore.IgniteServer.csproj" -c Release -o /app/publish --no-restore

# Build runtime image
FROM eclipse-temurin:17-jre-noble AS runtime

ARG DOTNET_CHANNEL=10.0

RUN apt-get update \
    # Install prerequisites
    && apt-get install -y --no-install-recommends \
       wget \
       ca-certificates \
       # .NET globalization support
       libicu74 \
    \
    # Install .NET (pinned to the channel the server targets)
    && wget https://dot.net/v1/dotnet-install.sh -O dotnet-install.sh \
    && chmod +x dotnet-install.sh \
    && ./dotnet-install.sh --channel ${DOTNET_CHANNEL} --runtime dotnet --install-dir /usr/local/dotnet \
    \
    # Cleanup \
    && rm ./dotnet-install.sh \
    && rm -rf /var/lib/apt/lists/*

# .NET root
ENV DOTNET_ROOT=/usr/local/dotnet
ENV PATH=$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools

# Install IGNITE (archive.apache.org keeps every release, dlcdn only the current ones)
ARG IGNITE_VERSION=2.18.0
ARG IGNITE_SHA512=f1eba2c899e3aa4a3f50f9db9da1342ce6a0009de8e1fb2c9e751cb84346a764fe2385e2753eac1a90e500ff1a7fd531ea1b0251072ba7b57373b63b829a68bd

# Ignite home
ARG IGNITE_HOME=/opt/ignite/apache-ignite-${IGNITE_VERSION}-bin
ENV IGNITE_HOME=$IGNITE_HOME

# Do not rely on anything provided by base image(s), but be explicit, if they are installed already it is noop then
RUN apt-get update && apt-get install -y --no-install-recommends \
        unzip \
        curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /opt/ignite

RUN curl -fsSL https://archive.apache.org/dist/ignite/${IGNITE_VERSION}/apache-ignite-${IGNITE_VERSION}-bin.zip -o ignite.zip \
    && echo "${IGNITE_SHA512}  ignite.zip" | sha512sum -c - \
    && unzip ignite.zip \
    && rm ignite.zip

RUN mv apache-ignite-${IGNITE_VERSION}-bin/libs/optional/ignite-rest-http apache-ignite-${IGNITE_VERSION}-bin/libs/ignite-rest-http
RUN mv apache-ignite-${IGNITE_VERSION}-bin/libs/optional/ignite-json apache-ignite-${IGNITE_VERSION}-bin/libs/ignite-json
RUN mv apache-ignite-${IGNITE_VERSION}-bin/libs/optional/ignite-kubernetes apache-ignite-${IGNITE_VERSION}-bin/libs/ignite-kubernetes
RUN mv apache-ignite-${IGNITE_VERSION}-bin/libs/optional/ignite-opencensus apache-ignite-${IGNITE_VERSION}-bin/libs/ignite-opencensus

RUN rm -r apache-ignite-${IGNITE_VERSION}-bin/benchmarks
RUN rm -r apache-ignite-${IGNITE_VERSION}-bin/examples
RUN rm -r apache-ignite-${IGNITE_VERSION}-bin/libs/optional
RUN rm -r apache-ignite-${IGNITE_VERSION}-bin/platforms

WORKDIR /app
COPY --from=publish /app/publish .

RUN chgrp -R 0 /app && chmod -R g=u /app
RUN chown -R 1001:0 /app && chmod -R 775 /app

USER 1001

ENTRYPOINT ["dotnet", "AspNetCore.IgniteServer.dll"]

EXPOSE 11211 47100 47500 49112 10800 8080 10900

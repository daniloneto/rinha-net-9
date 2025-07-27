FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

# Install native compilation prerequisites for AOT
RUN apt-get update && apt-get install -y \
    clang \
    zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY Gateway.csproj .
RUN dotnet restore Gateway.csproj

COPY . .
RUN dotnet publish Gateway.csproj -c Release -o /app/publish \
    --self-contained true \
    --runtime linux-x64 \
    /p:PublishAot=true \
    /p:PublishTrimmed=true \
    /p:StripSymbols=true

FROM mcr.microsoft.com/dotnet/runtime-deps:9.0 AS runtime

RUN apt-get update && apt-get install -y \
    curl \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && update-ca-certificates


WORKDIR /app
COPY --from=build /app/publish .

# Ensure the app user can create sockets in /tmp
RUN mkdir -p /tmp && chmod 777 /tmp

# Don't switch to non-root user for socket creation permissions
# USER 1000:1000

ENTRYPOINT ["./gateway"]

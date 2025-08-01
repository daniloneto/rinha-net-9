FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build

# Install native compilation prerequisites for AOT
RUN apt-get update && apt-get install -y \
    clang \
    zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY Database.csproj .
RUN dotnet restore Database.csproj

COPY . .
RUN dotnet publish Database.csproj -c Release -o /app/publish \
    --self-contained true \
    --runtime linux-x64 \
    /p:PublishAot=true \
    /p:PublishTrimmed=true \
    /p:StripSymbols=true \
    /p:PublishReadyToRun=true \
    /p:PublishProfileOptimization=true

FROM mcr.microsoft.com/dotnet/runtime-deps:9.0 AS runtime

RUN apt-get update && apt-get install -y \
    ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && update-ca-certificates

WORKDIR /app
COPY --from=build /app/publish .

# Ensure socket directory exists
RUN mkdir -p /sockets && chmod 777 /sockets

ENTRYPOINT ["./database"]

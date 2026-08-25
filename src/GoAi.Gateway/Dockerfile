FROM mcr.microsoft.com/dotnet/sdk@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/GoAi.Contracts/GoAi.Contracts.csproj src/GoAi.Contracts/
COPY src/GoAi.Server.Core/GoAi.Server.Core.csproj src/GoAi.Server.Core/
COPY src/GoAi.Gateway/GoAi.Gateway.csproj src/GoAi.Gateway/
RUN dotnet restore src/GoAi.Gateway/GoAi.Gateway.csproj --runtime linux-x64
COPY src/GoAi.Contracts src/GoAi.Contracts
COPY src/GoAi.Server.Core src/GoAi.Server.Core
COPY src/GoAi.Gateway src/GoAi.Gateway
RUN dotnet publish src/GoAi.Gateway/GoAi.Gateway.csproj \
    --configuration Release \
    --runtime linux-x64 \
    --no-restore \
    --output /out

FROM mcr.microsoft.com/dotnet/aspnet@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94
RUN mkdir -p /app /data/database /data/uploads /data/artifacts/worker /data/logs
WORKDIR /app
COPY --from=build /out ./
ENV ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    GO_AI_DATA_DIRECTORY=/data \
    GO_AI_GATEWAY_PORT=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "GoAi.Gateway.dll"]

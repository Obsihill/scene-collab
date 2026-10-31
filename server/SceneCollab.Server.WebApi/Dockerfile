# Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY contracts/SceneCollab.Contracts/SceneCollab.Contracts.csproj contracts/SceneCollab.Contracts/
COPY server/SceneCollab.Domain/SceneCollab.Domain.csproj server/SceneCollab.Domain/
COPY server/SceneCollab.Server.WebApi/SceneCollab.Server.WebApi.csproj server/SceneCollab.Server.WebApi/
RUN dotnet restore server/SceneCollab.Server.WebApi/SceneCollab.Server.WebApi.csproj

COPY unity/com.obsihill.scenecollab/Contracts/ unity/com.obsihill.scenecollab/Contracts/
COPY server/ server/
RUN dotnet publish server/SceneCollab.Server.WebApi/SceneCollab.Server.WebApi.csproj \
    -c Release -o /app /p:UseAppHost=false

# Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./

ENV SCENECOLLAB_BIND=0.0.0.0 \
    SCENECOLLAB_PORT=8080 \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

EXPOSE 8080

# Non-root: the image default user already exists in the aspnet base image.
USER $APP_UID

ENTRYPOINT ["dotnet", "SceneCollab.Server.WebApi.dll"]

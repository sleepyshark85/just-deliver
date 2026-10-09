# Build from the repository root (the shared Directory.*.props live there):
#   docker build --platform linux/amd64 -f samples/sample-app/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY samples/sample-app/ samples/sample-app/
RUN dotnet publish samples/sample-app/sample-app.csproj -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /out .
# The port the workload definition declares (container.ports).
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# The aspnet image ships a non-root "app" user.
USER $APP_UID
ENTRYPOINT ["dotnet", "sample-app.dll"]

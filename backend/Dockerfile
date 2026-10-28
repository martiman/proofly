# Build context: backend/   (docker build -f backend/Dockerfile --target api|worker backend)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/ src/
RUN dotnet publish src/Proofly.Api/Proofly.Api.csproj -c Release -o /out/api \
 && dotnet publish src/Proofly.Worker/Proofly.Worker.csproj -c Release -o /out/worker

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS api
WORKDIR /app
COPY --from=build /out/api ./
# base image provides the non-root user "app" (uid 1654)
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=5 \
  CMD wget -q -O /dev/null http://127.0.0.1:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "Proofly.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine AS worker
WORKDIR /app
COPY --from=build /out/worker ./
USER app
# The worker exposes no HTTP endpoint; no /healthz. Compose supervises it by restart policy.
ENTRYPOINT ["dotnet", "Proofly.Worker.dll"]

# Multi-stage build for the API (with the Angular UI) and the Worker.
#   docker build -f deploy/Dockerfile --target api -t followup-officer-api .
#   docker build -f deploy/Dockerfile --target worker -t followup-officer-worker .

FROM node:24-alpine AS ui
WORKDIR /src/client
COPY client/package*.json ./
RUN npm ci
COPY client/ ./
RUN npm run build -- --output-path=/ui

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/FollowUp.Api -c Release -o /out/api \
 && dotnet publish src/FollowUp.Worker -c Release -o /out/worker

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS api
WORKDIR /app
COPY --from=build /out/api .
COPY --from=ui /ui/browser ./wwwroot
ENV ASPNETCORE_URLS=http://+:5860 DOTNET_ENVIRONMENT=Production
EXPOSE 5860
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s CMD wget -qO- http://localhost:5860/api/health || exit 1
ENTRYPOINT ["dotnet", "FollowUp.Api.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS worker
WORKDIR /app
COPY --from=build /out/worker .
ENV DOTNET_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "FollowUp.Worker.dll"]

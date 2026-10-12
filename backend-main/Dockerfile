# Build context: repository root (docker compose: context: ., dockerfile: backend-main/Dockerfile)

# --- Frontend build ---
FROM node:24 AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci
COPY frontend/ ./
ARG VITE_ML_API_URL
ENV VITE_ML_API_URL=$VITE_ML_API_URL
RUN npm run build

# --- Backend build ---
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend-main/src/SalonCrm.Api/SalonCrm.Api.csproj backend-main/src/SalonCrm.Api/
RUN dotnet restore backend-main/src/SalonCrm.Api/SalonCrm.Api.csproj
COPY backend-main/src/ backend-main/src/
RUN dotnet publish backend-main/src/SalonCrm.Api/SalonCrm.Api.csproj -c Release -o /app/publish --no-restore

# --- Runtime (standard image: tzdata needed for Europe/Bratislava) ---
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl tzdata \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish ./
COPY --from=frontend /src/frontend/dist ./wwwroot
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "SalonCrm.Api.dll"]

# Use the official ASP.NET Core runtime as base image (Ubuntu Noble 24.04)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 6767

# Add Jellyfin repo for jellyfin-ffmpeg (custom FFmpeg with full VAAPI/QSV support)
RUN apt-get update && apt-get install -y curl gnupg && \
    mkdir -p /etc/apt/keyrings && \
    curl -fsSL https://repo.jellyfin.org/jellyfin_team.gpg.key | gpg --dearmor -o /etc/apt/keyrings/jellyfin.gpg && \
    printf "Types: deb\nURIs: https://repo.jellyfin.org/ubuntu\nSuites: noble\nComponents: main\nArchitectures: amd64\nSigned-By: /etc/apt/keyrings/jellyfin.gpg\n" \
    > /etc/apt/sources.list.d/jellyfin.sources

# Install jellyfin-ffmpeg7 (latest available for Noble), Intel VAAPI drivers, supporting
# libraries, and the runtime deps that SkiaSharp needs for its native bitmap decoder
# (libfontconfig1, libfreetype6). The TesseractOCR NuGet only ships native libs for
# Windows; on Linux its LibraryLoader dlopens the system libtesseract.so.5 /
# libleptonica.so.6, so libtesseract5 + libleptonica must be present.
RUN apt-get update && apt-get install -y \
    coreutils \
    jellyfin-ffmpeg7=7.1.3-5* \
    intel-media-va-driver-non-free \
    i965-va-driver \
    mesa-va-drivers \
    va-driver-all \
    vainfo \
    libva-drm2 \
    libfontconfig1 \
    libfreetype6 \
    libtesseract5 \
    libleptonica \
    && rm -rf /var/lib/apt/lists/*

# Verify FFmpeg has VAAPI support
RUN /usr/lib/jellyfin-ffmpeg/ffmpeg -hwaccels 2>/dev/null | grep -q vaapi && echo "VAAPI support confirmed" || echo "WARNING: No VAAPI support"
RUN /usr/lib/jellyfin-ffmpeg/ffmpeg -encoders 2>/dev/null | grep vaapi && echo "VAAPI encoders confirmed"

# Create working directories
RUN mkdir -p /app/work/uploads /app/work/output /app/work/logs

# Use the SDK image to build the application
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Snacks/Snacks.csproj", "Snacks/"]
RUN dotnet restore "./Snacks/Snacks.csproj"
COPY . .
WORKDIR "/src/Snacks"
RUN dotnet build "./Snacks.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Snacks.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# Final stage/image
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Set environment variables — use jellyfin-ffmpeg paths
ENV ASPNETCORE_URLS=http://+:6767
ENV SNACKS_WORK_DIR=/app/work
ENV FFMPEG_PATH=/usr/lib/jellyfin-ffmpeg/ffmpeg
ENV FFPROBE_PATH=/usr/lib/jellyfin-ffmpeg/ffprobe
# VAAPI driver — auto-detection will try both iHD and i965 at runtime
# Set via deploy-compose.yml or leave unset for auto-detection

# Run as root to ensure /dev/dri access on NAS devices (QNAP, Synology)
RUN mkdir -p /app/work/uploads /app/work/output /app/work/logs

# Health check
HEALTHCHECK --interval=30s --timeout=10s --start-period=40s --retries=3 \
    CMD curl -f http://localhost:6767/api/health || exit 1

ENTRYPOINT ["dotnet", "Snacks.dll"]

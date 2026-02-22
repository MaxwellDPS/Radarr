# -- Backend build --
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS backend-build

ARG TARGETARCH

WORKDIR /build

# --- Layer 1: NuGet restore (cached unless project files change) ---
COPY global.json .editorconfig ./
COPY src/Directory.Build.props src/Directory.Build.targets src/NuGet.config src/
COPY src/Targets/ src/Targets/

# Copy csproj files preserving directory structure for restore caching.
# Only these files (plus build infra above) affect NuGet restore; source code
# changes will NOT invalidate this layer.
COPY src/NzbDrone/Radarr.csproj src/NzbDrone/
COPY src/NzbDrone.Common/Radarr.Common.csproj src/NzbDrone.Common/
COPY src/NzbDrone.Console/Radarr.Console.csproj src/NzbDrone.Console/
COPY src/NzbDrone.Core/Radarr.Core.csproj src/NzbDrone.Core/
COPY src/NzbDrone.Host/Radarr.Host.csproj src/NzbDrone.Host/
COPY src/NzbDrone.Mono/Radarr.Mono.csproj src/NzbDrone.Mono/
COPY src/NzbDrone.SignalR/Radarr.SignalR.csproj src/NzbDrone.SignalR/
COPY src/NzbDrone.Update/Radarr.Update.csproj src/NzbDrone.Update/
COPY src/NzbDrone.Windows/Radarr.Windows.csproj src/NzbDrone.Windows/
COPY src/Radarr.Api.V3/Radarr.Api.V3.csproj src/Radarr.Api.V3/
COPY src/Radarr.Http/Radarr.Http.csproj src/Radarr.Http/
COPY src/ServiceHelpers/ServiceInstall/ServiceInstall.csproj src/ServiceHelpers/ServiceInstall/
COPY src/ServiceHelpers/ServiceUninstall/ServiceUninstall.csproj src/ServiceHelpers/ServiceUninstall/

# Compute runtime identifier from Docker platform
RUN case "$TARGETARCH" in \
      amd64) RID=linux-musl-x64 ;; \
      arm64) RID=linux-musl-arm64 ;; \
      *) echo "Unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac && \
    echo "$RID" > /tmp/rid && \
    dotnet restore src/NzbDrone.Console/Radarr.Console.csproj -r "$RID"

# --- Layer 2: Build (rebuilt on code changes) ---
COPY Logo/ Logo/
COPY src/ src/

RUN dotnet publish src/NzbDrone.Console/Radarr.Console.csproj \
    -c Release \
    -f net8.0 \
    -r "$(cat /tmp/rid)" \
    -o /app \
    --self-contained \
    --no-restore \
    -p:TreatWarningsAsErrors=false

# -- Frontend build --
# Use BUILDPLATFORM so node/yarn run natively (no QEMU emulation)
FROM --platform=$BUILDPLATFORM node:20-slim AS frontend-build
WORKDIR /build
COPY package.json yarn.lock .yarnrc ./
RUN yarn install --frozen-lockfile --network-timeout 600000
COPY frontend/ frontend/
COPY tsconfig.json ./
RUN yarn build --env production

# -- Runtime image --
# Pin to a specific version tag to avoid supply chain risk from :latest
FROM lscr.io/linuxserver/radarr:5.21.1

# Preserve ffprobe from the base image before replacing binaries
RUN cp /app/radarr/bin/ffprobe /tmp/ffprobe

# Replace Radarr binaries with our build
RUN rm -rf /app/radarr/bin
COPY --from=backend-build /app /app/radarr/bin
COPY --from=frontend-build /build/_output/UI /app/radarr/bin/UI
RUN cp /tmp/ffprobe /app/radarr/bin/ffprobe && chmod +x /app/radarr/bin/ffprobe

# Update package info to reflect custom build
RUN echo -e "UpdateMethod=docker\nBranch=develop\nPackageVersion=custom\nPackageAuthor=custom-build" > /app/radarr/package_info

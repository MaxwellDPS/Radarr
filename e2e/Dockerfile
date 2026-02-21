# E2E test Dockerfile — builds for the native platform architecture
# (avoids slow qemu emulation on macOS ARM)

ARG DOTNET_RID=linux-musl-x64

# -- Backend build --
FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS backend-build
ARG DOTNET_RID
WORKDIR /build
COPY global.json .editorconfig ./
COPY Logo/ Logo/
COPY src/ src/
RUN dotnet publish src/NzbDrone.Console/Radarr.Console.csproj \
    -c Release \
    -f net8.0 \
    -r ${DOTNET_RID} \
    -o /app \
    --self-contained \
    -p:TreatWarningsAsErrors=false

# -- Frontend build --
FROM node:20-slim AS frontend-build
WORKDIR /build
COPY package.json yarn.lock .yarnrc ./
RUN yarn install --frozen-lockfile
COPY frontend/ frontend/
COPY tsconfig.json ./
RUN yarn build --env production

# -- Runtime image --
FROM alpine:3.21 AS runtime

# Install runtime dependencies (matches linuxserver/radarr base)
RUN apk add --no-cache \
    icu-libs \
    libintl \
    sqlite-libs \
    curl \
    wget \
    ca-certificates

# Create app user
RUN addgroup -g 1000 radarr && adduser -u 1000 -G radarr -D radarr

# Create required directories
RUN mkdir -p /app/radarr/bin /config/radarr /downloads /movies && \
    chown -R radarr:radarr /config /downloads /movies

# Copy built application
COPY --from=backend-build /app /app/radarr/bin
COPY --from=frontend-build /build/_output/UI /app/radarr/bin/UI

# Write package info
RUN echo -e "UpdateMethod=docker\nBranch=develop\nPackageVersion=custom\nPackageAuthor=custom-build" > /app/radarr/package_info

EXPOSE 7878
VOLUME ["/config", "/downloads", "/movies"]

# Healthcheck
HEALTHCHECK --interval=5s --timeout=5s --retries=30 --start-period=30s \
    CMD wget --spider -q http://localhost:7878/ping || exit 1

USER radarr
ENTRYPOINT ["/app/radarr/bin/Radarr"]
CMD ["-nobrowser", "-data=/config/radarr"]

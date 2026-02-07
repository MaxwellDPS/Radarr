# -- Backend build --
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-build
WORKDIR /build
COPY global.json .
COPY src/ src/
RUN dotnet publish src/NzbDrone.Console/Radarr.Console.csproj \
    -c Release \
    -f net8.0 \
    -o /app \
    -p:SelfContained=false

# -- Frontend build --
FROM node:20-slim AS frontend-build
WORKDIR /build
COPY package.json yarn.lock .yarnrc ./
RUN yarn install --frozen-lockfile
COPY frontend/ frontend/
COPY tsconfig.json ./
RUN yarn build --env production

# -- Runtime image --
FROM lscr.io/linuxserver/radarr:latest

# Replace Radarr binaries with our build
RUN rm -rf /app/radarr/bin
COPY --from=backend-build /app /app/radarr/bin
COPY --from=frontend-build /build/_output/UI /app/radarr/bin/UI

# Update package info to reflect custom build
RUN echo -e "UpdateMethod=docker\nBranch=develop\nPackageVersion=custom\nPackageAuthor=custom-build" > /app/radarr/package_info

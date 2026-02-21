# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a **fork of Radarr** adding **Seedr.cc API support** as a download client. Radarr is a movie collection manager for Usenet and BitTorrent users, built with a C# (.NET 8) backend and React/TypeScript frontend.

## Build Commands

### Backend
```bash
# Clean
dotnet clean src/Radarr.sln -c Debug

# Build (macOS/Linux)
dotnet msbuild -restore src/Radarr.sln -p:Configuration=Debug -p:Platform=Posix -t:PublishAllRids

# Build (Windows)
dotnet msbuild -restore src/Radarr.sln -p:Configuration=Debug -p:Platform=Windows -t:PublishAllRids

# Run from output
./_output/net8.0/<runtime>/publish/Radarr
```

### Frontend
```bash
# Enable yarn (included with Node 20+)
corepack enable

# Install dependencies
yarn install

# Dev watch mode
yarn start

# Production build
yarn build --env production
```

### Linting
```bash
yarn lint --fix          # ESLint for JS/TS
yarn stylelint-linux --fix   # CSS lint (Linux/macOS)
yarn stylelint-windows --fix # CSS lint (Windows)
```

### Tests
```bash
# Via build script
./test.sh <PLATFORM> <TYPE> <COVERAGE>
# Examples:
./test.sh Linux Unit Test
./test.sh Windows Integration Test
./test.sh Linux Unit Coverage

# Via dotnet directly (run a single test DLL)
dotnet test _tests/net8.0/Radarr.Core.Test.dll --filter "FullyQualifiedName~ClassName"
```

Test framework: **NUnit 3** with Moq and FluentAssertions. Test categories: `ManualTest`, `IntegrationTest`, `AutomationTest`.

## Architecture

### Solution Structure (`src/Radarr.sln`)

- **NzbDrone.Core** (`Radarr.Core`) — All business logic: download clients, indexers, movies, media files, notifications, parsing. Feature-based organization under namespaces like `NzbDrone.Core.Download`, `NzbDrone.Core.Movies`, etc.
- **Radarr.Api.V3** — REST API controllers and resource models
- **Radarr.Http** — HTTP infrastructure and routing
- **NzbDrone.Common** (`Radarr.Common`) — Shared utilities: HTTP client, disk I/O, caching, extensions, serialization
- **NzbDrone.Host** (`Radarr.Host`) — Bootstrap, DI container setup, startup/shutdown. Entry assembly scans: Host, Core, SignalR, Api.V3, Http
- **Radarr.Console** — Application entry point (set as startup project)
- **Radarr.SignalR** — Real-time UI updates

### Dependency Injection

Uses **DryIoc** via Microsoft.Extensions.DependencyInjection. Convention-based auto-registration by scanning assemblies at startup (`src/NzbDrone.Host/Bootstrap.cs`). Constructor injection throughout — no service locator pattern.

### Download Client Pattern (Key for Seedr Development)

The provider/plugin architecture for download clients:

1. **Interface**: `IDownloadClient` in `NzbDrone.Core/Download/`
2. **Base classes**: `DownloadClientBase<TSettings>` → `TorrentClientBase<TSettings>` (for torrent clients)
3. **Settings**: Class with `[FieldDefinition]` attributes (auto-generates UI fields) + FluentValidation validator
4. **Proxy**: Separate class handling HTTP communication with the external API
5. **Factory**: `DownloadClientFactory` auto-discovers all `IDownloadClient` implementations via DI

### Seedr.cc Implementation

Located at `src/NzbDrone.Core/Download/Clients/Seedr/`:
- `Seedr.cs` — Main client extending `TorrentClientBase<SeedrSettings>`
- `SeedrSettings.cs` — Configuration (email/password) with validation
- `SeedrProxy.cs` — HTTP calls to `https://www.seedr.cc/rest` (Basic Auth)
- `SeedrFolder.cs`, `SeedrTransfer.cs`, `SeedrUser.cs` — API data models
- Uses `ICached<SeedrDownloadMapping>` for tracking torrent hash → Seedr transfer/folder mapping

### Frontend

React 18 + TypeScript + Redux, built with Webpack 5. Source in `frontend/src/`. Key areas:
- `Settings/DownloadClients/` — Download client management UI
- `Components/` — Reusable UI components
- State management via Redux with redux-thunk for async

### Localization

English strings in `src/NzbDrone.Core/Localization/en.json`. Backend uses `ILocalizationService.GetLocalizedString()`. Frontend uses `import translate from 'Utilities/String/translate'`.

## Code Conventions

- **C#**: 4 spaces, `var` everywhere, `_camelCase` for private fields, System usings first, no `this.` qualifier
- **Frontend**: 2 spaces, ESLint + Prettier + Stylelint
- **Line endings**: Unix (LF)
- **Namespaces**: Legacy code uses `NzbDrone.*`, newer code uses `Radarr.*`
- **Commit messages**: Prefix with `New:` or `Fixed:` for non-maintenance changes
- **PRs**: Target `develop` branch only, rebase (don't merge), one feature/fix per PR

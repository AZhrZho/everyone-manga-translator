# EMT Studio application layer

This directory contains the runnable application boundary for EMT Studio. Reusable domain libraries live under `packages/`, command-line utilities under `tools/`, and verification projects under `tests/`.

## Projects

- `MangaTranslator.Contracts`: host-neutral API and SignalR contracts.
- `MangaTranslator.Api`: ASP.NET Core service with a durable-compatible job boundary, Fixture catalog, and replayable precheck coordinator.
- `emt-client`: React + TypeScript client that can run in Electron or as a web app.
- `emt-desktop`: thin Electron host. It starts the local API and exposes only desktop runtime metadata to the client.

## Run the first slice

Terminal 1:

```powershell
& 'C:\Program Files\dotnet\dotnet.exe' run --project .\apps\MangaTranslator.Api\MangaTranslator.Api.csproj --urls http://127.0.0.1:5198
```

Terminal 2:

```powershell
Set-Location .\apps\emt-client
npm run dev -- --host 127.0.0.1
```

The client uses `VITE_API_BASE_URL` when supplied and otherwise targets `http://127.0.0.1:5198`.

For the desktop shell, the development script starts the Vite client and Electron together:

```powershell
Set-Location .\apps\emt-desktop
npm run dev
```

The desktop dependency bootstrap uses the npmmirror Electron binary mirror by default. The mirror is only used for the Electron runtime archive; JavaScript packages still use the configured npm registry. For a clean checkout or an interrupted first download:

```powershell
Set-Location .\apps\emt-desktop
npm run bootstrap
npm run dev
```

The download source and proxy remain overrideable:

```powershell
$env:ELECTRON_MIRROR = 'https://your-mirror.example/electron/'
$env:ELECTRON_GET_USE_PROXY = 'true'
$env:HTTPS_PROXY = 'http://127.0.0.1:7890'
npm run install:electron
```

The desktop shell starts the same ASP.NET Core service with `EMT_HOST_KIND=desktop` and passes Electron's platform paths through `EMT_USER_DATA_ROOT`, `EMT_CACHE_ROOT`, `EMT_LOGS_ROOT`, and `EMT_TEMP_ROOT`.

## Precheck execution sources

Development defaults to `Replay`: the API reads stable artifacts from `precheck-runs/` and turns the real manifest and batch plan into HTTP/SignalR task events. It does not call a model or rerun precheck.

- `Replay`: replays a selected Fixture through the normal serial and parallel task lifecycle.
- `Cached`: loads the selected Fixture as an already-completed snapshot.
- `Live`: reserved for the real workflow adapter and currently returns HTTP 501 to prevent accidental model calls.

The first discovered fixture is used by default. Override discovery with:

```powershell
$env:EMT_FIXTURE_ROOT = 'D:\stable-precheck-runs'
$env:EMT_FIXTURE_RUN_ID = 'sample-run'
```

The catalog intentionally does not inspect `TestResults/`. `GET /api/precheck/fixtures` lists valid replay sources; a request can select one with `fixtureRunId`. The next integration step can add a live adapter over the existing precheck modules without changing the React client contract.

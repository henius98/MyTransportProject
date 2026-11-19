# Architecture & Project Context: MyTransportAppWASM

## 1. Project Overview & Core Mission

**MyTransportAppWASM** is a Client-Side Blazor WebAssembly (.NET 10.0) application designed to provide a unified dashboard for public transit tracking and weather-aware trip planning in Malaysia. It leverages open data APIs (data.gov.my, met.gov.my) and integrates them with a Google Maps frontend to deliver live bus/train positions alongside hyper-local weather forecasts.

## 2. Technical Stack & Architecture

### **Core Framework**

- **Runtime**: Blazor WebAssembly running on .NET 10.0.
- **Assembly Layout & Route Lazy Loading**:
  - `MyTransportAppWASM`: Startup shell (`Program.cs`, `App.razor`, `Core/Layout/`, `Core/Pages/`, `Features/Home/`, and `wwwroot/`).
  - `MyTransportAppWASM.Core`: Startup core library (`Auth/`, `Models/`, `Services/`, `Utils/`, plus embedded `en-US.json` default translations loaded synchronously via `LanguageService.LoadDefaultLanguage()`).
  - `MyTransportAppWASM.Features`: Route-lazy-loaded Razor Class Library (`Transit/`, `Weather/`, `Fortune/`, `Google/`, `FeatureComponentBase`, `FeatureServices`, plus feature-only `Core/Models/` and `Core/Services/` such as `FeatureOptionsBinder`, `FeaturesJsonSerializerContext`, `AppMemoryCache`, and `PlaceResolutionService` that retain `MyTransportAppWASM.Core.*` namespaces). Loaded via `App.razor` on `/map`, `/weather`, `/fortune`, and `/google/*`, with offline/failure retry UI.
  - `MyTransportAppWASM.Gtfs`: Trimmable (`IsTrimmable=true`) GTFS-Realtime wire parser, route-lazy-loaded via `App.razor` when `/map` is opened.
- **Compressed WASM Loading & Splash**:
  - `wwwroot/index.html` starts Blazor via `Blazor.start({ loadBootResource })` using `wwwroot/js/wasm-gzip.js` to fetch `.wasm.gz`, decompress via `DecompressionStream('gzip')`, validate the WebAssembly `\0asm` header, and verify required `sha256-` integrity before a service worker takes control; `wwwroot/service-worker.published.js` pre-caches `.wasm.gz` the same way with bounded concurrency and SRI fallback.
  - `#app-splash` in `wwwroot/index.html` intentionally keeps the standalone dark Glassmorphism loading overlay with `--blazor-load-percentage` progress.
- **Authentication**: Optional Google sign-in via Firebase Authentication managed by `IAuthSessionService` (`FirebaseAuthSessionService`) and cascaded as `AuthSessionState` (no `Microsoft.AspNetCore.Components.Authorization` dependency on the startup path). All routes remain public; Firestore stores per-account preferences protected by owner-only security rules, and optional Cloud Functions (`functions/`) handle Google Workspace connections and FCM push notifications.
- **Styling**: Soft Neo-Brutalist & Warm Technical Editorial custom CSS (`wwwroot/css/app.css`, `wwwroot/css/bootstrap-subset.css`, and `wwwroot/css/icons.css`) with `ThemeService` for Light (default) / Dark mode toggling.

### **Data Integration & External APIs**

- **Transport Data (GTFS-Realtime)**:
  - Fetches from `api.data.gov.my` endpoints covering major Malaysian transit networks (Rapid Bus Penang/KL, KTMB, and BAS.MY).
  - Route-lazy-loads `MyTransportAppWASM.Features` and `MyTransportAppWASM.Gtfs` when `/map` is opened; the startup path does not download feature or GTFS code.
  - Uses a bounded selective Protocol Buffers wire parser that projects only the vehicle fields consumed by the map. `Google.Protobuf` is retained only in tests and host benchmarks for parity validation.
- **Weather Data**:
  - Aggregates multiple sources: **Open-Meteo** (CORS-friendly, primary), **MetMalaysia** (myCuaca/Radar APIs), and **Singapore NEA**.
  - The `WeatherPlannerService` handles dynamic JSON parsing (`JsonDocument`) and shapes heterogeneous API responses into a unified `WeatherTimeSlice` model.
  - Implements robust fallback mechanisms (returning sample/mock data if endpoints fail or CORS blocks requests).
- **Mapping & Geolocation**:
  - Powered by the **Google Maps JavaScript API**.
  - Blazor interacts with the map via JS Interop (`IJSRuntime`), specifically calling into localized scripts (`googleMap.js`, `geolocation.js`).

## 3. Key Modules & Workflows

### **Map & Transit Tracker (`MyTransportAppWASM.Features/Transit/Map.razor`)**

- **Optimistic Initialization**: Loads Google Maps immediately with fallback coordinates (configured in `appsettings.json`) while concurrently searching for the user's GPS lock.
- **Dynamic Bus Syncing**: Identifies transit providers within a certain radius of the user's location. Fetches their GTFS feeds, parses them, and pushes marker updates (`lat`, `lng`, `bearing`, `speed`) directly to the Google Map via JS Interop (`syncMarkers`).
- **Routing**: Integrates Google Maps Directions API (`showRouteByName`) allowing Transit, Walking, Driving, and Bicycling modes.

### **Weather Planner (`MyTransportAppWASM.Features/Weather/Services/WeatherPlannerService.cs`)**

- **Multi-layered Forecasts**:
  1. _Baseline_: Current weather conditions (temperature, precipitation, probability of rain).
  2. _Radar_: Next-hour rain radar prediction.
  3. _Outlooks_: Long-term daily forecasts.
- **Resilience**: Because it's a WASM app running in the browser, CORS is a major concern. The service anticipates CORS failures (especially from MetMalaysia) and seamlessly falls back to Open-Meteo or sample objects.

### **Companion Features (`MyTransportAppWASM.Features/Fortune/`, `MyTransportAppWASM.Features/Google/`)**

- **Fortune**: BaziFlow daily fortune and profile setup via `IBaziFlowService` and `BaziFlowAuthHandler`.
- **Google Workspace**: Google Calendar and Google Tasks dashboards (`GoogleServicePage`, `GoogleCalendarWorkflowService`, `GoogleTasksWorkflowService`) backed by JS interop (`wwwroot/js/google-*.js`, `wwwroot/js/firebase-task-extensions.js`).

### **Configuration (`MyTransportAppWASM/wwwroot/appsettings.json`)**

- Houses Google Maps API keys, `WeatherProviders`, `CacheExpiration`, `DefaultLocation`, and `BaziFlow` settings.
- Defines a comprehensive array of `TransportProviders` (Rapid, KTMB, BAS.MY) with their respective bounding boxes (`CenterLat`, `CenterLng`, `RadiusKm`) and optional `StaticData` endpoints.
- Firebase Web App configuration is stored in the `Firebase` section of `wwwroot/appsettings.json`, with optional `appsettings.{Environment}.json` overrides matching the active Blazor environment. Deployment populates `Firebase`, static data, and BaziFlow base URLs via `scripts/configure-firebase.mjs` from repository variables (`FIREBASE_CONFIG_JSON`, `STATIC_DATA_BASE_URL`, `BAZIFLOW_BASE_URL`).

## 4. Design Patterns & Best Practices Observed

- **Dependency Injection & Lightweight Startup**: Core services, root cascading values (`ThemeService`, `LanguageService`, `AuthSessionState`), and `IAppHttpClientFactory` (`AppHttpClientFactory` providing `ExternalApi`, `StaticAssets`, and `CreateExternalHandler()`) register in `MyTransportAppWASM/Program.cs` without pulling `Microsoft.Extensions.Http`, `Microsoft.Extensions.Caching.Memory`, `System.Net.Http.Json`, or `Microsoft.AspNetCore.Components.Authorization` into the startup bundle. Lazy-loaded feature services (`IGtfsService`, `ITransportStaticDataService`, `ITransitTrackingService`, `IWeatherStationCatalogService`, `IPlaceResolutionService`, `IWeatherPlannerService`, `IBaziFlowService`, `IGoogleWorkspaceService`, `GoogleTasksWorkflowService`, plus caller-disposed transient `TransitMapSession` and `GoogleCalendarWorkflowService`) are provisioned on demand by `MyTransportAppWASM.Features/FeatureServices.cs`, owned by `FeatureServiceHost`, and backed by `IAppCache` (`AppMemoryCache`).
- **Source-Generated JSON**: `AppJsonSerializerContext` (`MyTransportAppWASM.Core`) and `FeaturesJsonSerializerContext` (`MyTransportAppWASM.Features`) pair with `HttpJsonExtensions` (`GetJsonAsync`, `PostJsonAsync`, `ReadJsonAsync`) for trim-safe HTTP JSON serialization.
- **Asynchronous JS Interop**: Heavy use of `Task.WhenAll` to parallelize JS module loading and map initialization without blocking the UI thread, alongside `JsModuleLoader` and `JsInteropExtensions`.
- **Graceful Degradation**: Whether it's GPS permission denial, CORS blocked APIs, failed Protobuf parsing, or failed lazy route-assembly loads (`App.razor` retry UI), the application avoids hard crashes by falling back to defaults and informing the user via the UI (`LocationStatus`).
- **UI & Styling Principles**: Implement minimal, simple CSS with a single source of truth (shared styles). Maintain a clear, modern UI aesthetic while strictly avoiding complex, performance-heavy animations.

## 5. CI & Automated Verification

- **GitHub Actions Workflow**:
  - `.github/workflows/deploy.yml`: Single consolidated pipeline running on PRs, pushes to `master`, and release tags (build, unit tests, Firebase rules/services, browser smoke test, and GitHub Pages deployment).
- **Test Suites**:
  - `.NET` unit & component tests: `dotnet test MyTransportApp.slnx -c Release`
  - Node tests (Firebase config/push, service worker & `wasm-gzip.js`, Google services): `npm run test:node`
  - Cloud Functions tests: `pnpm --dir functions test`
  - Firestore security rule tests (requires Java 21+): `npm --prefix tests/firebase test`
- **Local Workflow Testing (`wrkflw`)**:
  - Tool: [`wrkflw`](https://github.com/bahdotsh/wrkflw) validates and runs workflows locally (emulation or rootless Podman).
  - Helper script: `./scripts/test-workflow-local.sh`
  - Validation: `./scripts/test-workflow-local.sh validate`
  - Execution: `./scripts/test-workflow-local.sh run .github/workflows/deploy.yml --runtime emulation` (or `--runtime podman`)
  - Interactive TUI: `./scripts/test-workflow-local.sh tui`
- **Browser Smoke Test**: `./scripts/smoke-test.sh` verifies live WASM rendering, routing, and navigation via `agent-browser`.

## Agent skills

### Issue tracker

GitHub Issues via `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

Canonical 5-role triage label set. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context layout (`GLOSSARY.md` and `docs/adr/` at root). See `docs/agents/domain.md`.


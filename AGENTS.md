# Architecture & Project Context: MyTransportAppWASM

## 1. Project Overview & Core Mission
**MyTransportAppWASM** is a Client-Side Blazor WebAssembly (.NET 10.0) application designed to provide a unified dashboard for public transit tracking and weather-aware trip planning in Malaysia. It leverages open data APIs (data.gov.my, met.gov.my) and integrates them with a Google Maps frontend to deliver live bus/train positions alongside hyper-local weather forecasts.

## 2. Technical Stack & Architecture

### **Core Framework**
- **Runtime**: Blazor WebAssembly running on .NET 10.0.
- **Authentication**: OIDC (OpenID Connect) via Google Auth (`Microsoft.AspNetCore.Components.WebAssembly.Authentication`).
- **Styling**: Custom CSS with a `ThemeService` for dynamic Light/Dark mode toggling.

### **Data Integration & External APIs**
- **Transport Data (GTFS-Realtime)**:
  - Fetches from `api.data.gov.my` endpoints covering major Malaysian transit networks (Rapid Bus Penang/KL, KTMB, and BAS.MY).
  - Uses Protocol Buffers (`Google.Protobuf`, `TransitRealtime.FeedMessage`).
  - Employs a custom, highly-optimized `ProtocolBufferUtils` to parse binary streams efficiently.
- **Weather Data**:
  - Aggregates multiple sources: **Open-Meteo** (CORS-friendly, primary) and **MetMalaysia** (myCuaca/Radar APIs).
  - The `WeatherPlannerService` handles dynamic JSON parsing (`JsonDocument`) and shapes heterogeneous API responses into a unified `WeatherTimeSlice` model.
  - Implements robust fallback mechanisms (returning sample/mock data if endpoints fail or CORS blocks requests).
- **Mapping & Geolocation**:
  - Powered by the **Google Maps JavaScript API**.
  - Blazor interacts with the map via JS Interop (`IJSRuntime`), specifically calling into localized scripts (`googleMap.js`, `geolocation.js`).

## 3. Key Modules & Workflows

### **Map & Transit Tracker (`Map.razor`)**
- **Optimistic Initialization**: Loads Google Maps immediately with fallback coordinates (configured in `appsettings.json`) while concurrently searching for the user's GPS lock.
- **Dynamic Bus Syncing**: Identifies transit providers within a certain radius of the user's location. Fetches their GTFS feeds, parses them, and pushes marker updates (`lat`, `lng`, `bearing`, `speed`) directly to the Google Map via JS Interop (`syncMarkers`).
- **Routing**: Integrates Google Maps Directions API (`showRouteByName`) allowing Transit, Walking, Driving, and Bicycling modes.

### **Weather Planner (`WeatherPlannerService.cs`)**
- **Multi-layered Forecasts**:
  1. *Baseline*: Current weather conditions (temperature, precipitation, probability of rain).
  2. *Radar*: Next-hour rain radar prediction.
  3. *Outlooks*: Long-term daily forecasts.
- **Resilience**: Because it's a WASM app running in the browser, CORS is a major concern. The service anticipates CORS failures (especially from MetMalaysia) and seamlessly falls back to Open-Meteo or sample objects.

### **Configuration (`appsettings.json`)**
- Houses Google Maps API Keys.
- Defines a comprehensive array of `TransportProviders` (Rapid, KTMB, BAS.MY) with their respective bounding boxes (`CenterLat`, `CenterLng`, `RadiusKm`).
- Configures OIDC endpoints for Google Authentication.

## 4. Design Patterns & Best Practices Observed
- **Dependency Injection**: Services (`IGtfsService`, `IWeatherPlannerService`) are injected via `AddHttpClient`, ensuring proper connection pooling and lifecycle management.
- **Asynchronous JS Interop**: Heavy use of `Task.WhenAll` to parallelize JS module loading and map initialization without blocking the UI thread.
- **Graceful Degradation**: Whether it's GPS permission denial, CORS blocked APIs, or failed Protobuf parsing, the application avoids hard crashes by falling back to defaults and informing the user via the UI (`locationStatus`).
- **UI & Styling Principles**: Implement minimal, simple CSS with a single source of truth (shared styles). Maintain a clear, modern UI aesthetic while strictly avoiding complex, performance-heavy animations.
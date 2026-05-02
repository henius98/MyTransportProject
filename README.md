# MyTransportAppWASM

A unified dashboard for public transit tracking and weather-aware trip planning in Malaysia. Built with **Blazor WebAssembly (.NET 10.0)**, this application integrates live transit data (GTFS-Realtime) with hyper-local weather forecasts to help users plan their commutes efficiently.

## 🚀 Key Features

- **Real-Time Transit Tracking**: Live positions for Rapid Bus (Penang, KL, Kuantan, MRT Feeder), KTMB National Rail, and BAS.MY (Kangar, Alor Setar, Ipoh, etc.) across Malaysia.
- **Interactive Mapping**: Powered by Google Maps JavaScript API with:
  - Custom vehicle markers and info windows showing route IDs and speeds.
  - Draggable user location marker (🚶) for precise planning.
  - Direction routing (Transit, Walking, Driving, Bicycling).
  - Advanced Marker Elements for smooth performance.
- **Weather-Aware Planning**:
  - Integrated weather forecasts from **Open-Meteo** and **MetMalaysia**.
  - Real-time rain radar predictions.
  - Resilient fallback mechanisms for CORS-blocked or unavailable endpoints.
- **Authentication**: Secure login via **Google OIDC** integration.
- **Modern UI/UX**:
  - Dynamic **Light/Dark mode** support via a dedicated `ThemeService`.
  - Responsive design optimized for both desktop and mobile form factors.
  - Progressive Web App (PWA) support for offline capability and "Add to Home Screen".

## 🛠️ Technical Stack

- **Runtime**: .NET 10.0 (Blazor WebAssembly)
- **Mapping**: Google Maps JS API (via JS Interop)
- **Serialization**: Google Protocol Buffers (`Google.Protobuf`) for efficient GTFS-Realtime parsing.
- **Authentication**: `Microsoft.AspNetCore.Components.WebAssembly.Authentication` (OIDC)
- **Styling**: Custom Vanilla CSS with Bootstrap 5 baseline.

## 📂 Project Structure

- `MyTransportAppWASM/Pages`: Razor components for the main application views (`Map.razor`, `Weather.razor`, `Home.razor`).
- `MyTransportAppWASM/Services`: Core logic for transit data fetching, weather aggregation, and geocoding.
- `MyTransportAppWASM/Utils`: High-performance utilities like `ProtocolBufferUtils.cs` for binary data parsing.
- `MyTransportAppWASM/wwwroot/js`: JavaScript modules for direct Google Maps manipulation and geolocation.

## ⚙️ Configuration

The application requires several API keys and configurations in `wwwroot/appsettings.json`:

```json
{
  "GoogleMaps": {
    "ApiKey": "YOUR_GOOGLE_MAPS_API_KEY"
  },
  "Authentication": {
    "Google": {
      "Authority": "https://accounts.google.com/",
      "ClientId": "YOUR_GOOGLE_CLIENT_ID"
    }
  }
}
```

Transit and weather providers are also configured in `appsettings.json`, allowing for easy extension to new regions or data sources.

## 📊 Data Sources

- **Transport Data**: [data.gov.my](https://data.gov.my) (GTFS-Realtime feeds)
- **Weather Data**: [met.gov.my](https://www.met.gov.my) (MetMalaysia) and [Open-Meteo](https://open-meteo.com)
- **Geocoding & Maps**: Google Maps Platform

## 🚀 Getting Started

### Prerequisites

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

### Running the App

To run the application normally:

```bash
dotnet run --project MyTransportAppWASM
```

### Hot Reload (Development)

To run with Hot Reload enabled:

```bash
dotnet watch --project MyTransportAppWASM
```

## 📄 License

This project is licensed under the MIT License.

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
- **Optional Google sign-in**: Firebase Authentication and per-account preferences in Firestore. Every page remains public.
- **Modern UI/UX**:
  - Dynamic **Light/Dark mode** support via a dedicated `ThemeService`.
  - Responsive design optimized for both desktop and mobile form factors.
  - Progressive Web App (PWA) support for offline capability and "Add to Home Screen".

## 🛠️ Technical Stack

- **Runtime**: .NET 10.0 (Blazor WebAssembly)
- **Mapping**: Google Maps JS API (via JS Interop)
- **Serialization**: A selective, allocation-conscious GTFS-Realtime wire parser that reads only the vehicle fields used by the map. The browser runtime does not ship `Google.Protobuf`.
- **Authentication**: Firebase JavaScript SDK with a Blazor `AuthenticationStateProvider`.
- **Styling**: Custom Vanilla CSS with Bootstrap 5 baseline.

## 📂 Project Structure

- `MyTransportAppWASM/Pages`: Razor components for the main application views (`Map.razor`, `Weather.razor`, `Home.razor`).
- `MyTransportAppWASM/Services`: Core logic for transit data fetching, weather aggregation, and geocoding.
- `MyTransportAppWASM.Gtfs`: Route-lazy-loaded GTFS-Realtime parser and transport projection contract.
- `MyTransportAppWASM/Utils`: Shared data-shaping utilities for runtime hot paths.
- `MyTransportAppWASM/wwwroot/js`: JavaScript modules for direct Google Maps manipulation and geolocation.
- `benchmarks`: Repeatable host and browser-native Mono/WASM performance harnesses.
- `bruno`: Ready-to-open requests for the app's external APIs; see [collection setup](bruno/README.md).

## ⚙️ Configuration

The application requires several API keys and configurations in `wwwroot/appsettings.json`:

```json
{
  "GoogleMaps": {
    "ApiKey": "YOUR_GOOGLE_MAPS_API_KEY"
  },
  "Firebase": {
    "apiKey": "YOUR_FIREBASE_API_KEY",
    "authDomain": "YOUR_PROJECT_ID.firebaseapp.com",
    "projectId": "YOUR_PROJECT_ID",
    "appId": "YOUR_FIREBASE_APP_ID"
  }
}
```

Transit and weather providers are also configured in `appsettings.json`, allowing for easy extension to new regions or data sources.

### Optional Google sign-in and preferences

1. Create a Firebase project and register a Web App. Enable **Authentication → Sign-in method → Google**, choose a support email, and add the deployed hostname (and `localhost` for development) under **Authentication → Settings → Authorized domains**.
2. Create a Cloud Firestore database. Deploy the repository's `firestore.rules` to that project. These rules allow only the signed-in owner to read/write `users/{uid}/data/settings`, validate preference fields, and deny all other client access. If using an existing database, review and merge any existing rules before publishing these defaults.
3. Copy the public Firebase Web App configuration into the `Firebase` object in `MyTransportAppWASM/wwwroot/appsettings.json`. Required fields are `apiKey`, `authDomain`, `projectId`, and `appId`. For automated deployment, set the repository Actions **variable** `FIREBASE_CONFIG_JSON` to that Firebase JSON object (without a `Firebase` wrapper). The deployment step validates it and updates only the `Firebase` section, preserving the other application settings; it fails clearly if configuration is missing.
4. Publish the app. For rules deployment with the Firebase CLI, authenticate to the intended project and run `firebase deploy --only firestore:rules --project YOUR_PROJECT_ID` from the repository root. App deployment does not publish database rules automatically.

`firebase-config.js` loads the `Firebase` object from `appsettings.json` and overlays any `Firebase` keys in the optional `appsettings.{Environment}.json`, using the active Blazor environment. For example, put development overrides in `wwwroot/appsettings.Development.json`; omitted keys retain their base values.

The web config is public by design. Never put service-account keys, OAuth client secrets, or Admin SDK credentials in this WASM application. Google manages Google credentials, Firebase Authentication manages the app session, and application preferences live in your Firebase project.

Visitors can use the transit, weather, and fortune pages without signing in. The Google service pages show a sign-in prompt to guests. Login personalizes theme, language, and welcome state; the settings model also supports a default location. Accounts and guests have separate local caches. New accounts start with defaults rather than copying another person's device preferences. On login/logout, the app applies the active account's theme and language. A failed cloud operation falls back to that account's local settings and displays a notice in the profile menu. Failed writes are not automatically replayed: retry the preference change after reconnecting. Successful reads use cloud values, and updates merge only the edited field to preserve other devices' changes.

Google sign-in requests only the standard sign-in profile. Calendar and Tasks request their Google OAuth scopes separately when the user connects each service. A Firebase ID token is not a Google API access token; do not add broad scopes or persist Google access tokens in the preferences document. Permission-granting roles, if ever needed, must be assigned through a trusted administrator and enforced by backend rules, not editable preferences.

See [Firebase Google sign-in](https://firebase.google.com/docs/auth/web/google-signin) and [Firestore security rules](https://firebase.google.com/docs/firestore/security/rules-conditions).

### Google Calendar, Tasks, and Keep

Signed-in users can open **My Google** from the navigation menu:

- `/google`: service overview, including shortcuts to Gmail, Drive, and Docs.
- `/google/calendar`: calendar selection, event browsing, and creating, editing, or deleting events. Read-only calendars can be viewed; recurring event edits affect the selected occurrence.
- `/google/tasks`: task-list selection and creating, editing, completing, reopening, or deleting tasks.
- `/google/notes`: opens Google Keep in a new tab to view and edit existing notes. Google's public Keep API is designed for managed enterprise accounts; this app does not claim to sync personal Keep notes.

Enable **Google Calendar API** and **Google Tasks API** in the Google Cloud project used by Firebase Authentication's Google OAuth client. In **Google Auth Platform → Data Access**, configure these scopes on that OAuth client's consent screen:

| Service | Scopes |
| --- | --- |
| Calendar | `https://www.googleapis.com/auth/calendar.calendarlist.readonly`, `https://www.googleapis.com/auth/calendar.events` |
| Tasks | `https://www.googleapis.com/auth/tasks` |

While the OAuth app is in testing, add the intended Google accounts as test users. Complete Google's applicable consent-screen verification before making these scopes available publicly. These integrations use the existing Firebase Google provider; no client secret, service account, or additional browser API key is needed. The Maps API key does not grant Calendar or Tasks access.

Connecting a service opens Google's permission window and reauthenticates the current Firebase user. Tokens stay in JavaScript memory, are cleared on sign-out/account changes, and must be renewed with **Reconnect** after expiry or a page reload. Failed saves are not replayed automatically. Updates use PATCH with the item's ETag to detect changes made elsewhere. Google service data is fetched directly from Google and is not copied into Firestore or browser storage.

Run the Google API boundary tests with `node --experimental-vm-modules --test tests/google-services/*.test.mjs`, and the .NET tests with `dotnet test tests/MyTransportAppWASM.Tests`. Before release, test with an authorized account: connect each service, cancel or deny a permission request, create/edit/delete a test event, complete/reopen a test task, and sign out during loading. Check that an account switch clears the previous user's content. Actual Google access requires the Cloud configuration above and real user consent.

References: [Calendar scopes](https://developers.google.com/workspace/calendar/api/auth), [Tasks scopes](https://developers.google.com/workspace/tasks/auth), [Keep API overview](https://developers.google.com/workspace/keep/api/guides).

### Authentication verification

Run the .NET regression tests with `dotnet test tests/MyTransportAppWASM.Tests`.
For Firestore rules, install Node.js and Java 21+, then run `npm ci` and `npm test` from `tests/firebase`. These tests use the local emulator with a demo project; they do not touch production data.

Before enabling sign-in for users, check with two real Google accounts:

- All routes work signed out, including direct links to `/map`, `/weather`, and `/fortune`.
- Sign in, change theme/language, refresh, and sign in from another browser: saved preferences return.
- Sign out while remaining on the current page: guest preferences return. Sign in as a second account: the first account's preferences are not copied.
- Cancel the Google popup, block popups, or go offline: public pages remain usable and login can be retried.
- A failed cloud save displays a notice; reconnecting and repeating the change saves it successfully.

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

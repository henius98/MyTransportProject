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
  - Installable PWA manifest and opt-in Web Push notifications on supported browsers.

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
    "appId": "YOUR_FIREBASE_APP_ID",
    "messagingSenderId": "YOUR_MESSAGING_SENDER_ID",
    "webPushVapidKey": "YOUR_PUBLIC_WEB_PUSH_VAPID_KEY"
  }
}
```

Transit and weather providers are also configured in `appsettings.json`, allowing for easy extension to new regions or data sources.

### Android notifications from a Cloudflare Worker

Web Push uses the existing Firebase project. In **Firebase console → Project settings → Cloud Messaging → Web Push certificates**, generate a key pair. Put the public key in `Firebase.webPushVapidKey` and include it with `messagingSenderId` in `FIREBASE_CONFIG_JSON` for GitHub Pages deployment. Enable the FCM Registration API if the Firebase project requires it. The private VAPID key stays in Firebase.

Deploy `pushRegistration`, `pushWebhook`, and the Firestore rules. Set a long random `PUSH_WEBHOOK_SECRET` with `firebase functions:secrets:set PUSH_WEBHOOK_SECRET --project YOUR_PROJECT_ID`, then run `firebase deploy --only functions:pushRegistration,functions:pushWebhook,firestore:rules --project YOUR_PROJECT_ID`. Put the same secret and the deployed `pushWebhook` URL in the Cloudflare Worker's secrets/configuration. Do not put this secret in `wwwroot` or the Worker source. The web deployment workflow does not deploy Firebase Functions or rules.

On Android Chrome, open the HTTPS app, sign in, open the profile menu, and choose **Enable notifications**. Accept the browser permission prompt. The service worker can then display notifications when the tab is closed. Browser and Android notification settings still control delivery; force-stopping the browser or disabling notifications can prevent it. The app does not poll third-party APIs while closed. A Cloudflare Worker must send each alert when its event occurs.

From a trusted Cloudflare Worker handler, send a POST to the Firebase Function with the Firebase user's UID and a relative app path:

```js
const pushResponse = await fetch(env.PUSH_WEBHOOK_URL, {
  method: "POST",
  headers: {
    Authorization: `Bearer ${env.PUSH_WEBHOOK_SECRET}`,
    "Content-Type": "application/json"
  },
  body: JSON.stringify({
    uid: firebaseUserUid,
    title: "Transit update",
    body: "Your service has an update.",
    url: "map"
  })
});
if (!pushResponse.ok) throw new Error(`Push relay failed: ${pushResponse.status}`);
```

The Worker must obtain `firebaseUserUid` from its trusted event or account mapping. Do not expose an unauthenticated public endpoint that lets callers pick any UID and message. The webhook returns `{ "sent": 0, "failed": 0 }` when a user has no opted-in devices. Users can turn notifications off in the same menu; signing out removes the current device registration. Test on a real Android phone by sending an alert, closing the app tab, sending another alert, and tapping the notification to reopen the app.

### Optional Google sign-in and preferences

1. Create a Firebase project and register a Web App. Enable **Authentication → Sign-in method → Google**, choose a support email, and add the deployed hostname (and `localhost` for development) under **Authentication → Settings → Authorized domains**.
2. Create a Cloud Firestore database. Deploy the repository's `firestore.rules` to that project. These rules allow only the signed-in owner to read/write `users/{uid}/data/settings`, validate preference fields, and deny all other client access. If using an existing database, review and merge any existing rules before publishing these defaults.
3. Copy the public Firebase Web App configuration into the `Firebase` object in `MyTransportAppWASM/wwwroot/appsettings.json`. Required fields are `apiKey`, `authDomain`, `projectId`, and `appId`. For automated deployment, set the repository Actions **variable** `FIREBASE_CONFIG_JSON` to that Firebase JSON object (without a `Firebase` wrapper). The deployment step validates it and updates only the `Firebase` section, preserving the other application settings; it fails clearly if configuration is missing.
4. Publish the app. For rules deployment with the Firebase CLI, authenticate to the intended project and run `firebase deploy --only firestore:rules --project YOUR_PROJECT_ID` from the repository root. App deployment does not publish database rules automatically.

`firebase-config.js` loads the `Firebase` object from `appsettings.json` and overlays any `Firebase` keys in the optional `appsettings.{Environment}.json`, using the active Blazor environment. For example, put development overrides in `wwwroot/appsettings.Development.json`; omitted keys retain their base values.

Google One Tap prompts signed-out visitors after Firebase restores its saved session. Set `Firebase.googleClientId` to the Google OAuth **Web client ID** associated with this Firebase project (ending in `.apps.googleusercontent.com`), available under **Authentication → Sign-in method → Google → Web SDK configuration**. Include `googleClientId` in `FIREBASE_CONFIG_JSON` for deployment. In Google Cloud's **Google Auth Platform → Clients**, add the deployed origin (scheme and hostname, without a path) and your localhost development origins (including the port) to that client's **Authorized JavaScript origins**. Use HTTPS in production. The client ID is public; do not add a client secret.

One Tap loads on demand, prompts at most once per page load, and is skipped when `googleClientId` is empty or the visitor is already signed in. Opening the login dialog or signing out cancels it for the current page load. Its Google ID token is exchanged through Firebase, which updates the existing Blazor authentication state. The regular sign-in button remains available if the Google script is blocked, the prompt is dismissed, or the browser suppresses it. The browser controls the prompt's position and appearance when using FedCM. See [Google One Tap setup](https://developers.google.com/identity/gsi/web/guides/get-google-api-clientid).

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

While the OAuth app is in testing, add the intended Google accounts as test users. Complete Google's applicable consent-screen verification before making these scopes available publicly. Use the same OAuth Web client as Firebase sign-in. Its client secret belongs only in Firebase Secret Manager, never in the WASM app. The Maps API key does not grant Calendar or Tasks access.

Connecting a service uses Google's authorization-code flow. The `googleConnection` callable Firebase Function verifies the Firebase user, allowed browser origin, granted scopes, and Google account identity before saving credentials in `users/{uid}/googleConnections/{service}`. Each record contains `accessToken`, `expiresAt`, `refreshToken`, `googleSubject`, and `scopes`. Firestore rules deny all browser access to these records, including access by their owner; only the backend reads or writes them. User preferences remain in `users/{uid}/data/settings`.

The backend uses Google's `expires_in` value (normally about one hour) as the expiry source. It refreshes within one minute of expiry, or after a Google API 401, and stores the new access token and any rotated refresh token. There is no application expiry for refresh tokens. An `invalid_grant` refresh response removes the unusable connection; network failures, rate limits, and server/configuration errors retain it for a later retry. Missing service permissions mark the connection as requiring consent. A Google API request is retried once after a 401; other failures and ambiguous failed saves are not replayed. Updates use PATCH with the item's ETag to detect changes made elsewhere.

Only the short-lived access token is returned to the browser and cached locally. Signing out clears this browser cache; signing in again or opening the app on another device restores the account's connection from Firestore. Refresh tokens never enter browser storage or Blazor interop. Calendar and task data continues to be fetched directly from Google, without being copied into Firestore. Existing connections created before the backend was added require **Reconnect** once to obtain offline access. Google can still revoke or expire refresh tokens; external OAuth apps in Testing generally receive refresh tokens that expire after seven days for these scopes.

#### Deploy automatic token renewal

1. Enable the Firebase project's Blaze plan for Cloud Functions, and enable the Calendar and Tasks APIs. Use Node.js 24 for the `functions` package. From the repository root, run `pnpm --prefix functions install`.
2. Configure that OAuth Web client's **Authorized JavaScript origins** with the exact production and development origins, including ports, without paths or trailing slashes. For GIS popup code exchange, also register these exact origins as **Authorized redirect URIs**. A site at `https://example.com/MyTransportApp/` uses `https://example.com` for both settings.
3. Run `firebase functions:secrets:set GOOGLE_OAUTH_CLIENT_SECRET --project YOUR_PROJECT_ID` and enter the OAuth Web client secret when prompted. Do not paste it into `wwwroot/appsettings.json` or source control.
4. Run `firebase deploy --only functions:googleConnection,firestore:rules --project YOUR_PROJECT_ID`. The CLI prompts for `GOOGLE_OAUTH_CLIENT_ID` and `GOOGLE_OAUTH_ALLOWED_ORIGINS` (a comma-separated list of the same exact origins). It saves these non-secret parameters in an ignored `functions/.env.YOUR_PROJECT_ID` file. The function and browser SDK use Firebase's default `us-central1` region.
5. Publish the WASM app and reconnect Calendar and Tasks once. Verify reopening after an hour, signing out and back in, and signing in on another device. If Google does not issue a refresh token, remove this app's connection in Google Account permissions and reconnect the services.

The existing web deployment workflow tests the backend but does not deploy functions or secrets. Deploy the function before publishing the updated web app. See [Google's authorization-code flow](https://developers.google.com/identity/oauth2/web/guides/use-code-model), [refresh-token expiration](https://developers.google.com/identity/protocols/oauth2#expiration), and [Firebase function secrets](https://firebase.google.com/docs/functions/config-env#secret_parameters).

Run the Google API boundary tests with `node --experimental-vm-modules --test tests/google-services/*.test.mjs`, backend tests with `pnpm --prefix functions test`, and the .NET tests with `dotnet test tests/MyTransportAppWASM.Tests`. Before release, test with an authorized account: connect each service, cancel or deny a permission request, create/edit/delete a test event, complete/reopen a test task, and sign out during loading. Check that an account switch clears the previous user's content. Actual Google access requires the Cloud configuration above and real user consent.

References: [Calendar scopes](https://developers.google.com/workspace/calendar/api/auth), [Tasks scopes](https://developers.google.com/workspace/tasks/auth), [Keep API overview](https://developers.google.com/workspace/keep/api/guides).

### Authentication verification

Run the .NET regression tests with `dotnet test tests/MyTransportAppWASM.Tests`.

Before enabling sign-in for users, check with two real Google accounts:

- All routes work signed out, including direct links to `/map`, `/weather`, and `/fortune`.
- Sign in, change theme/language, refresh, and sign in from another browser: saved preferences return.
- Sign out while remaining on the current page: guest preferences return. Sign in as a second account: the first account's preferences are not copied.
- Cancel the Google popup, block popups, or go offline: public pages remain usable and login can be retried.
- With One Tap configured, open a signed-out page and choose a Google account: the profile and preferences update. Refresh while signed in: no prompt appears. Dismiss the prompt, open manual sign-in, or sign out: it does not reopen on route changes. Test script blocking and a rejected credential: the regular sign-in button still works.
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

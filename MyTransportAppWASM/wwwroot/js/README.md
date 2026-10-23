# Client JavaScript Architecture & Third-Party Service Registry

This directory contains browser-side JavaScript modules and interop bridges. Files are organized by their third-party provider and companion C# service adapter:

## 1. Google Services

| Script | Provider / Subsystem | Companion C# Service / Component | Purpose |
|---|---|---|---|
| `google-services.js` | Google Workspace | `Services/Google/Workspace/GoogleWorkspaceService.cs` | Google Calendar & Google Tasks REST API client |
| `google-authorization.js` | Google Workspace / Identity | `Services/Google/Workspace/GoogleWorkspaceService.cs` | Google Identity Services OAuth 2.0 authorization code flow |
| `google-token-store.js` | Google Workspace | `Services/Google/Workspace/GoogleWorkspaceService.cs` | Client token caching with session/local storage fallback |
| `googleMap.js` | Google Maps Platform | `Services/Google/Maps/LiveRoutingService.cs`, `Pages/Map/Map.razor.cs` | Maps JS API, markers, routes, polyline decoding |
| `locationPicker.js` | Google Places Platform | `Pages/Weather/Components/LocationSearchAutocomplete.razor` | Google Places (New) AutocompleteElement interop |

## 2. Firebase Services

| Script | Provider / Subsystem | Companion C# Service / Component | Purpose |
|---|---|---|---|
| `firebase-config.js` | Firebase Core | `wwwroot/index.html` | Firebase App initialization with multi-environment appsettings merge |
| `firebase-auth.js` | Firebase Authentication | `Services/Firebase/FirebaseAuthenticationStateProvider.cs` | Google Sign-In with popup and One Tap credential exchange |
| `firebase-firestore.js` | Cloud Firestore | `Services/Firebase/UserSettingsService.cs` | User profile & application settings document persistence |
| `firebase-task-extensions.js` | Cloud Firestore | `Services/Google/Workspace/GoogleTasksWorkflowService.cs` | Task metadata, recurrence, and dependency tracking in Firestore |
| `push-notifications.js` | Firebase Cloud Messaging | `wwwroot/firebase-messaging-sw.js` | Push notification permission, FCM token registration, and notification routing |

## 3. Core Browser Utilities

| Script | Role | Companion C# Service | Purpose |
|---|---|---|---|
| `geolocation.js` | Browser Geolocation API | `Services/Core/LocationService.cs` | GPS coordinate retrieval and watchPosition tracking |
| `theme-manager.js` | Theme Management | `Services/Core/ThemeService.cs` | Pre-Blazor theme flash prevention and body class synchronization |

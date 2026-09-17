# MyTransportAppWASM external API collection

Open this `bruno/` directory as a collection in Bruno, then select the **Example** environment. Requests mirror the outbound calls in `MyTransportAppWASM` and `scripts/fetch_rc_data.py`. Application configuration and source code remain the authority for active URLs and request shapes.

Edit the example coordinates, location ID, dates, calendar and task IDs before sending requests. GTFS responses are binary Protocol Buffers; download the response to inspect it with a GTFS Realtime decoder. The example environment has no credentials. Set `google_maps_api_key`, `google_workspace_access_token`, `bazi_api_key`, and `firebase_id_token` as Bruno secret variables only when needed. Do not save live tokens in tracked collection files.

| Folder | Requests | Setup |
| --- | --- | --- |
| 01 Transit | Every GTFS vehicle-position endpoint configured in `MyTransportAppWASM/wwwroot/appsettings.json` | None |
| 02 Weather | MetMalaysia/data.gov.my, Open-Meteo baseline and outlook, Singapore NEA baseline and outlook | Current dates and forecast location |
| 03 Google Maps | Places text search, reverse geocoding, JavaScript API loader | Google Maps key with the corresponding APIs enabled |
| 04 Google Calendar | List calendars/events, create/update/delete events | Google OAuth access token with the Calendar scopes used by the app |
| 05 Google Tasks | List task lists/tasks, create/update/delete tasks | Google OAuth access token with the Tasks scope used by the app |
| 06 BaziFlow | Get/create profile, date fortune | Running BaziFlow backend and its API key |
| 07 Data import script | Daily weather data and Nominatim search | Current date; respect Nominatim request limits |
| 08 Firebase | Read settings and merge one theme field | Firebase project ID, user UID and that user's Firebase ID token |

The Google Workspace token must be a **Google OAuth access token** granted with the relevant scopes. A Firebase ID token only authorizes the Firestore requests. The app obtains the Google token through its Firebase Google sign-in flow and keeps it in JavaScript memory. Copy values into Bruno's local secrets; they are not in this repository.

Google Maps routing, autocomplete and rendering use the Maps JavaScript SDK; Firebase sign-in and Firestore use the Firebase Web SDK. Bruno lists the direct HTTP calls and a Firestore REST equivalent for settings read/write. It cannot reproduce the SDK's browser session or map interactions.

The BaziFlow entries reflect what the current Blazor service sends. If you point them at the adjacent `BaziFlowAgent` project, its current profile API expects snake_case birth fields, and its date-fortune route is a WebSocket GET. Those are existing differences between the projects; this collection does not change the app's behavior.

Send write and delete examples individually, after replacing the sample IDs and ETags. Running the whole collection would create, edit or delete data in the selected services.

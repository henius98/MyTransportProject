# Daily Fortune Module Design Spec

## Overview
This feature integrates the BaziFlowAgent API into MyTransportAppWASM to provide users with a daily fortune reading based on their profile (gender, birth date/time, location). The reading acts as a specialized module alongside the existing transit tracking and weather forecasting, expanding the application's daily utility.

## Architecture & Integration

### Endpoints
- Base URL: `http://localhost:3000` (or configured via `appsettings.json`)
- `GET /api/v1/profile`: Fetches the current user's profile.
- `POST /api/v1/profile`: Creates/updates the user's profile.
- `POST /api/v1/date-fortune`: Fetches the fortune for a given date.

### BaziFlowService (Client Service)
- A new typed HTTP client: `BaziFlowService`.
- Responsible for attaching the Bearer API key to requests.
- C# representations of the backend API payloads (e.g. `CreateProfileRequest`, `DateFortuneRequest`, `ProfileData`, `FortuneData`).

### State & Authentication
- **Storage**: `Blazored.LocalStorage` will be used to store the user's BaziFlow API key securely in the browser.
- **Auth Flow**: When the `Fortune` page mounts, it checks local storage for the key. If missing, it prompts the user. The `BaziFlowService` will retrieve this key dynamically for outbound requests.

## User Interface

### Layout and Routing
- **NavMenu Addition**: A new navigation link labeled "Fortune" pointing to `/fortune`.
- **Page Component (`Fortune.razor`)**: A dedicated page for displaying the daily reading.

### Design Aesthetic (Frontend-Design & Modern Web)
- **Signature Element**: The page features a central Glassmorphism card displaying the daily fortune text (Almanac and Analysis). The background should subtly reflect the nature of the feature (perhaps a gradient that shifts with the time of day, matching the application's overall light/dark mode theme).
- **Profile Configuration Modal**: A clean dialog using modern UI patterns (`<dialog>` or a Blazor modal equivalent) to capture the API Key, Gender, and Birth Date/Time. It degrades gracefully, maintaining the app's clean typography and spacing.
- **Empty State**: If the API key or profile is missing, an inviting empty state appears with a call-to-action to setup the profile.

## Error Handling
- **API Unreachable/CORS**: The service will catch `HttpRequestException` and provide a user-friendly error message within the UI (e.g., "Cannot connect to the BaziFlow service. Please check your connection.").
- **Invalid Token**: The UI will detect `401 Unauthorized` responses and prompt the user to update their API key in the configuration modal.

## Future Scope (Not in MVP)
- Chat interactions.
- Pick Date feature.
- Multi-user profile management.

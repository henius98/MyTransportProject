# Auspicious Trip Routing Design

## Overview
This feature integrates the existing BaziFlow (Fortune) module with the transit routing system in `MyTransportAppWASM`. By matching the daily favorable directions and lucky hours against proposed transit routes, the app will subtly highlight auspicious travel options for the user. 

## Approach
- **Subtle UI Integration**: Standard route searches will be enriched with an "Auspicious Badge" (or tooltip/icon) if the route's primary direction matches the user's daily favorable direction, or if the departure/arrival time falls within the daily lucky hours.
- **Backend Schema Evolution**: The BaziFlow backend API (currently external to this repository) will need to be updated to provide structured data (`FavorableDirections`, `LuckyHours`). 
- **Client-Side Mocking**: While the backend is being updated, the Blazor client will use mocked structured data in the `FortuneData` model to build and test the UI.

## Components & Data Flow

### 1. `FortuneData` Model Update
**File:** `MyTransportAppWASM/Models/BaziFlow/BaziFlowModels.cs`
Update `FortuneData` to include new structured fields that the backend will eventually provide:
```csharp
public record FortuneData
{
    public string Almanac { get; set; } = string.Empty;
    public string Analysis { get; set; } = string.Empty;
    public List<string> FavorableDirections { get; set; } = new(); // e.g., ["North", "East"]
    public List<int> LuckyHours { get; set; } = new(); // e.g., [7, 8, 17, 18] (0-23 format)
}
```

### 2. `BaziFlowService` Mocking
**File:** `MyTransportAppWASM/Services/BaziFlowService.cs`
Update `GetDateFortuneAsync` to append mocked `FavorableDirections` and `LuckyHours` to the response until the backend API natively supports them.

### 3. Route Calculation & Matching
The routing logic (which interacts with Google Maps API via JS Interop) needs a mechanism to evaluate a route:
- Extract the primary bearing/direction of a calculated route.
- Compare the bearing to the `FavorableDirections`.
- Compare the route departure time to `LuckyHours`.

### 4. UI Integration (`Map.razor` or Route Results Panel)
- Inject `IBaziFlowService` to access the current day's `FortuneData`.
- When rendering route options, check the route against the fortune data.
- If it aligns, display a subtle UI indicator (e.g., a gold star, a lucky badge, or highlighted text with a tooltip explaining *why* it's auspicious).

## Expected Backend Changes (For Reference)
The external BaziFlow backend (running at `localhost:8080`) must update the `/api/v1/date-fortune` endpoint to return the structured `FavorableDirections` and `LuckyHours` arrays.

## Testing Strategy
- Verify the mocked `BaziFlowService` correctly populates the new fields.
- Test routing combinations (northbound vs southbound, morning vs evening) to ensure the badge conditionally appears when the mocked criteria are met.

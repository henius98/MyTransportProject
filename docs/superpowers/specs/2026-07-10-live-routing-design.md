# Live Routing Algorithm Service Design

## 1. Purpose and Goal
The goal is to provide a "live routing" experience. Instead of blindly trusting Google Maps transit directions (which rely on static schedules), this service will fetch multiple route alternatives from Google Maps, cross-reference the required bus lines with our live GTFS-Realtime data (provided by `data.gov.my`), and rank the routes based on the actual proximity of active buses.

## 2. Architecture & Data Flow

### 2.1 JS Interop & Google Maps Integration
- **`googleMap.js` Changes**:
  - Add a new function `getTransitRoutes(origin, destination, mode)` that wraps the Google Directions API call.
  - Request `provideRouteAlternatives: true`.
  - Instead of calling `directionsRenderer.setDirections()` immediately, the JS function will serialize the `DirectionsResult` into a simplified JSON object (containing route summaries, polylines, duration, distance, and a list of transit steps).
  - Return this JSON back to Blazor (C#).
  - Add a function `drawRoute(routeIndex)` or `drawPolyline(encodedPath)` to allow C# to instruct JS to render the user's selected route.

### 2.2 LiveRoutingService (C#)
- Create a new service `LiveRoutingService` or `LiveRouteEnrichmentService`.
- **Inputs**: The JSON payload of route alternatives from JS, and the current list of active GTFS vehicles.
- **Logic**:
  1. For each route alternative, iterate over its legs and steps.
  2. For any step where `travel_mode == "TRANSIT"` and `vehicle.type` is Bus/Train:
     - Extract `line.short_name` (e.g., "T781") and `departure_stop.location` (Lat/Lng).
     - Query the live GTFS vehicle data for vehicles matching this route name (using fuzzy/substring matching to handle naming discrepancies between Google and `data.gov.my`).
     - Calculate the straight-line distance from each matching live vehicle to the `departure_stop` location.
     - Determine the nearest bus and estimate its arrival time.
  3. Re-calculate the total route ETA by combining Google's base travel time with the estimated wait time for the first bus.
- **Output**: A collection of `EnrichedRoute` objects, sorted by the new live ETA.

### 2.3 UI Components (Map.razor)
- Add a new "Route Options" panel/bottom sheet to display the `EnrichedRoute` objects.
- Each card will show:
  - Route name/interchanges.
  - Original Google Maps duration.
  - Live wait time / nearest bus distance.
  - Total adjusted ETA.
- Clicking a card calls the JS `drawRoute` function to render that specific polyline on the map.

## 3. Handling Edge Cases
- **No live buses found**: If a transit step has no matching live buses in the GTFS feed, we fall back to the original Google Maps ETA and display a "No live data" badge for that route.
- **Matching Discrepancies**: Use case-insensitive string matching and ignore spaces when comparing Google's `line.short_name` to GTFS `route_id` or `trip_id`.

## 4. Testing & Verification
- Verify that requesting a route returning multiple options correctly lists them in the UI.
- Verify that a route with a bus physically closer to the departure stop receives a better "live score" than one further away.
- Verify that clicking different options updates the map polyline without re-fetching directions from Google.

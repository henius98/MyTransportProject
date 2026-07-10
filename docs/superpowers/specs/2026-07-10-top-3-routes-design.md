# Top 3 Routes Visualization & Interactivity Design

## Overview
Currently, the live routing algorithm fetches route alternatives and ranks them, but only lists them in the side panel. The map remains empty until the user manually clicks "Draw Route". The goal is to immediately draw the top 3 routes on the map upon calculation, distinguishing the primary route visually from the alternatives, and allowing users to seamlessly switch between routes by clicking the lines directly on the map or by clicking the cards in the panel.

## Visual Representation
- **Primary Route (Top 1):** Drawn prominently using a thick stroke (weight: 5) and an active color (e.g., `#4285F4` Blue, or `#0F9D58` Green if it has a live bus).
- **Alternative Routes (Top 2 & 3):** Drawn less prominently using a thinner stroke (weight: 3) and a subdued color (e.g., `#808080` Grey).
- **Z-Index:** The primary route will be drawn last (or with a higher z-index) so it renders on top of the alternative routes, ensuring clicks hit the primary route first if paths overlap.

## Interactivity & Data Flow
1. **Drawing the Routes:**
   - Instead of `drawEncodedPath(string)`, JS will expose a new method `drawMultipleRoutes(routesJson, dotNetRef)`.
   - Blazor will serialize the top 3 route objects (including their `RouteId`, `EncodedPolyline`, and `IsActive` state) and pass them to JS.
2. **Clicking a Route on the Map:**
   - JS will attach a `click` event listener to each Google Maps `Polyline`.
   - When an alternative route polyline is clicked:
     - JS invokes a C# method `OnMapRouteClicked(routeId)` via the provided `DotNetObjectReference`.
     - Blazor handles the callback, updates its internal `_activeRouteId` state, triggers a UI re-render (highlighting the newly selected card), and calls back into JS to update the active polyline styles.
3. **Clicking a Card in the Panel:**
   - The user can still click a card in the side panel.
   - This sets the `_activeRouteId` in Blazor and calls JS to update the polyline styles to reflect the new active route.

## Component Changes

### 1. `googleMap.js`
- Remove/deprecate single `drawEncodedPath`.
- Introduce `drawRouteOptions(routes, dotNetRef)`.
- `routes` will be an array of `{ id, path, isActive, hasLiveBus }`.
- Add `click` listeners to the polylines to call `dotNetRef.invokeMethodAsync('OnRouteSelected', id)`.
- Add `setActiveRoute(id)` to update polyline styles (stroke weight, color, z-index) without redrawing from scratch.

### 2. `Map.razor` & `Map.razor.cs`
- Add `_activeRouteId` state to track which route is currently active.
- Create a `[JSInvokable]` method `OnRouteSelected(int routeId)` to handle map clicks.
- Pass `DotNetObjectReference.Create(this)` to JS when drawing the routes.
- Update the UI template to apply an active styling class (e.g., `border-primary` and slightly larger scale) to the card that matches `_activeRouteId`.

## Constraints & Edge Cases
- **Overlapping Paths:** Google Maps handles overlapping polylines. To ensure the active route is always clickable and visible, we must assign a higher `zIndex` to the active polyline.
- **Cleanup:** We must correctly dispose of the `DotNetObjectReference` when the map is cleared or the component is destroyed to prevent memory leaks in Blazor WASM.
- **Empty States:** If less than 3 routes are returned, it handles 1 or 2 gracefully.

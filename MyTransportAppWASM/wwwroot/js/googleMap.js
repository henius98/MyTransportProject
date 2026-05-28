let mapsApiPromise = null;
let map, userMarker, infoWindow;
let storedApiKey = null;
const activeMarkers = Object.create(null);
const autocompletes = Object.create(null);

// Route rendering state (replaces legacy DirectionsRenderer)
let routePolylines = [];
let routeMarkers = [];



export async function loadGoogleMaps(apiKey) {
	if (apiKey) storedApiKey = apiKey;
	if (mapsApiPromise) return mapsApiPromise;
	if (window.google?.maps?.importLibrary) return Promise.resolve();

	const key = storedApiKey || apiKey;
	if (!key) throw new Error("Google Maps API key not provided.");

	mapsApiPromise = new Promise((resolve, reject) => {
		const script = document.createElement("script");
		script.src = `https://maps.googleapis.com/maps/api/js?key=${key}&v=weekly&libraries=places&loading=async`;
		script.async = true;
		script.defer = true;

		script.onload = () => {
			const waitUntilReady = () => {
				if (window.google?.maps?.importLibrary) resolve();
				else setTimeout(waitUntilReady, 25);
			};
			waitUntilReady();
		};

		script.onerror = () => {
			mapsApiPromise = null;
			reject(new Error("Failed to load Google Maps script"));
		};

		document.head.appendChild(script);
	});

	return mapsApiPromise;
}

export async function initGoogleMaps(elementId, lat, lng, zoom = 13, apiKey, dotNetHelper, theme = 'DARK') {
	await loadGoogleMaps(apiKey);

	const el = document.getElementById(elementId);
	if (!el) throw new Error(`Element #${elementId} not found.`);

	// Using modern importLibrary pattern
	const [{ Map, InfoWindow }, { AdvancedMarkerElement, PinElement }] = await Promise.all([
		google.maps.importLibrary("maps"),
		google.maps.importLibrary("marker"),
	]);

	map = new Map(el, {
		center: { lat, lng },
		zoom,
		mapId: "7b39cc20de6bf1d7bcbd1c11", // Required for AdvancedMarkerElement
		colorScheme: theme.toUpperCase(),
		mapTypeControl: false,
		streetViewControl: false,
		fullscreenControl: false,
	});

	infoWindow = new InfoWindow({ disableAutoPan: true, headerDisabled: true });

	// Close infoWindow when clicking anywhere else on the map
	map.addListener("click", () => {
		infoWindow.close();
	});

	const pin = document.createElement("div");
	pin.innerHTML = "🚶";
	pin.style.fontSize = "33px";

	userMarker = new AdvancedMarkerElement({
		position: { lat, lng },
		map,
		title: "Me",
		gmpDraggable: true,
		content: pin,
	});

	// Add dragend listener to update origin in Blazor
	userMarker.addListener("dragend", async () => {
		const newPos = userMarker.position;
		try {
			const address = await reverseGeocode(newPos.lat, newPos.lng);
			if (dotNetHelper) {
				await dotNetHelper.invokeMethodAsync("UpdateOrigin", address);
				await dotNetHelper.invokeMethodAsync("UpdateUserPosition", newPos.lat, newPos.lng);
				
				// Update the origin autocomplete widget value visually
				setAutocompleteValue("origin-input", address);
			}
		} catch (err) {
			console.error("Dragend geocode failed:", err);
		}
	});

	return map;
}

export async function setMapTheme(theme) {
	if (map) {
		map.setOptions({ colorScheme: theme.toUpperCase() });
	}
}

/**
 * Initializes Autocomplete using the new PlaceAutocompleteElement (replaces legacy Autocomplete widget).
 * The new widget is a custom HTML element appended inside the container identified by elementId.
 */
export async function initAutocomplete(elementId, dotNetHelper, methodName, apiKey) {
	await loadGoogleMaps(apiKey);
	const { PlaceAutocompleteElement } = await google.maps.importLibrary("places");

	const container = document.getElementById(elementId);
	if (!container) return;

	// Create the new PlaceAutocompleteElement widget
	const placeAutocomplete = new PlaceAutocompleteElement({});

	// Style the widget to fill its parent container
	placeAutocomplete.style.width = "100%";

	// Clear the container and append the new element
	container.innerHTML = "";
	container.appendChild(placeAutocomplete);
	autocompletes[elementId] = placeAutocomplete;

	// Listen for place selection via the new gmp-select event
	placeAutocomplete.addEventListener("gmp-select", async ({ placePrediction }) => {
		const place = placePrediction.toPlace();
		await place.fetchFields({ fields: ["displayName", "formattedAddress", "location"] });

		const displayText = place.displayName || place.formattedAddress;

		if (dotNetHelper) {
			dotNetHelper.invokeMethodAsync(methodName, displayText);

			if (methodName === "UpdateOrigin" && place.location) {
				const lat = place.location.lat();
				const lng = place.location.lng();
				updateUserMarker(lat, lng);
				dotNetHelper.invokeMethodAsync("UpdateUserPosition", lat, lng);
			}
		}
	});
}

/**
 * Updates the text value of a PlaceAutocompleteElement widget.
 */
export function setAutocompleteValue(elementId, value) {
	const widget = autocompletes[elementId];
	if (widget) {
		widget.value = value || "";
		
		// Also find the internal input and trigger an input event if needed
		// although setting .value on the web component should suffice for display.
		const internalInput = widget.querySelector('input');
		if (internalInput) {
			internalInput.value = value || "";
		}
	}
}

export async function updateUserMarker(lat, lng) {
	try {
		await loadGoogleMaps();
		if (!map || !userMarker) return;
		userMarker.position = { lat, lng };
		map.panTo({ lat, lng });
	} catch (err) {
		console.error("updateUserMarker err: " + err)
	}
}

/**
 * Clears any previously rendered route polylines and markers from the map.
 */
export function clearRoute() {
	routePolylines.forEach(p => p.setMap(null));
	routePolylines = [];
	routeMarkers.forEach(m => (m.map = null));
	routeMarkers = [];
}

/**
 * Computes and renders a route using the new Routes library (Route.computeRoutes).
 * Replaces legacy DirectionsService.route() + DirectionsRenderer.setDirections().
 */
export async function showRoute(origin, destination, travelMode = "TRANSIT") {
	await loadGoogleMaps();
	if (!map) throw new Error("Map not initialized.");

	const { Route } = await google.maps.importLibrary("routes");

	// Clear any existing route before drawing a new one
	clearRoute();

	const request = {
		origin,
		destination,
		travelMode,
		fields: ["path"],
	};

	const { routes } = await Route.computeRoutes(request);

	if (!routes || routes.length === 0) {
		throw new Error("No routes found.");
	}

	// Draw polylines on the map
	routePolylines = routes[0].createPolylines();
	routePolylines.forEach(polyline => {
		polyline.setOptions({
			strokeColor: "#4285F4",
			strokeWeight: 5,
		});
		polyline.setMap(map);
	});

	// Draw origin/destination markers
	try {
		const { PinElement, AdvancedMarkerElement } = await google.maps.importLibrary("marker");
		const route = routes[0];
		routeMarkers = [];

		if (route.legs && route.legs.length > 0) {
			// Add Start Marker
			const startLeg = route.legs[0];
			const startPin = new PinElement({
				glyphText: 'A',
				background: '#4285F4',
				borderColor: '#FFFFFF'
			});
			const startMarker = new AdvancedMarkerElement({
				position: startLeg.startLocation.latLng,
				content: startPin,
				title: "Origin"
			});
			startMarker.map = map;
			routeMarkers.push(startMarker);

			// Add End Marker (and intermediate ones if they existed, but for now we follow the legs)
			const lastLeg = route.legs[route.legs.length - 1];
			const endPin = new PinElement({
				glyphText: 'B',
				background: '#EA4335',
				borderColor: '#FFFFFF'
			});
			const endMarker = new AdvancedMarkerElement({
				position: lastLeg.endLocation.latLng,
				content: endPin,
				title: "Destination"
			});
			endMarker.map = map;
			routeMarkers.push(endMarker);
		}
	} catch (err) {
		console.error("Manual marker creation failed:", err);
	}
}

export async function showRouteByName(originName, destinationName, travelMode = "TRANSIT") {
	await loadGoogleMaps();
	try {
		const [originLoc, destinationLoc] = await Promise.all([
			geocodePlaceName(originName),
			geocodePlaceName(destinationName)
		]);

		await showRoute(originLoc, destinationLoc, travelMode);
	} catch (err) {
		console.error("showRouteByName failed:", err);
		throw err;
	}
}

export async function geocodePlaceName(name) {
	await loadGoogleMaps();
	const { Geocoder } = await google.maps.importLibrary("geocoding");
	const geocoder = new Geocoder();

	return new Promise((resolve, reject) => {
		geocoder.geocode({ address: name }, (results, status) => {
			if (status === "OK" && results[0]) {
				resolve(results[0].geometry.location);
			} else {
				reject(new Error("Geocode failed: " + status));
			}
		});
	});
}

export async function reverseGeocode(lat, lng) {
	await loadGoogleMaps();
	const { Geocoder } = await google.maps.importLibrary("geocoding");
	const geocoder = new Geocoder();

	return new Promise((resolve, reject) => {
		geocoder.geocode({ location: { lat, lng } }, (results, status) => {
			if (status === "OK" && results[0]) {
				resolve(results[0].formatted_address);
			} else {
				reject(new Error("Reverse geocode failed: " + status));
			}
		});
	});
}

export async function syncMarkers(locations) {
	if (!map || !infoWindow) return;
	
	// Safety check for Blazor interop
	if (!locations || !Array.isArray(locations)) {
		console.warn("syncMarkers: locations is not an array", locations);
		return;
	}

	await loadGoogleMaps();
	const { AdvancedMarkerElement } = await google.maps.importLibrary("marker");

	try {
		const currentIds = new Set(locations.map(l => l.vehicleId).filter(id => id != null));

		// Remove old markers
		for (const id in activeMarkers) {
			if (!currentIds.has(id)) {
				activeMarkers[id].map = null;
				delete activeMarkers[id];
			}
		}

		locations.forEach(loc => {
			const id = loc.vehicleId;
			if (id == null) return;
			
			const pos = { lat: loc.lat, lng: loc.lng };
			
			if (activeMarkers[id]) {
				const marker = activeMarkers[id];
				marker.position = pos;

				// Convert speed if provided (GTFS-RT speed is m/s)
				const speedKmh = (loc.speed || 0) * 3.6;
				loc.displaySpeed = speedKmh > 0 ? Math.round(speedKmh) : "-";
				
				marker.busData = loc;
			} else {
				const el = document.createElement("div");
				el.textContent = loc.icon || "🚌";
				el.className = "bus-marker";
				el.style.fontSize = "28px";

				const marker = new AdvancedMarkerElement({
					position: pos,
					map,
					title: `Route ${loc.routeId || 'Unknown'} - ${id}`,
					content: el,
				});

				const speedKmh = (loc.speed || 0) * 3.6;
				loc.displaySpeed = speedKmh > 0 ? Math.round(speedKmh) : "-";
				marker.busData = loc;

				marker.addListener("gmp-click", () => {
					if (infoWindow.get("anchor") === marker) {
						infoWindow.close();
						return;
					}

					const currentLoc = marker.busData;
					const content = `
						<div class="bus-info-window">
							<div class="bus-info-header">Bus ${id}</div>
							<div class="bus-info-item"><b>Route</b> <span>${currentLoc.routeId || 'N/A'}</span></div>
							<div class="bus-info-item"><b>Speed</b> <span>${currentLoc.displaySpeed}${currentLoc.displaySpeed === "-" ? "" : " km/h"}</span></div>
							<div class="bus-info-item"><b>Updated</b> <span>${new Date(currentLoc.timestamp * 1000).toLocaleTimeString()}</span></div>
						</div>
					`;
					infoWindow.setContent(content);
					infoWindow.open({
						anchor: marker,
						map,
					});
				});

				activeMarkers[id] = marker;
			}
		});
	} catch (err) {
		console.error("syncMarkers internal error:", err);
	}
}
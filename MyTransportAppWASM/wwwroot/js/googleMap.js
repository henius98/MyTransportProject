let mapsApiPromise = null;
let map, userMarker, directionsService, directionsRenderer, infoWindow;
let storedApiKey = null;
const activeMarkers = Object.create(null);



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
	const [{ Map }, { AdvancedMarkerElement }, { InfoWindow }] = await Promise.all([
		google.maps.importLibrary("maps"),
		google.maps.importLibrary("marker"),
		google.maps.importLibrary("maps"),
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
		infoWindow.setAnchor(null);
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

	directionsService = new google.maps.DirectionsService();
	directionsRenderer = new google.maps.DirectionsRenderer({
		map,
		suppressMarkers: true,
		preserveViewport: true,
		polylineOptions: {
			strokeColor: "#4285F4",
			strokeWeight: 5,
		},
	});

	// Add dragend listener to update origin in Blazor
	userMarker.addListener("dragend", async () => {
		const newPos = userMarker.position;
		try {
			const address = await reverseGeocode(newPos.lat, newPos.lng);
			if (dotNetHelper) {
				await dotNetHelper.invokeMethodAsync("UpdateOrigin", address);
				await dotNetHelper.invokeMethodAsync("UpdateUserPosition", newPos.lat, newPos.lng);
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
 * Initializes Autocomplete using the Places (New) compatible patterns.
 */
export async function initAutocomplete(elementId, dotNetHelper, methodName, apiKey) {
	await loadGoogleMaps(apiKey);
	const { Autocomplete } = await google.maps.importLibrary("places");
	
	const input = document.getElementById(elementId);
	if (!input) return;

	// The standard Autocomplete works with Places API (New) enabled in Console
	const autocomplete = new Autocomplete(input, {
		fields: ["formatted_address", "geometry", "name"],
		strictBounds: false,
	});

	autocomplete.addListener("place_changed", () => {
		const place = autocomplete.getPlace();
		if (dotNetHelper) {
			dotNetHelper.invokeMethodAsync(methodName, place.name || place.formatted_address);

			if (methodName === "UpdateOrigin" && place.geometry && place.geometry.location) {
				const lat = place.geometry.location.lat();
				const lng = place.geometry.location.lng();
				updateUserMarker(lat, lng);
				dotNetHelper.invokeMethodAsync("UpdateUserPosition", lat, lng);
			}
		}
	});
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

export async function showRoute(origin, destination, travelMode = "TRANSIT") {
	await loadGoogleMaps();
	if (!directionsService || !directionsRenderer)
		throw new Error("Map not initialized.");

	const request = {
		origin,
		destination,
		travelMode: google.maps.TravelMode[travelMode],
	};

	const result = await directionsService.route(request);
	directionsRenderer.setDirections(result);
}

export async function showRouteByName(originName, destinationName, travelMode = "TRANSIT") {
	await loadGoogleMaps();
	const [originLoc, destinationLoc] = await Promise.all([
		geocodePlaceName(originName),
		geocodePlaceName(destinationName)
	]);

	await showRoute(originLoc, destinationLoc, travelMode);
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
	await loadGoogleMaps();
	const { AdvancedMarkerElement } = await google.maps.importLibrary("marker");

	const currentIds = new Set(locations.map(l => l.vehicleId));

	// Remove old markers
	for (const id in activeMarkers) {
		if (!currentIds.has(id)) {
			activeMarkers[id].map = null;
			delete activeMarkers[id];
		}
	}

	locations.forEach(loc => {
		const id = loc.vehicleId;
		const pos = { lat: loc.lat, lng: loc.lng };
		
		if (activeMarkers[id]) {
			const marker = activeMarkers[id];
			const oldLoc = marker.busData;
			marker.position = pos;

			// Convert speed if provided (GTFS-RT speed is m/s)
			// If speed is 0 or missing, assume not provided and show "-"
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
				title: `Route ${loc.routeId} - ${id}`,
				content: el,
			});

			// Initial speed conversion (GTFS-RT speed is m/s)
			const speedKmh = (loc.speed || 0) * 3.6;
			loc.displaySpeed = speedKmh > 0 ? Math.round(speedKmh) : "-";
			marker.busData = loc;

			marker.addListener("click", () => {
				// If the same marker is clicked again, toggle the info window
				if (infoWindow.getAnchor() === marker) {
					infoWindow.close();
					infoWindow.setAnchor(null);
					return;
				}

				const currentLoc = marker.busData;
				const content = `
					<div class="bus-info-window">
						<div class="bus-info-header">Bus ${id}</div>
						<div class="bus-info-item"><b>Route</b> <span>${currentLoc.routeId}</span></div>
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
}
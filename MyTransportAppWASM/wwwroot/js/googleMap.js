let mapsApiPromise = null;
let map, userMarker, infoWindow;
let storedApiKey = null;
const activeMarkers = Object.create(null);
const autocompletes = Object.create(null);

// Route rendering state (replaces legacy DirectionsRenderer)
let routePolylines = [];
let routeMarkers = [];
let routeOptionsMap = new Map();
let currentActiveRouteId = -1;
let locationPickerMap = null;
let locationPickerMarkers = [];



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
	const [{ Map, InfoWindow }, { AdvancedMarkerElement }] = await Promise.all([
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
	pin.textContent = "🚶";
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
	placeAutocomplete.setAttribute(
		"aria-label",
		methodName === "UpdateOrigin" ? "Origin" : "Destination",
	);

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
	routeOptionsMap.clear();
	currentActiveRouteId = -1;
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

export async function getTransitRoutes(originName, destinationName) {
	await loadGoogleMaps();
	try {
		const [originLoc, destinationLoc] = await Promise.all([
			geocodePlaceName(originName),
			geocodePlaceName(destinationName)
		]);

		const { Route } = await google.maps.importLibrary("routes");
		const { GeometryLibrary } = await google.maps.importLibrary("geometry");

		const request = {
			origin: originLoc,
			destination: destinationLoc,
			travelMode: "TRANSIT",
			computeAlternativeRoutes: true,
			fields: ["*"],
		};

		const { routes } = await Route.computeRoutes(request);

		if (!routes || routes.length === 0) {
			return [];
		}

		return routes.map((r, index) => {
			const steps = [];
			if (r.legs && r.legs.length > 0) {
				r.legs.forEach(leg => {
					if (leg.steps) {
						leg.steps.forEach(step => {
							if (step.transitDetails) {
								const td = step.transitDetails;
								steps.push({
									lineShortName: td.transitLine?.shortName || td.transitLine?.nameShort || "",
									lineName: td.transitLine?.name || "",
									departureLat: typeof td.departureStop?.location?.lat === 'function' ? td.departureStop.location.lat() : (td.departureStop?.location?.lat || 0),
									departureLng: typeof td.departureStop?.location?.lng === 'function' ? td.departureStop.location.lng() : (td.departureStop?.location?.lng || 0),
									departureStopName: td.departureStop?.name || ""
								});
							}
						});
					}
				});
			}

			let encodedPolyline = "";
			if (r.path && google.maps.geometry?.encoding) {
				encodedPolyline = google.maps.geometry.encoding.encodePath(r.path);
			}

			return {
				routeId: index,
				encodedPolyline: encodedPolyline,
				durationSeconds: r.durationMillis ? Math.round(r.durationMillis / 1000) : 0,
				distanceMeters: r.distanceMeters || 0,
				transitSteps: steps
			};
		});

	} catch (err) {
		console.error("getTransitRoutes failed:", err);
		throw err;
	}
}

export async function drawMultipleRoutes(routesData, activeRouteId, dotNetRef) {
	await loadGoogleMaps();
	if (!map) throw new Error("Map not initialized.");
	
	const { GeometryLibrary } = await google.maps.importLibrary("geometry");

	clearRoute();
	currentActiveRouteId = activeRouteId;

	const bounds = new google.maps.LatLngBounds();

	routesData.forEach(routeData => {
		if (!routeData.path) return;
		
		const path = google.maps.geometry.encoding.decodePath(routeData.path);
		
		const polyline = new google.maps.Polyline({
			path: path,
			strokeColor: "#808080",
			strokeWeight: 3,
			zIndex: 1,
			clickable: true
		});

		polyline.addListener("click", () => {
			if (currentActiveRouteId !== routeData.id) {
				setActiveRoute(routeData.id);
				dotNetRef.invokeMethodAsync('OnRouteSelected', routeData.id);
			}
		});

		polyline.setMap(map);
		routePolylines.push(polyline);
		routeOptionsMap.set(routeData.id, { polyline, hasLiveBus: routeData.hasLiveBus });
		
		path.forEach(latLng => bounds.extend(latLng));
	});

	if (routesData.length > 0) {
		map.fitBounds(bounds);
		setActiveRoute(activeRouteId);
	}
}

export function setActiveRoute(routeId) {
	currentActiveRouteId = routeId;
	
	routeOptionsMap.forEach((data, id) => {
		const isActive = (id === routeId);
		const color = isActive ? (data.hasLiveBus ? "#0F9D58" : "#4285F4") : "#808080";
		const weight = isActive ? 6 : 3;
		const zIndex = isActive ? 100 : 1;

		data.polyline.setOptions({
			strokeColor: color,
			strokeWeight: weight,
			zIndex: zIndex
		});
	});
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
		const currentIds = new Set();

		for (const loc of locations) {
			const id = loc.vehicleId;
			if (id == null) continue;
			currentIds.add(id);

			const speedKmh = (loc.speed || 0) * 3.6;
			loc.displaySpeed = speedKmh > 0 ? Math.round(speedKmh) : "-";
			
			if (activeMarkers[id]) {
				const marker = activeMarkers[id];
				const previous = marker.busData;
				if (!previous || previous.lat !== loc.lat || previous.lng !== loc.lng) {
					marker.position = { lat: loc.lat, lng: loc.lng };
				}
				marker.busData = loc;
			} else {
				const pos = { lat: loc.lat, lng: loc.lng };
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

				marker.busData = loc;

				marker._clickListener = marker.addListener("gmp-click", () => {
					if (infoWindow.get("anchor") === marker) {
						infoWindow.close();
						return;
					}

					const currentLoc = marker.busData;
					infoWindow.setContent(buildBusInfoContent(id, currentLoc));
					infoWindow.open({
						anchor: marker,
						map,
					});
				});

				activeMarkers[id] = marker;
			}
		}

		// Remove markers absent from the latest complete provider snapshot.
		for (const id in activeMarkers) {
			if (!currentIds.has(id)) {
				if (activeMarkers[id]._clickListener) {
					activeMarkers[id]._clickListener.remove();
				}
				activeMarkers[id].map = null;
				delete activeMarkers[id];
			}
		}
	} catch (err) {
		console.error("syncMarkers internal error:", err);
	}
}

function buildBusInfoContent(id, location) {
	const content = document.createElement("div");
	content.className = "bus-info-window";

	const header = document.createElement("div");
	header.className = "bus-info-header";
	header.textContent = `Bus ${id}`;
	content.appendChild(header);

	appendBusInfoRow(content, "Route", location.routeId || "N/A");
	appendBusInfoRow(
		content,
		"Speed",
		`${location.displaySpeed}${location.displaySpeed === "-" ? "" : " km/h"}`,
	);
	appendBusInfoRow(
		content,
		"Updated",
		new Date(location.timestamp * 1000).toLocaleTimeString(),
	);

	return content;
}

function appendBusInfoRow(container, label, value) {
	const row = document.createElement("div");
	row.className = "bus-info-item";

	const heading = document.createElement("b");
	heading.textContent = label;
	const text = document.createElement("span");
	text.textContent = value;

	row.append(heading, text);
	container.appendChild(row);
}
export async function showLocationPickerMap(containerId, locations, dotnetHelper, apiKey) {
    await loadGoogleMaps(apiKey);
    const { Map } = await google.maps.importLibrary("maps");
    const { AdvancedMarkerElement } = await google.maps.importLibrary("marker");

    const container = document.getElementById(containerId);
    if (!container) return;

    cleanupLocationPickerMap();
    container.replaceChildren();

    // Center map around Malaysia
    locationPickerMap = new Map(container, {
        center: { lat: 4.2105, lng: 101.9758 },
        zoom: 6,
        mapId: "LOCATION_PICKER_MAP",
        disableDefaultUI: false
    });

    locations.forEach(loc => {
        const marker = new AdvancedMarkerElement({
            position: { lat: loc.latitude, lng: loc.longitude },
            map: locationPickerMap,
            title: loc.location_name
        });

        locationPickerMarkers.push(marker);

        marker.addListener("gmp-click", () => {
            dotnetHelper.invokeMethodAsync('OnLocationSelectedFromMap', loc.latitude, loc.longitude, `MET ID: ${loc.location_id}`, loc.location_name);
        });
    });
}

export function cleanupLocationPickerMap() {
    locationPickerMarkers.forEach(marker => (marker.map = null));
    locationPickerMarkers = [];
    locationPickerMap = null;
}

export function showDialog(dialog) {
    if (dialog && typeof dialog.showModal === 'function') {
        dialog.showModal();
    }
}

export function closeDialog(dialog) {
    if (dialog && typeof dialog.close === 'function') {
        dialog.close();
    }
}

export function cleanupMap() {
	for (const id in activeMarkers) {
		if (activeMarkers[id]._clickListener) {
			activeMarkers[id]._clickListener.remove();
		}
		activeMarkers[id].map = null;
		delete activeMarkers[id];
	}
	routePolylines.forEach(p => p.setMap(null));
	routePolylines = [];
	routeMarkers.forEach(m => (m.map = null));
	routeMarkers = [];
	
	if (infoWindow) {
		infoWindow.close();
		infoWindow = null;
	}
	if (userMarker) {
		userMarker.map = null;
		userMarker = null;
	}
	map = null;

	// Fallback clear all autocompletes
	for (const id in autocompletes) {
		disposeAutocomplete(id);
	}
}

/**
 * Properly disposes of an autocomplete widget and its event listeners
 * to prevent detached DOM memory leaks.
 */
export function disposeAutocomplete(elementId) {
	if (autocompletes[elementId]) {
		const widget = autocompletes[elementId];
		// By removing the widget from the DOM and deleting the reference,
		// the browser GC will clean it up along with any attached 'gmp-select' listeners.
		if (widget && widget.parentNode) {
			widget.parentNode.removeChild(widget);
		}
		delete autocompletes[elementId];
	}
}

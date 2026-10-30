let mapsApiPromise = null;
let map, userMarker, infoWindow;
let currentInfoWindowAnchor = null;
let activeDotNetHelper = null;
let storedApiKey = null;
let mapAuthFailed = false;
const activeMarkers = Object.create(null);
const autocompletes = Object.create(null);
const advancedMarkerEventHandlers = new WeakMap();
let displayTimeZone = null;

// Global auth failure handler for Google Maps JS API
window.gm_authFailure = function () {
	mapAuthFailed = true;
	console.warn("Google Maps authentication failed (gm_authFailure triggered).");
	if (activeDotNetHelper) {
		activeDotNetHelper.invokeMethodAsync("OnMapAuthError", "Google Maps authorization failed: please verify your API key and allowed referers.")
			.catch(() => {});
	}
};
const transportMarkerTypes = {
	bus: { icon: "🚌", className: "bus-marker", label: "Bus" },
	lrt: { icon: "🚈", className: "lrt-marker", label: "LRT" },
	mrt: { icon: "🚇", className: "mrt-marker", label: "MRT" },
	ktmb: { icon: "🚆", className: "ktmb-marker", label: "KTMB" },
};
const unknownTransportMarker = { icon: "🚉", className: "unknown-marker", label: "Vehicle" };

// Route rendering state (replaces legacy DirectionsRenderer)
let routePolylines = [];
let routeMarkers = [];
let routeOptionsMap = new Map();
let currentActiveRouteId = -1;
let staticRouteLayer = null;
let staticRouteDotNetRef = null;

export function clearStaticRoute() {
	if (!staticRouteLayer) return;
	const features = [];
	staticRouteLayer.forEach(feature => features.push(feature));
	features.forEach(feature => staticRouteLayer.remove(feature));
}

export function showStaticRoute(geoJson, dotNetRef) {
	if (!map) return;
	if (!staticRouteLayer) {
		staticRouteLayer = new google.maps.Data({ map });
		staticRouteLayer.setStyle(feature => {
			if (feature.getProperty("kind") === "stop") {
				return { icon: { path: google.maps.SymbolPath.CIRCLE, scale: 5, fillColor: "#ffffff", fillOpacity: 1, strokeColor: "#1769aa", strokeWeight: 2 }, zIndex: 2 };
			}
			const color = feature.getProperty("route_color");
			return { strokeColor: typeof color === "string" && /^[0-9a-fA-F]{6}$/.test(color) ? `#${color}` : "#1769aa", strokeWeight: 4, zIndex: 1 };
		});
		staticRouteLayer.addListener("click", event => {
			if (event.feature.getProperty("kind") !== "stop") return;
			const stopId = event.feature.getProperty("stop_id");
			if (!stopId || !staticRouteDotNetRef) return;
			staticRouteDotNetRef.invokeMethodAsync("OnStaticStopSelected", stopId, event.feature.getProperty("stop_name") || stopId)
				.catch(error => console.error("Static stop selection failed:", error));
		});
	}
	clearStaticRoute();
	staticRouteDotNetRef = dotNetRef;
	const collection = JSON.parse(geoJson);
	const features = staticRouteLayer.addGeoJson(collection);
	const bounds = new google.maps.LatLngBounds();
	for (const feature of features) {
		feature.getGeometry()?.forEachLatLng(position => bounds.extend(position));
	}
	if (!bounds.isEmpty()) map.fitBounds(bounds);
}

function addAdvancedMarkerEventListener(marker, eventName, handler) {
	marker.addEventListener(eventName, handler);

	const handlers = advancedMarkerEventHandlers.get(marker) || [];
	handlers.push({ eventName, handler });
	advancedMarkerEventHandlers.set(marker, handlers);
}

function removeAdvancedMarker(marker) {
	if (!marker) return;
	const handlers = advancedMarkerEventHandlers.get(marker) || [];
	for (const { eventName, handler } of handlers) {
		try {
			marker.removeEventListener(eventName, handler);
		} catch (_) { }
	}
	advancedMarkerEventHandlers.delete(marker);
	if (currentInfoWindowAnchor === marker) {
		try {
			infoWindow?.close();
		} catch (_) { }
		currentInfoWindowAnchor = null;
	}
	try {
		if (window.google?.maps?.event?.clearInstanceListeners) {
			google.maps.event.clearInstanceListeners(marker);
		}
	} catch (_) { }
	try {
		if (typeof marker.setMap === "function") marker.setMap(null);
		marker.map = null;
	} catch (_) { }
}

export async function loadGoogleMaps(apiKey) {
	if (apiKey) storedApiKey = apiKey;
	if (mapsApiPromise) return mapsApiPromise;
	if (window.google?.maps?.importLibrary) return Promise.resolve();

	const key = storedApiKey || apiKey;
	if (!key) throw new Error("Google Maps API key not provided.");

	mapsApiPromise = new Promise((resolve, reject) => {
		const script = document.createElement("script");
		script.src = `https://maps.googleapis.com/maps/api/js?key=${key}&v=quarterly&loading=async`;
		script.async = true;
		script.defer = true;

		script.onload = () => {
			const startTime = Date.now();
			const waitUntilReady = () => {
				if (window.google?.maps?.importLibrary) {
					resolve();
				} else if (Date.now() - startTime > 15000) {
					mapsApiPromise = null;
					reject(new Error("Google Maps API initialization timed out"));
				} else {
					setTimeout(waitUntilReady, 25);
				}
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

export async function initGoogleMaps(elementId, lat, lng, zoom = 13, apiKey, dotNetHelper, theme = 'DARK', timeZone) {
	mapAuthFailed = false;
	activeDotNetHelper = dotNetHelper;
	await loadGoogleMaps(apiKey);
	if (!timeZone) throw new Error("Display time zone not provided.");
	displayTimeZone = timeZone;

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
		currentInfoWindowAnchor = null;
	});

	const pin = document.createElement("div");
	pin.textContent = "🚶";
	pin.style.fontSize = "33px";

	try {
		userMarker = new AdvancedMarkerElement({
			position: { lat, lng },
			map,
			title: "Me",
			gmpDraggable: true,
			content: pin,
		});

		// Add gmp-dragend listener to update origin in Blazor
		addAdvancedMarkerEventListener(userMarker, "gmp-dragend", async () => {
			const newPos = userMarker.position;
			const lat = typeof newPos?.lat === "function" ? newPos.lat() : newPos?.lat;
			const lng = typeof newPos?.lng === "function" ? newPos.lng() : newPos?.lng;
			try {
				const address = await reverseGeocode(lat, lng);
				if (dotNetHelper) {
					await dotNetHelper.invokeMethodAsync("UpdateOrigin", address);
					await dotNetHelper.invokeMethodAsync("UpdateUserPosition", lat, lng);
					
					// Update the origin autocomplete widget value visually
					setAutocompleteValue("origin-input", address);
				}
			} catch (err) {
				console.error("Dragend geocode failed:", err);
			}
		});
	} catch (markerErr) {
		console.warn("Could not create user marker with AdvancedMarkerElement, falling back:", markerErr);
		try {
			if (google.maps.Marker) {
				userMarker = new google.maps.Marker({
					position: { lat, lng },
					map,
					title: "Me",
					draggable: true,
					label: "🚶",
				});
				userMarker.addListener("dragend", async (e) => {
					const newLat = typeof e?.latLng?.lat === "function" ? e.latLng.lat() : e?.latLng?.lat;
					const newLng = typeof e?.latLng?.lng === "function" ? e.latLng.lng() : e?.latLng?.lng;
					try {
						const address = await reverseGeocode(newLat, newLng);
						if (dotNetHelper) {
							await dotNetHelper.invokeMethodAsync("UpdateOrigin", address);
							await dotNetHelper.invokeMethodAsync("UpdateUserPosition", newLat, newLng);
							setAutocompleteValue("origin-input", address);
						}
					} catch (err) {
						console.error("Dragend geocode failed:", err);
					}
				});
			}
		} catch (fallbackErr) {
			console.warn("User marker fallback failed:", fallbackErr);
		}
	}

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
	const container = document.getElementById(elementId);
	if (!container) return;

	try {
		await loadGoogleMaps(apiKey);
		const { PlaceAutocompleteElement } = await google.maps.importLibrary("places");

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
	} catch (err) {
		console.warn(`PlaceAutocompleteElement unavailable for ${elementId}, falling back to text input:`, err);
		container.innerHTML = "";
		const fallbackInput = document.createElement("input");
		fallbackInput.type = "text";
		fallbackInput.className = "w-100";
		fallbackInput.style.border = "none";
		fallbackInput.style.background = "transparent";
		fallbackInput.style.outline = "none";
		fallbackInput.style.color = "inherit";
		fallbackInput.style.font = "inherit";
		fallbackInput.placeholder = methodName === "UpdateOrigin" ? "Enter origin..." : "Enter destination...";
		fallbackInput.setAttribute("aria-label", methodName === "UpdateOrigin" ? "Origin" : "Destination");
		fallbackInput.addEventListener("input", async (e) => {
			if (dotNetHelper) {
				await dotNetHelper.invokeMethodAsync(methodName, e.target.value);
			}
		});
		container.appendChild(fallbackInput);
		autocompletes[elementId] = fallbackInput;
	}
}

/**
 * Updates the text value of a PlaceAutocompleteElement widget or fallback input.
 */
export function setAutocompleteValue(elementId, value) {
	const widget = autocompletes[elementId];
	if (widget) {
		if ("value" in widget) {
			widget.value = value || "";
		}
		
		// Also find the internal input and trigger an input event if needed
		// although setting .value on the web component should suffice for display.
		const internalInput = widget.querySelector ? widget.querySelector('input') : null;
		if (internalInput) {
			internalInput.value = value || "";
		}
	}
}

export async function updateUserMarker(lat, lng) {
	try {
		await loadGoogleMaps();
		if (!map || !userMarker) return;
		if (typeof userMarker.setPosition === "function") {
			userMarker.setPosition({ lat, lng });
		} else {
			userMarker.position = { lat, lng };
		}
		try {
			map.panTo?.({ lat, lng });
		} catch (_) { }
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
	routeMarkers.forEach(removeAdvancedMarker);
	routeMarkers = [];
	routeOptionsMap.clear();
	currentActiveRouteId = -1;
}

function toLatLngLiteral(pos) {
	if (!pos) return null;
	const lat = typeof pos.lat === "function" ? pos.lat() : pos.lat;
	const lng = typeof pos.lng === "function" ? pos.lng() : pos.lng;
	const numLat = Number(lat);
	const numLng = Number(lng);
	return (Number.isFinite(numLat) && Number.isFinite(numLng)) ? { lat: numLat, lng: numLng } : null;
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

	const originLoc = toLatLngLiteral(origin) || origin;
	const destinationLoc = toLatLngLiteral(destination) || destination;

	const request = {
		origin: originLoc,
		destination: destinationLoc,
		travelMode,
		fields: ["path", "legs"],
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
		const route = routes[0];
		routeMarkers = [];

		const startPos = (route.legs && route.legs.length > 0)
			? (route.legs[0].startLocation?.latLng || route.legs[0].startLocation)
			: originLoc;
		const endPos = (route.legs && route.legs.length > 0)
			? (route.legs[route.legs.length - 1].endLocation?.latLng || route.legs[route.legs.length - 1].endLocation)
			: destinationLoc;

		if (startPos && endPos) {
			let addedWithAdvanced = false;
			try {
				const { PinElement, AdvancedMarkerElement } = await google.maps.importLibrary("marker");
				if (AdvancedMarkerElement && PinElement) {
					const startPin = new PinElement({
						glyphText: 'A',
						background: '#4285F4',
						borderColor: '#FFFFFF'
					});
					const startMarker = new AdvancedMarkerElement({
						position: startPos,
						content: startPin,
						title: "Origin"
					});
					startMarker.map = map;
					routeMarkers.push(startMarker);

					const endPin = new PinElement({
						glyphText: 'B',
						background: '#EA4335',
						borderColor: '#FFFFFF'
					});
					const endMarker = new AdvancedMarkerElement({
						position: endPos,
						content: endPin,
						title: "Destination"
					});
					endMarker.map = map;
					routeMarkers.push(endMarker);
					addedWithAdvanced = true;
				}
			} catch (_) { }

			if (!addedWithAdvanced && google.maps.Marker) {
				try {
					const startMarker = new google.maps.Marker({
						position: startPos,
						map,
						title: "Origin",
						label: "A"
					});
					routeMarkers.push(startMarker);

					const endMarker = new google.maps.Marker({
						position: endPos,
						map,
						title: "Destination",
						label: "B"
					});
					routeMarkers.push(endMarker);
				} catch (_) { }
			}
		}
	} catch (err) {
		console.error("Manual marker creation failed:", err);
	}
}

export async function showRouteByName(originName, destinationName, travelMode = "TRANSIT") {
	await loadGoogleMaps();
	try {
		const [originRes, destinationRes] = await Promise.all([
			geocodePlaceName(originName),
			geocodePlaceName(destinationName)
		]);

		const originLoc = toLatLngLiteral(originRes);
		const destinationLoc = toLatLngLiteral(destinationRes);

		if (!originLoc || !destinationLoc) {
			throw new Error("Unable to resolve origin or destination location.");
		}

		await showRoute(originLoc, destinationLoc, travelMode);
	} catch (err) {
		console.error("showRouteByName failed:", err);
		throw err;
	}
}

export async function getTransitRoutes(originName, destinationName) {
	await loadGoogleMaps();
	try {
		const [originRes, destinationRes] = await Promise.all([
			geocodePlaceName(originName),
			geocodePlaceName(destinationName)
		]);

		const originLoc = toLatLngLiteral(originRes);
		const destinationLoc = toLatLngLiteral(destinationRes);

		if (!originLoc || !destinationLoc) {
			throw new Error("Unable to resolve origin or destination location.");
		}

		const { Route } = await google.maps.importLibrary("routes");
		const { GeometryLibrary } = await google.maps.importLibrary("geometry");

		const request = {
			origin: originLoc,
			destination: destinationLoc,
			travelMode: "TRANSIT",
			computeAlternativeRoutes: true,
			fields: ["path", "durationMillis", "distanceMeters", "legs.steps.transitDetails"],
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


export async function geocodePlaceName(name, apiKey) {
	if (!name || typeof name !== "string" || !name.trim()) return null;
	await loadGoogleMaps(apiKey);
	const { Geocoder } = await google.maps.importLibrary("geocoding");
	const geocoder = new Geocoder();

	return new Promise((resolve, reject) => {
		geocoder.geocode({ address: name }, (results, status) => {
			if (status === "OK" && results && results.length > 0) {
				const loc = results[0].geometry.location;
				const lat = typeof loc?.lat === "function" ? loc.lat() : loc?.lat;
				const lng = typeof loc?.lng === "function" ? loc.lng() : loc?.lng;
				resolve({
					lat,
					lng,
					formattedAddress: results[0].formatted_address || ""
				});
			} else if (status === "ZERO_RESULTS" || (status === "OK" && (!results || results.length === 0))) {
				resolve(null);
			} else {
				reject(new Error("Geocode failed: " + status));
			}
		});
	});
}

export async function reverseGeocode(lat, lng, apiKey) {
	const numLat = Number(lat);
	const numLng = Number(lng);
	if (!Number.isFinite(numLat) || !Number.isFinite(numLng)) return null;
	await loadGoogleMaps(apiKey);
	const { Geocoder } = await google.maps.importLibrary("geocoding");
	const geocoder = new Geocoder();

	return new Promise((resolve, reject) => {
		geocoder.geocode({ location: { lat: numLat, lng: numLng } }, (results, status) => {
			if (status === "OK" && results && results.length > 0) {
				resolve(results[0].formatted_address);
			} else if (status === "ZERO_RESULTS" || (status === "OK" && (!results || results.length === 0))) {
				resolve(null);
			} else {
				reject(new Error("Reverse geocode failed: " + status));
			}
		});
	});
}

function getTransportMarker(location) {
	const type = typeof location.transportType === "string" ? location.transportType.trim().toLowerCase() : "";
	return Object.hasOwn(transportMarkerTypes, type) ? transportMarkerTypes[type] : unknownTransportMarker;
}

function updateTransportMarkerElement(element, location) {
	const markerType = getTransportMarker(location);
	element.textContent = location.icon || markerType.icon;
	element.className = `transport-marker ${markerType.className}`;
}

export async function syncMarkers(locations) {
	if (!map || !infoWindow || mapAuthFailed) return;
	
	// Safety check for Blazor interop
	if (!locations || !Array.isArray(locations)) {
		console.warn("syncMarkers: locations is not an array", locations);
		return;
	}

	try {
		await loadGoogleMaps();
		const { AdvancedMarkerElement } = await google.maps.importLibrary("marker");
		const currentIds = new Set();

		for (const loc of locations) {
			const id = loc.vehicleId;
			if (id == null || !Number.isFinite(loc.lat) || !Number.isFinite(loc.lng)) {
				console.warn("syncMarkers: skipping invalid vehicle location", loc);
				continue;
			}
			const markerKey = loc.providerName ? `${loc.providerName}:${id}` : String(id);
			currentIds.add(markerKey);

			const speedKmh = (loc.speed || 0) * 3.6;
			loc.displaySpeed = speedKmh > 0 ? Math.round(speedKmh) : "-";
			const title = `Route ${loc.routeId || 'Unknown'} - ${id}`;
			
			if (activeMarkers[markerKey]) {
				const marker = activeMarkers[markerKey];
				try {
					if (typeof marker.setPosition === "function") {
						marker.setPosition({ lat: loc.lat, lng: loc.lng });
						if (typeof marker.setTitle === "function") marker.setTitle(title);
					} else {
						const previous = marker.busData;
						if (!previous || previous.lat !== loc.lat || previous.lng !== loc.lng) {
							marker.position = { lat: loc.lat, lng: loc.lng };
						}
						marker.title = title;
						if (marker.content) {
							updateTransportMarkerElement(marker.content, loc);
						}
					}
					marker.busData = loc;
				} catch (updateErr) {
					console.warn("syncMarkers: failed to update marker position:", markerKey, updateErr);
				}
			} else {
				const pos = { lat: loc.lat, lng: loc.lng };
				let marker = null;

				try {
					const el = document.createElement("div");
					updateTransportMarkerElement(el, loc);
					el.style.fontSize = "28px";

					marker = new AdvancedMarkerElement({
						position: pos,
						map,
						title,
						content: el,
						gmpClickable: true,
					});

					marker.busData = loc;

					addAdvancedMarkerEventListener(marker, "gmp-click", () => {
						if (currentInfoWindowAnchor === marker) {
							infoWindow.close();
							currentInfoWindowAnchor = null;
							return;
						}

						const currentLoc = marker.busData;
						infoWindow.setContent(buildVehicleInfoContent(id, currentLoc));
						infoWindow.open({
							anchor: marker,
							map,
						});
						currentInfoWindowAnchor = marker;
					});

					activeMarkers[markerKey] = marker;
				} catch (markerErr) {
					// AdvancedMarkerElement failed; attempt fallback to google.maps.Marker
					try {
						if (google.maps.Marker) {
							const markerType = getTransportMarker(loc);
							marker = new google.maps.Marker({
								position: pos,
								map,
								title,
								label: {
									text: loc.icon || markerType.icon,
									fontSize: "20px",
								},
							});
							marker.busData = loc;
							marker.addListener("click", () => {
								if (currentInfoWindowAnchor === marker) {
									infoWindow.close();
									currentInfoWindowAnchor = null;
									return;
								}
								const currentLoc = marker.busData;
								infoWindow.setContent(buildVehicleInfoContent(id, currentLoc));
								infoWindow.open({
									anchor: marker,
									map,
								});
								currentInfoWindowAnchor = marker;
							});
							activeMarkers[markerKey] = marker;
						}
					} catch (fallbackErr) {
						console.warn("syncMarkers: both AdvancedMarkerElement and fallback failed:", fallbackErr);
					}
				}
			}
		}

		// Remove markers absent from the latest complete provider snapshot.
		for (const id in activeMarkers) {
			if (!currentIds.has(id)) {
				removeAdvancedMarker(activeMarkers[id]);
				delete activeMarkers[id];
			}
		}
	} catch (err) {
		console.error("syncMarkers internal error:", err);
	}
}

function buildVehicleInfoContent(id, location) {
	const content = document.createElement("div");
	content.className = "bus-info-window";

	const header = document.createElement("div");
	header.className = "bus-info-header";
	header.textContent = `${getTransportMarker(location).label} ${id}`;
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
		new Date(location.timestamp * 1000).toLocaleTimeString(undefined, {
			timeZone: displayTimeZone,
		}),
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
export function cleanupMap() {
	clearStaticRoute();
	if (staticRouteLayer) {
		staticRouteLayer.setMap(null);
		staticRouteLayer = null;
		staticRouteDotNetRef = null;
	}
	for (const id in activeMarkers) {
		removeAdvancedMarker(activeMarkers[id]);
		delete activeMarkers[id];
	}
	routePolylines.forEach(p => p.setMap(null));
	routePolylines = [];
	routeMarkers.forEach(removeAdvancedMarker);
	routeMarkers = [];
	
	if (infoWindow) {
		infoWindow.close();
		infoWindow = null;
	}
	if (userMarker) {
		removeAdvancedMarker(userMarker);
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

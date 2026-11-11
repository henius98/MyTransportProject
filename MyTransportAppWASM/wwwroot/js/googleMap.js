let mapsApiPromise = null;
let storedApiKey = null;
let cachedGeocoder = null;
let map, userMarker, infoWindow;
let currentInfoWindowAnchor = null;
let activeDotNetHelper = null;
let mapAuthFailed = false;
const activeMarkers = Object.create(null);
const autocompletes = Object.create(null);
const pendingAutocompleteValues = Object.create(null);
const advancedMarkerEventHandlers = new WeakMap();
let displayTimeZone = null;
let pendingUserPosition = null;

function ensureGoogleMapsBootstrap(key) {
	if (window.google?.maps?.importLibrary) return;
	(g => {
		var h, a, k, p = "The Google Maps JavaScript API", c = "google", l = "importLibrary", q = "__ib__", m = document, b = window;
		b = b[c] || (b[c] = {});
		var d = b.maps || (b.maps = {}), r = new Set, e = new URLSearchParams, u = () => h || (h = new Promise(async (f, n) => {
			await (a = m.createElement("script"));
			e.set("libraries", [...r] + "");
			for (k in g) e.set(k.replace(/[A-Z]/g, t => "_" + t[0].toLowerCase()), g[k]);
			e.set("callback", c + ".maps." + q);
			a.src = `https://maps.${c}apis.com/maps/api/js?` + e;
			d[q] = f;
			a.onerror = () => h = n(Error(p + " could not load."));
			a.nonce = m.querySelector("script[nonce]")?.nonce || "";
			(m.head || m.body || m.documentElement).append(a);
		}));
		d[l] ? console.warn(p + " only loads once. Ignoring:", g) : d[l] = (f, ...n) => r.add(f) && u().then(() => d[l](f, ...n));
	})({
		key,
		v: "weekly",
		loading: "async",
	});
}

export async function loadGoogleMaps(apiKey) {
	if (apiKey) storedApiKey = apiKey;
	if (mapsApiPromise) return mapsApiPromise;
	if (window.google?.maps?.Map) return Promise.resolve();

	const key = storedApiKey || apiKey;
	if (!key) throw new Error("Google Maps API key not provided.");

	ensureGoogleMapsBootstrap(key);
	mapsApiPromise = google.maps.importLibrary("maps").catch(err => {
		mapsApiPromise = null;
		if (!window.google?.maps?.Map) {
			try {
				delete window.google?.maps?.importLibrary;
				delete window.google?.maps?.__ib__;
			} catch (_) { }
		}
		throw err;
	});

	return mapsApiPromise;
}

export function formatDisplayName(displayName) {
	if (!displayName) return "";
	if (typeof displayName === "string") return displayName;
	if (typeof displayName.text === "string") return displayName.text;
	return String(displayName);
}

async function getGeocoder(apiKey) {
	if (cachedGeocoder) return cachedGeocoder;
	await loadGoogleMaps(apiKey);
	const { Geocoder } = await google.maps.importLibrary("geocoding");
	cachedGeocoder = new Geocoder();
	return cachedGeocoder;
}

async function callGeocoder(geocoder, request) {
	return new Promise((resolve, reject) => {
		try {
			const ret = geocoder.geocode(request, (results, status) => {
				if (status === "OK" && results && results.length > 0) {
					resolve(results);
				} else if (status === "ZERO_RESULTS" || (status === "OK" && (!results || results.length === 0))) {
					resolve([]);
				} else {
					reject(new Error("Geocode failed: " + status));
				}
			});
			if (ret && typeof ret.then === "function") {
				ret.then(
					response => resolve(response?.results ?? []),
					err => {
						const msg = String(err?.message || err);
						if (msg.includes("ZERO_RESULTS")) resolve([]);
						else reject(err);
					}
				);
			}
		} catch (err) {
			reject(err);
		}
	});
}

export async function geocodePlaceName(name, apiKey) {
	if (!name || typeof name !== "string" || !name.trim()) return null;
	try {
		const geocoder = await getGeocoder(apiKey);
		const results = await callGeocoder(geocoder, {
			address: name.trim(),
			region: "my",
		});
		if (results && results.length > 0) {
			const loc = results[0].geometry?.location;
			const lat = typeof loc?.lat === "function" ? loc.lat() : loc?.lat;
			const lng = typeof loc?.lng === "function" ? loc.lng() : loc?.lng;
			return {
				lat,
				lng,
				formattedAddress: results[0].formatted_address || ""
			};
		}
		return null;
	} catch (err) {
		const msg = String(err?.message || err);
		if (msg.includes("ZERO_RESULTS")) return null;
		throw err;
	}
}

export async function reverseGeocode(lat, lng, apiKey) {
	const numLat = Number(lat);
	const numLng = Number(lng);
	if (!Number.isFinite(numLat) || !Number.isFinite(numLng) || numLat < -90 || numLat > 90 || numLng < -180 || numLng > 180) {
		return null;
	}
	try {
		const geocoder = await getGeocoder(apiKey);
		const results = await callGeocoder(geocoder, {
			location: { lat: numLat, lng: numLng },
		});
		if (results && results.length > 0) {
			return results[0].formatted_address || null;
		}
		return null;
	} catch (err) {
		const msg = String(err?.message || err);
		if (msg.includes("ZERO_RESULTS")) return null;
		throw err;
	}
}

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

export async function showStaticRoute(geoJson, dotNetRef) {
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
	const { LatLngBounds } = await google.maps.importLibrary("core");
	const bounds = new LatLngBounds();
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


export async function initGoogleMaps(elementId, lat, lng, zoom = 13, apiKey, dotNetHelper, theme = 'DARK', timeZone) {
	mapAuthFailed = false;
	activeDotNetHelper = dotNetHelper;
	pendingUserPosition = null;
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

	if (pendingUserPosition) {
		lat = pendingUserPosition.lat;
		lng = pendingUserPosition.lng;
		pendingUserPosition = null;
	}

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

	async function handleUserMarkerDragEnd(rawPos) {
		const coords = toLatLngLiteral(rawPos);
		if (!coords) return;
		try {
			const address = await reverseGeocode(coords.lat, coords.lng);
			if (dotNetHelper) {
				await dotNetHelper.invokeMethodAsync("UpdateOrigin", address);
				await dotNetHelper.invokeMethodAsync("UpdateUserPosition", coords.lat, coords.lng);

				// Update the origin autocomplete widget value visually
				setAutocompleteValue("origin-input", address);
			}
		} catch (err) {
			console.error("Dragend geocode failed:", err);
		}
	}

	try {
		userMarker = new AdvancedMarkerElement({
			position: { lat, lng },
			map,
			title: "Me",
			gmpDraggable: true,
			content: pin,
		});

		// Add gmp-dragend listener to update origin in Blazor
		addAdvancedMarkerEventListener(userMarker, "gmp-dragend", () => handleUserMarkerDragEnd(userMarker.position));
	} catch (markerErr) {
		console.debug("Could not create user marker with AdvancedMarkerElement, falling back:", markerErr);
		try {
			if (google.maps.Marker) {
				userMarker = new google.maps.Marker({
					position: { lat, lng },
					map,
					title: "Me",
					draggable: true,
					label: "🚶",
				});
				userMarker.addListener("dragend", (e) => handleUserMarkerDragEnd(e?.latLng));
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
	const placeholderText = methodName === "UpdateOrigin" ? "Enter origin..." : "Enter destination...";

	try {
		await loadGoogleMaps(apiKey);
		const { PlaceAutocompleteElement } = await google.maps.importLibrary("places");

		// Create the new PlaceAutocompleteElement widget
		const placeAutocomplete = new PlaceAutocompleteElement({
			requestedRegion: "my",
			includedRegionCodes: ["my"],
		});
		placeAutocomplete.id = `${elementId}-field`;
		placeAutocomplete.name = `${elementId}-field`;
		placeAutocomplete.setAttribute(
			"aria-label",
			methodName === "UpdateOrigin" ? "Origin" : "Destination",
		);
		placeAutocomplete.setAttribute("aria-labelledby", `${elementId}-label`);
		placeAutocomplete.setAttribute("placeholder", placeholderText);

		// Style the widget to fill its parent container
		placeAutocomplete.style.width = "100%";

		// Clear the container and append the new element
		container.innerHTML = "";
		container.appendChild(placeAutocomplete);
		autocompletes[elementId] = placeAutocomplete;
		if (Object.hasOwn(pendingAutocompleteValues, elementId)) {
			setAutocompleteValue(elementId, pendingAutocompleteValues[elementId]);
		}

		// Listen for place selection via the new gmp-select event
		placeAutocomplete.addEventListener("gmp-select", async ({ placePrediction }) => {
			if (!placePrediction) return;
			try {
				const place = placePrediction.toPlace();
				await place.fetchFields({ fields: ["displayName", "formattedAddress", "location"] });

				const displayText = formatDisplayName(place.displayName) || place.formattedAddress || "";

				if (dotNetHelper) {
					dotNetHelper.invokeMethodAsync(methodName, displayText);

					if (methodName === "UpdateOrigin" && place.location) {
						const lat = typeof place.location.lat === "function" ? place.location.lat() : place.location.lat;
						const lng = typeof place.location.lng === "function" ? place.location.lng() : place.location.lng;
						if (Number.isFinite(lat) && Number.isFinite(lng)) {
							updateUserMarker(lat, lng);
							dotNetHelper.invokeMethodAsync("UpdateUserPosition", lat, lng);
						}
					}
				}
			} catch (err) {
				console.error("Place selection failed:", err);
			}
		});
	} catch (err) {
		console.debug(`PlaceAutocompleteElement unavailable for ${elementId}, falling back to text input:`, err);
		container.innerHTML = "";
		const fallbackInput = document.createElement("input");
		fallbackInput.type = "text";
		fallbackInput.id = `${elementId}-field`;
		fallbackInput.name = `${elementId}-field`;
		fallbackInput.className = "w-100";
		fallbackInput.style.border = "none";
		fallbackInput.style.background = "transparent";
		fallbackInput.style.outline = "none";
		fallbackInput.style.color = "inherit";
		fallbackInput.style.font = "inherit";
		fallbackInput.placeholder = placeholderText;
		fallbackInput.setAttribute("aria-label", methodName === "UpdateOrigin" ? "Origin" : "Destination");
		fallbackInput.setAttribute("aria-labelledby", `${elementId}-label`);
		fallbackInput.addEventListener("input", async (e) => {
			if (dotNetHelper) {
				await dotNetHelper.invokeMethodAsync(methodName, e.target.value);
			}
		});
		container.appendChild(fallbackInput);
		autocompletes[elementId] = fallbackInput;
		if (Object.hasOwn(pendingAutocompleteValues, elementId)) {
			setAutocompleteValue(elementId, pendingAutocompleteValues[elementId]);
		}
	}
}

/**
 * Updates the text value of a PlaceAutocompleteElement widget or fallback input.
 */
export function setAutocompleteValue(elementId, value) {
	pendingAutocompleteValues[elementId] = value || "";
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
	pendingUserPosition = { lat, lng };
	try {
		await loadGoogleMaps();
		if (mapAuthFailed || !map || !userMarker) return;
		pendingUserPosition = null;
		try {
			if (typeof userMarker.setPosition === "function") {
				userMarker.setPosition({ lat, lng });
			} else {
				userMarker.position = { lat, lng };
			}
		} catch (posErr) {
			console.debug("Could not update user marker position:", posErr);
		}
		try {
			map.panTo?.({ lat, lng });
		} catch (_) { }
	} catch (err) {
		console.error("updateUserMarker err: " + err);
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

function normalizeRoutesTravelMode(mode) {
	const m = (mode || "").toUpperCase();
	if (m === "DRIVING" || m === "DRIVE") return "DRIVE";
	if (m === "WALKING" || m === "WALK") return "WALK";
	if (m === "BICYCLING" || m === "BICYCLE") return "BICYCLE";
	if (m === "TRANSIT") return "TRANSIT";
	return m || "TRANSIT";
}

/**
 * Computes and renders a route using the new Routes library (Route.computeRoutes).
 * Replaces legacy DirectionsService.route() + DirectionsRenderer.setDirections().
 */
async function showRoute(origin, destination, travelMode = "TRANSIT") {
	await loadGoogleMaps();
	if (!map) throw new Error("Map not initialized.");

	const { Route } = await google.maps.importLibrary("routes");

	// Clear any existing route before drawing a new one
	clearRoute();

	const originLoc = toLatLngLiteral(origin) || origin;
	const destinationLoc = toLatLngLiteral(destination) || destination;

	const routesTravelMode = normalizeRoutesTravelMode(travelMode);
	const request = {
		origin: originLoc,
		destination: destinationLoc,
		travelMode: routesTravelMode,
		fields: ["path", "legs", "viewport"],
	};

	const { routes } = await Route.computeRoutes(request);

	if (!routes || routes.length === 0) {
		throw new Error("No routes found.");
	}

	const route = routes[0];

	// Draw polylines on the map
	routePolylines = route.createPolylines();
	routePolylines.forEach(polyline => {
		polyline.setOptions({
			strokeColor: "#4285F4",
			strokeWeight: 5,
		});
		polyline.setMap(map);
	});

	// Draw origin/destination markers
	try {
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

	// Fit map bounds to the route
	if (route.viewport) map.fitBounds(route.viewport);
}

async function resolveRouteEndpoints(originName, destinationName) {
	const [originRes, destinationRes] = await Promise.all([
		geocodePlaceName(originName),
		geocodePlaceName(destinationName)
	]);

	const originLoc = toLatLngLiteral(originRes);
	const destinationLoc = toLatLngLiteral(destinationRes);

	if (!originLoc || !destinationLoc) {
		throw new Error("Unable to resolve origin or destination location.");
	}

	return { originLoc, destinationLoc };
}

export async function showRouteByName(originName, destinationName, travelMode = "TRANSIT") {
	await loadGoogleMaps();
	try {
		const { originLoc, destinationLoc } = await resolveRouteEndpoints(originName, destinationName);
		await showRoute(originLoc, destinationLoc, travelMode);
	} catch (err) {
		console.error("showRouteByName failed:", err);
		throw err;
	}
}

export async function getTransitRoutes(originName, destinationName) {
	await loadGoogleMaps();
	try {
		const { originLoc, destinationLoc } = await resolveRouteEndpoints(originName, destinationName);

		const { Route } = await google.maps.importLibrary("routes");
		const { encoding } = await google.maps.importLibrary("geometry");

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
			if (r.path) {
				encodedPolyline = encoding.encodePath(r.path);
			}

			return {
				routeId: index,
				encodedPolyline,
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
	
	const [{ Polyline }, { LatLngBounds }, { encoding }] = await Promise.all([
		google.maps.importLibrary("maps"),
		google.maps.importLibrary("core"),
		google.maps.importLibrary("geometry")
	]);

	clearRoute();
	currentActiveRouteId = activeRouteId;

	const bounds = new LatLngBounds();

	routesData.forEach(routeData => {
		if (!routeData.path) return;
		
		const path = encoding.decodePath(routeData.path);
		
		const polyline = new Polyline({
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
				console.debug("syncMarkers: skipping invalid vehicle location", loc);
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
				const toggleInfoWindow = () => {
					if (currentInfoWindowAnchor === marker) {
						infoWindow.close();
						currentInfoWindowAnchor = null;
						return;
					}
					infoWindow.setContent(buildVehicleInfoContent(id, marker.busData));
					infoWindow.open({ anchor: marker, map });
					currentInfoWindowAnchor = marker;
				};

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
					addAdvancedMarkerEventListener(marker, "gmp-click", toggleInfoWindow);
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
							marker.addListener("click", toggleInfoWindow);
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
	clearRoute();
	activeDotNetHelper = null;
	if (staticRouteLayer) {
		staticRouteLayer.setMap(null);
		staticRouteLayer = null;
		staticRouteDotNetRef = null;
	}
	for (const id in activeMarkers) {
		removeAdvancedMarker(activeMarkers[id]);
		delete activeMarkers[id];
	}

	if (infoWindow) {
		infoWindow.close();
		infoWindow = null;
	}
	if (userMarker) {
		removeAdvancedMarker(userMarker);
		userMarker = null;
	}
	pendingUserPosition = null;
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
	delete pendingAutocompleteValues[elementId];
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

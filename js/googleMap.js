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
const selectedPlaceLocations = Object.create(null);
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

async function geocodeFirst(apiKey, request) {
	try {
		const geocoder = await getGeocoder(apiKey);
		const results = await callGeocoder(geocoder, request);
		return results && results.length > 0 ? results[0] : null;
	} catch (err) {
		const msg = String(err?.message || err);
		if (msg.includes("ZERO_RESULTS")) return null;
		throw err;
	}
}

export async function geocodePlaceName(name, apiKey) {
	if (!name || typeof name !== "string" || !name.trim()) return null;
	const match = await geocodeFirst(apiKey, {
		address: name.trim(),
		region: "my",
	});
	if (!match) return null;
	const loc = match.geometry?.location;
	const lat = typeof loc?.lat === "function" ? loc.lat() : loc?.lat;
	const lng = typeof loc?.lng === "function" ? loc.lng() : loc?.lng;
	return {
		lat,
		lng,
		formattedAddress: match.formatted_address || ""
	};
}

export async function reverseGeocode(lat, lng, apiKey) {
	const numLat = Number(lat);
	const numLng = Number(lng);
	if (!Number.isFinite(numLat) || !Number.isFinite(numLng) || numLat < -90 || numLat > 90 || numLng < -180 || numLng > 180) {
		return null;
	}
	const match = await geocodeFirst(apiKey, {
		location: { lat: numLat, lng: numLng },
	});
	return match?.formatted_address || null;
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
	bus: { icon: "🚌", className: "bus-marker", label: "Bus", sideFacing: true },
	lrt: { icon: "🚈", className: "lrt-marker", label: "LRT", sideFacing: true },
	mrt: { icon: "🚅", className: "mrt-marker", label: "MRT", sideFacing: true },
	ktmb: { icon: "🚄", className: "ktmb-marker", label: "KTMB", sideFacing: true },
	rail: { icon: "🚄", className: "rail-marker", label: "Rail", sideFacing: true },
};
const unknownTransportMarker = { icon: "🚉", className: "unknown-marker", label: "Vehicle", sideFacing: false };

// Route rendering state (replaces legacy DirectionsRenderer)
let routePolylines = [];
let routeMarkers = [];
let routeCalloutMarkers = [];
let routeOptionsMap = new Map();
let cachedTransitRoutes = new Map();
let renderedRoutesData = [];
let activeRouteDotNetRef = null;
let currentColorScheme = "DARK";
let currentActiveRouteId = -1;
let staticRouteLayer = null;
let staticRouteDotNetRef = null;
let transitLayer = null;

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
	currentColorScheme = (theme || "DARK").toUpperCase();
	await loadGoogleMaps(apiKey);
	if (!timeZone) throw new Error("Display time zone not provided.");
	displayTimeZone = timeZone;

	const el = document.getElementById(elementId);
	if (!el) throw new Error(`Element #${elementId} not found.`);

	// Using modern importLibrary pattern
	const [{ Map, InfoWindow, TransitLayer }, { AdvancedMarkerElement }] = await Promise.all([
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

	if (transitLayer) {
		transitLayer.setMap(null);
		transitLayer = null;
	}
	if (TransitLayer) {
		transitLayer = new TransitLayer();
		transitLayer.setMap(map);
	}

	infoWindow = new InfoWindow({ disableAutoPan: true, headerDisabled: true });

	// Close infoWindow when clicking anywhere else on the map
	map.addListener("click", () => {
		infoWindow.close();
		currentInfoWindowAnchor = null;
	});

	const pin = document.createElement("div");
	pin.textContent = "🚶";
	pin.style.fontSize = "33px";

	let latestDragSeq = 0;

	async function handleUserMarkerDragEnd(rawPos) {
		const coords = toLatLngLiteral(rawPos);
		if (!coords) return;
		const dragSeq = ++latestDragSeq;
		try {
			if (dotNetHelper) {
				await dotNetHelper.invokeMethodAsync("UpdateUserPosition", coords.lat, coords.lng);
			}
			const address = (await reverseGeocode(coords.lat, coords.lng)) || `${coords.lat.toFixed(5)}, ${coords.lng.toFixed(5)}`;
			if (dragSeq !== latestDragSeq) return;
			if (dotNetHelper) {
				await dotNetHelper.invokeMethodAsync("UpdateOrigin", address);

				// Update the origin autocomplete widget value visually while retaining dragged coordinates
				setAutocompleteValue("origin-input", address, coords);
			}
		} catch (err) {
			console.error("Dragend geocode failed:", err);
		}
	}

	userMarker = createMarkerWithFallback(
		() => {
			const marker = new AdvancedMarkerElement({
				position: { lat, lng },
				map,
				title: "Me",
				gmpDraggable: true,
				content: pin,
			});

			// Add gmp-dragend listener to update origin in Blazor
			addAdvancedMarkerEventListener(marker, "gmp-dragend", () => handleUserMarkerDragEnd(marker.position));
			return marker;
		},
		() => {
			const marker = new google.maps.Marker({
				position: { lat, lng },
				map,
				title: "Me",
				draggable: true,
				label: "🚶",
			});
			marker.addListener("dragend", (e) => handleUserMarkerDragEnd(e?.latLng));
			return marker;
		},
		"Could not create user marker with AdvancedMarkerElement, falling back:",
		"User marker fallback failed:",
	);

	return map;
}

export async function setMapTheme(theme) {
	currentColorScheme = (theme || "DARK").toUpperCase();
	if (map) {
		map.setOptions({ colorScheme: currentColorScheme });
		if (renderedRoutesData.length > 0 && currentActiveRouteId >= 0) {
			await renderMultipleRoutes(renderedRoutesData, currentActiveRouteId, activeRouteDotNetRef, false);
		}
	}
}

function createMarkerWithFallback(createAdvanced, createLegacy, fallbackDebugMessage, fallbackErrorMessage) {
	try {
		return createAdvanced();
	} catch (markerErr) {
		if (fallbackDebugMessage) console.debug(fallbackDebugMessage, markerErr);
		try {
			return google.maps.Marker ? createLegacy() : null;
		} catch (fallbackErr) {
			console.warn(fallbackErrorMessage, fallbackErr);
			return null;
		}
	}
}

function attachAutocompleteWidget(container, elementId, methodName, widget) {
	const placeholderText = methodName === "UpdateOrigin" ? "Enter origin..." : "Enter destination...";
	widget.id = `${elementId}-field`;
	widget.name = `${elementId}-field`;
	widget.setAttribute(
		"aria-label",
		methodName === "UpdateOrigin" ? "Origin" : "Destination",
	);
	widget.setAttribute("aria-labelledby", `${elementId}-label`);
	widget.setAttribute("placeholder", placeholderText);

	container.innerHTML = "";
	container.appendChild(widget);
	autocompletes[elementId] = widget;
	if (Object.hasOwn(pendingAutocompleteValues, elementId)) {
		setAutocompleteValue(
			elementId,
			pendingAutocompleteValues[elementId],
			selectedPlaceLocations[elementId]?.location ?? null
		);
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
		const placeAutocomplete = new PlaceAutocompleteElement({
			requestedRegion: "my",
			includedRegionCodes: ["my"],
		});

		// Style the widget to fill its parent container
		placeAutocomplete.style.width = "100%";

		// Clear the container and append the new element
		attachAutocompleteWidget(container, elementId, methodName, placeAutocomplete);

		// Listen for place selection via the new gmp-select event
		placeAutocomplete.addEventListener("gmp-select", async ({ placePrediction }) => {
			if (!placePrediction) return;
			try {
				const place = placePrediction.toPlace();
				await place.fetchFields({ fields: ["displayName", "formattedAddress", "location"] });

				const displayText = place.formattedAddress || formatDisplayName(place.displayName) || "";
				const selectedLoc = toLatLngLiteral(place.location);
				if (displayText && selectedLoc) {
					selectedPlaceLocations[elementId] = { text: displayText, location: selectedLoc };
				} else {
					delete selectedPlaceLocations[elementId];
				}

				if (dotNetHelper) {
					dotNetHelper.invokeMethodAsync(methodName, displayText);

					if (methodName === "UpdateOrigin" && selectedLoc) {
						updateUserMarker(selectedLoc.lat, selectedLoc.lng);
						dotNetHelper.invokeMethodAsync("UpdateUserPosition", selectedLoc.lat, selectedLoc.lng);
					}
				}
			} catch (err) {
				console.error("Place selection failed:", err);
			}
		});
	} catch (err) {
		console.debug(`PlaceAutocompleteElement unavailable for ${elementId}, falling back to text input:`, err);
		const fallbackInput = document.createElement("input");
		fallbackInput.type = "text";
		fallbackInput.className = "w-100";
		fallbackInput.style.border = "none";
		fallbackInput.style.background = "transparent";
		fallbackInput.style.outline = "none";
		fallbackInput.style.color = "inherit";
		fallbackInput.style.font = "inherit";
		fallbackInput.addEventListener("input", async (e) => {
			if (dotNetHelper) {
				await dotNetHelper.invokeMethodAsync(methodName, e.target.value);
			}
		});
		attachAutocompleteWidget(container, elementId, methodName, fallbackInput);
	}
}

/**
 * Updates the text value of a PlaceAutocompleteElement widget or fallback input.
 */
export function setAutocompleteValue(elementId, value, location = null) {
	if (
		location &&
		Number.isFinite(Number(location.lat)) &&
		Number.isFinite(Number(location.lng))
	) {
		selectedPlaceLocations[elementId] = {
			text: value || "",
			location: { lat: Number(location.lat), lng: Number(location.lng) },
		};
	} else {
		delete selectedPlaceLocations[elementId];
	}
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

function getAutocompleteValueAndLocation(elementId) {
	const cached = selectedPlaceLocations[elementId];
	const widget = autocompletes[elementId];
	const internalInput = widget?.querySelector ? widget.querySelector("input") : null;
	const text =
		(typeof widget?.value === "string" && widget.value) ||
		(typeof internalInput?.value === "string" && internalInput.value) ||
		pendingAutocompleteValues[elementId] ||
		cached?.text ||
		"";
	const location = cached && cached.text === text ? cached.location : null;
	return { text, location };
}

export function swapAutocompleteLocations(firstId, secondId) {
	const first = getAutocompleteValueAndLocation(firstId);
	const second = getAutocompleteValueAndLocation(secondId);
	setAutocompleteValue(firstId, second.text, second.location);
	setAutocompleteValue(secondId, first.text, first.location);
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

function clearRenderedRouteOverlays() {
	routePolylines.forEach(p => p.setMap(null));
	routePolylines = [];
	routeMarkers.forEach(removeAdvancedMarker);
	routeMarkers = [];
	routeCalloutMarkers.forEach(entry => removeAdvancedMarker(entry.marker));
	routeCalloutMarkers = [];
	routeOptionsMap.clear();
}

/**
 * Clears any previously rendered route polylines, markers, and cached route state.
 */
export function clearRoute() {
	clearRenderedRouteOverlays();
	cachedTransitRoutes.clear();
	renderedRoutesData = [];
	activeRouteDotNetRef = null;
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
	if (m === "DRIVING" || m === "DRIVE") return "DRIVING";
	if (m === "WALKING" || m === "WALK") return "WALKING";
	if (m === "BICYCLING" || m === "BICYCLE") return "BICYCLING";
	if (m === "TRANSIT") return "TRANSIT";
	return m || "TRANSIT";
}

function normalizeHexColor(color, fallback = "#1a73e8") {
	if (typeof color !== "string") return fallback;
	const trimmed = color.trim();
	if (/^#[0-9a-fA-F]{6}$/.test(trimmed)) return trimmed;
	if (/^[0-9a-fA-F]{6}$/.test(trimmed)) return `#${trimmed}`;
	return fallback;
}

function toValidDate(value) {
	if (!value) return null;
	const ms = typeof value.getTime === "function" ? value.getTime() : new Date(value).getTime();
	return Number.isFinite(ms) ? new Date(ms) : null;
}

function formatRouteTime(value) {
	const date = toValidDate(value);
	if (!date) return "";
	try {
		return new Intl.DateTimeFormat(undefined, {
			hour: "numeric",
			minute: "2-digit",
			timeZone: displayTimeZone || undefined,
		}).format(date);
	} catch (_) {
		return new Intl.DateTimeFormat(undefined, {
			hour: "numeric",
			minute: "2-digit",
		}).format(date);
	}
}

function formatDurationLabel(durationSeconds) {
	const totalMins = Math.max(1, Math.round((durationSeconds || 0) / 60));
	if (totalMins < 60) return `${totalMins} min`;
	const hours = Math.floor(totalMins / 60);
	const mins = totalMins % 60;
	return mins > 0 ? `${hours} hr ${mins} min` : `${hours} hr`;
}

function createOriginMarkerContent() {
	const dot = document.createElement("div");
	dot.className = "route-origin-dot";
	return dot;
}

const SVG_NS = "http://www.w3.org/2000/svg";

function createTransitCalloutSvg() {
	const svg = document.createElementNS(SVG_NS, "svg");
	svg.setAttribute("viewBox", "0 0 24 24");
	svg.setAttribute("width", "14");
	svg.setAttribute("height", "14");
	svg.setAttribute("aria-hidden", "true");
	const path = document.createElementNS(SVG_NS, "path");
	path.setAttribute("fill", "currentColor");
	path.setAttribute(
		"d",
		"M12 2c-4 0-8 .5-8 4v9.5C4 17.43 5.57 19 7.5 19L6 20.5v.5h2l2-2h4l2 2h2v-.5L16.5 19c1.93 0 3.5-1.57 3.5-3.5V6c0-3.5-4-4-8-4zM7.5 17c-.83 0-1.5-.67-1.5-1.5S6.67 14 7.5 14s1.5.67 1.5 1.5S8.33 17 7.5 17zm3.5-7H6V6h5v4zm5.5 7c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 1.5-.67 1.5-1.5 1.5zm1.5-7h-5V6h5v4z"
	);
	svg.appendChild(path);
	return svg;
}

function createCalloutPillElement(durationLabel, headwayMinutes, isActive, showTransitIcon = true) {
	const pill = document.createElement("button");
	pill.type = "button";
	pill.className = isActive ? "route-callout-pill route-callout-pill--active" : "route-callout-pill";

	if (showTransitIcon) {
		const iconWrap = document.createElement("span");
		iconWrap.className = "route-callout-icon";
		iconWrap.appendChild(createTransitCalloutSvg());
		pill.appendChild(iconWrap);
	}

	const textWrap = document.createElement("span");
	textWrap.className = "route-callout-text";

	const durationEl = document.createElement("span");
	durationEl.className = "route-callout-duration";
	durationEl.textContent = durationLabel;
	textWrap.appendChild(durationEl);

	if (Number.isFinite(headwayMinutes) && headwayMinutes > 0) {
		const subEl = document.createElement("span");
		subEl.className = "route-callout-sub";
		subEl.textContent = `every ${headwayMinutes} min`;
		textWrap.appendChild(subEl);
	}

	pill.appendChild(textWrap);
	return pill;
}

async function createRouteEndpointMarkers(startPos, endPos) {
	if (!startPos || !endPos) return;
	try {
		const { PinElement, AdvancedMarkerElement } = await google.maps.importLibrary("marker");
		if (AdvancedMarkerElement) {
			const originMarker = new AdvancedMarkerElement({
				position: startPos,
				content: createOriginMarkerContent(),
				title: "Origin",
				zIndex: 150,
			});
			originMarker.map = map;
			routeMarkers.push(originMarker);

			const destContent = PinElement
				? new PinElement({
					background: "#EA4335",
					borderColor: "#B31412",
					glyphColor: "#FFFFFF",
				})
				: undefined;
			const destMarker = new AdvancedMarkerElement({
				position: endPos,
				content: destContent,
				title: "Destination",
				zIndex: 150,
			});
			destMarker.map = map;
			routeMarkers.push(destMarker);
			return;
		}
	} catch (_) { }

	if (google.maps.Marker) {
		try {
			routeMarkers.push(
				new google.maps.Marker({ position: startPos, map, title: "Origin", label: "A" }),
				new google.maps.Marker({ position: endPos, map, title: "Destination", label: "B" }),
			);
		} catch (_) { }
	}
}

async function addRouteCalloutMarker(routeId, position, durationLabel, headwayMinutes, isActive, onSelect, showTransitIcon = true) {
	if (!position || !durationLabel) return;
	try {
		const { AdvancedMarkerElement } = await google.maps.importLibrary("marker");
		if (!AdvancedMarkerElement) return;

		const pill = createCalloutPillElement(durationLabel, headwayMinutes, isActive, showTransitIcon);
		const marker = new AdvancedMarkerElement({
			position,
			map,
			content: pill,
			title: durationLabel,
			zIndex: isActive ? 200 : 50,
			gmpClickable: typeof onSelect === "function",
		});

		if (typeof onSelect === "function") {
			addAdvancedMarkerEventListener(marker, "gmp-click", () => onSelect(routeId));
		}

		routeCalloutMarkers.push({ routeId, marker, pill });
	} catch (_) { }
}

function buildSegmentPolylineOptions(defaultOptions, details, isActive) {
	const base = defaultOptions ? { ...defaultOptions } : {};
	const isWalking = details?.travelMode === "WALKING";

	if (isActive) {
		const activeOpts = {
			...base,
			clickable: true,
			zIndex: isWalking ? 110 : 100,
		};
		if (!isWalking) {
			activeOpts.strokeWeight = Math.max(base.strokeWeight || 6, 6);
			activeOpts.strokeOpacity = 1;
		}
		return activeOpts;
	}

	const isDark = currentColorScheme !== "LIGHT";
	const mutedColor = isDark ? "#7c83b8" : "#7e89a8";
	const inactiveOpts = {
		...base,
		strokeColor: mutedColor,
		strokeWeight: 4,
		strokeOpacity: isWalking ? 0 : 0.65,
		zIndex: 10,
		clickable: true,
	};

	if (Array.isArray(base.icons)) {
		inactiveOpts.icons = base.icons.map(entry => ({
			...entry,
			icon: entry?.icon
				? {
					...entry.icon,
					fillColor: mutedColor,
					strokeColor: mutedColor,
					fillOpacity: 0.5,
					strokeOpacity: 0.5,
				}
				: entry?.icon,
		}));
	}

	return inactiveOpts;
}

function pickCalloutPosition(route, fallbackPath, usedPositions) {
	const rawPath = (Array.isArray(route?.path) && route.path.length > 0) ? route.path : fallbackPath;
	if (!Array.isArray(rawPath) || rawPath.length === 0) return null;

	const points = rawPath.map(toLatLngLiteral).filter(Boolean);
	if (points.length === 0) return null;

	const fractions = [0.5, 0.35, 0.65, 0.25, 0.75, 0.18, 0.82];
	let bestPoint = points[Math.floor(points.length / 2)];
	let bestScore = -1;

	for (const fraction of fractions) {
		const idx = Math.min(points.length - 1, Math.max(0, Math.floor((points.length - 1) * fraction)));
		const candidate = points[idx];
		if (usedPositions.length === 0) {
			bestPoint = candidate;
			break;
		}
		let minDist = Infinity;
		for (const used of usedPositions) {
			const dist = distanceMeters(candidate, used);
			if (dist < minDist) minDist = dist;
		}
		if (minDist > bestScore) {
			bestScore = minDist;
			bestPoint = candidate;
		}
	}

	usedPositions.push(bestPoint);
	return bestPoint;
}

function getRouteEndpoints(route, fallbackStart, fallbackEnd) {
	const legs = route?.legs;
	if (Array.isArray(legs) && legs.length > 0) {
		const firstLoc = legs[0].startLocation?.latLng || legs[0].startLocation;
		const lastLoc = legs[legs.length - 1].endLocation?.latLng || legs[legs.length - 1].endLocation;
		return {
			startPos: firstLoc || fallbackStart,
			endPos: lastLoc || fallbackEnd,
		};
	}
	return { startPos: fallbackStart, endPos: fallbackEnd };
}

/**
 * Computes and renders a route using the new Routes library (Route.computeRoutes).
 * Replaces legacy DirectionsService.route() + DirectionsRenderer.setDirections().
 */
async function showRoute(origin, destination, travelMode = "TRANSIT") {
	await loadGoogleMaps();
	if (!map) throw new Error("Map not initialized.");

	const { Route } = await google.maps.importLibrary("routes");

	clearRoute();

	const originLoc = toLatLngLiteral(origin) || origin;
	const destinationLoc = toLatLngLiteral(destination) || destination;

	const routesTravelMode = normalizeRoutesTravelMode(travelMode);
	const request = {
		origin: originLoc,
		destination: destinationLoc,
		travelMode: routesTravelMode,
		fields: ["path", "durationMillis", "distanceMeters", "legs", "viewport", "localizedValues"],
	};

	const { routes } = await Route.computeRoutes(request);

	if (!routes || routes.length === 0) {
		return false;
	}

	const route = routes[0];

	routePolylines = route.createPolylines({
		colorScheme: currentColorScheme,
		polylineOptions: (defaultOptions, details) => buildSegmentPolylineOptions(defaultOptions, details, true),
	});
	routePolylines.forEach(polyline => polyline.setMap(map));

	const { startPos, endPos } = getRouteEndpoints(route, originLoc, destinationLoc);
	await createRouteEndpointMarkers(startPos, endPos);

	const durationSec = route.durationMillis ? Math.round(route.durationMillis / 1000) : 0;
	const durationLabel = route.localizedValues?.duration || (durationSec > 0 ? formatDurationLabel(durationSec) : "");
	const pillPos = pickCalloutPosition(route, null, []);
	if (pillPos && durationLabel) {
		await addRouteCalloutMarker(0, pillPos, durationLabel, null, true, null, routesTravelMode === "TRANSIT");
	}

	if (route.viewport) map.fitBounds(route.viewport);
	return true;
}

async function resolveEndpoint(elementId, placeName) {
	const cached = selectedPlaceLocations[elementId];
	if (cached && cached.text === placeName) {
		return cached.location;
	}
	return toLatLngLiteral(await geocodePlaceName(placeName));
}

async function resolveRouteEndpoints(originName, destinationName) {
	const [originLoc, destinationLoc] = await Promise.all([
		resolveEndpoint("origin-input", originName),
		resolveEndpoint("destination-input", destinationName)
	]);

	if (!originLoc || !destinationLoc) {
		throw new Error("Unable to resolve origin or destination location.");
	}

	return { originLoc, destinationLoc };
}

export async function showRouteByName(originName, destinationName, travelMode = "TRANSIT") {
	await loadGoogleMaps();
	try {
		const { originLoc, destinationLoc } = await resolveRouteEndpoints(originName, destinationName);
		return await showRoute(originLoc, destinationLoc, travelMode);
	} catch (err) {
		console.error("showRouteByName failed:", err);
		throw err;
	}
}

function extractRouteMetadata(route, fallbackDepartureDate) {
	const transitSteps = [];
	const rawSteps = [];

	if (Array.isArray(route.legs)) {
		route.legs.forEach(leg => {
			if (!Array.isArray(leg.steps)) return;
			leg.steps.forEach(step => {
				const stepDurationSec = step.staticDurationMillis ? Math.round(step.staticDurationMillis / 1000) : 0;
				if (step.transitDetails) {
					const td = step.transitDetails;
					const line = td.transitLine;
					const departureLoc = toLatLngLiteral(td.departureStop?.location);
					const shortName = line?.shortName || line?.nameShort || td.tripShortText || line?.name || "";
					const headwayMin = Number.isFinite(td.headwayMillis) && td.headwayMillis > 0
						? Math.round(td.headwayMillis / 60000)
						: null;

					transitSteps.push({
						lineShortName: shortName,
						lineName: line?.name || "",
						departureLat: departureLoc?.lat ?? 0,
						departureLng: departureLoc?.lng ?? 0,
						departureStopName: td.departureStop?.name || "",
					});

					rawSteps.push({
						travelMode: "TRANSIT",
						vehicleType: line?.vehicle?.vehicleType || "",
						lineShortName: shortName,
						lineName: line?.name || "",
						lineColor: normalizeHexColor(line?.color, "#1a73e8"),
						lineTextColor: normalizeHexColor(line?.textColor, "#ffffff"),
						headsign: td.headsign || "",
						departureStopName: td.departureStop?.name || "",
						arrivalStopName: td.arrivalStop?.name || "",
						departureTime: td.departureTime || null,
						arrivalTime: td.arrivalTime || null,
						departureTimeText: formatRouteTime(td.departureTime),
						arrivalTimeText: formatRouteTime(td.arrivalTime),
						durationSeconds: stepDurationSec,
						distanceMeters: step.distanceMeters || 0,
						stopCount: td.stopCount || 0,
						headwayMinutes: headwayMin,
						instructions: step.instructions || "",
					});
				} else {
					rawSteps.push({
						travelMode: step.travelMode || "WALKING",
						vehicleType: "",
						lineShortName: "",
						lineName: "",
						lineColor: "",
						lineTextColor: "",
						headsign: "",
						departureStopName: "",
						arrivalStopName: "",
						departureTime: null,
						arrivalTime: null,
						departureTimeText: "",
						arrivalTimeText: "",
						durationSeconds: stepDurationSec,
						distanceMeters: step.distanceMeters || 0,
						stopCount: 0,
						headwayMinutes: null,
						instructions: step.instructions || "",
					});
				}
			});
		});
	}

	// Merge consecutive WALKING steps into one summary walking segment, matching Google Maps cards.
	const segments = [];
	for (const step of rawSteps) {
		const prev = segments[segments.length - 1];
		if (step.travelMode === "WALKING" && prev && prev.travelMode === "WALKING") {
			prev.durationSeconds += step.durationSeconds;
			prev.distanceMeters += step.distanceMeters;
			if (!prev.instructions && step.instructions) prev.instructions = step.instructions;
		} else {
			segments.push({ ...step });
		}
	}

	let departureDate = null;
	let arrivalDate = null;
	let leadingWalkSec = 0;
	for (const seg of segments) {
		if (seg.travelMode === "TRANSIT") {
			const dep = toValidDate(seg.departureTime);
			if (dep) {
				departureDate = new Date(dep.getTime() - leadingWalkSec * 1000);
				break;
			}
		}
		leadingWalkSec += seg.durationSeconds;
	}

	let trailingWalkSec = 0;
	for (let i = segments.length - 1; i >= 0; i--) {
		const seg = segments[i];
		if (seg.travelMode === "TRANSIT") {
			const arr = toValidDate(seg.arrivalTime);
			if (arr) {
				arrivalDate = new Date(arr.getTime() + trailingWalkSec * 1000);
				break;
			}
		}
		trailingWalkSec += seg.durationSeconds;
	}

	const durationSeconds = route.durationMillis ? Math.round(route.durationMillis / 1000) : 0;
	if (!departureDate && fallbackDepartureDate) {
		departureDate = fallbackDepartureDate;
	}
	if (!arrivalDate && departureDate && durationSeconds > 0) {
		arrivalDate = new Date(departureDate.getTime() + durationSeconds * 1000);
	}

	const firstTransit = segments.find(s => s.travelMode === "TRANSIT");
	return {
		durationSeconds,
		distanceMeters: route.distanceMeters || 0,
		localizedDuration: route.localizedValues?.duration || (durationSeconds > 0 ? formatDurationLabel(durationSeconds) : ""),
		localizedDistance: route.localizedValues?.distance || "",
		departureTimeText: formatRouteTime(departureDate),
		arrivalTimeText: formatRouteTime(arrivalDate),
		headwayMinutes: firstTransit?.headwayMinutes ?? null,
		transitSteps,
		segments: segments.map(({ departureTime, arrivalTime, ...serializable }) => serializable),
	};
}

export async function getTransitRoutes(originName, destinationName, options = null) {
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
			fields: ["path", "durationMillis", "distanceMeters", "legs", "viewport", "localizedValues", "routeLabels"],
		};

		let fallbackDeparture = null;
		if (options?.departureMode === "depart" && options.dateTimeIso) {
			const dt = new Date(options.dateTimeIso);
			if (!Number.isNaN(dt.getTime())) {
				request.departureTime = dt;
				fallbackDeparture = dt;
			}
		} else if (options?.departureMode === "arrive" && options.dateTimeIso) {
			const dt = new Date(options.dateTimeIso);
			if (!Number.isNaN(dt.getTime())) {
				request.arrivalTime = dt;
			}
		}

		const { routes } = await Route.computeRoutes(request);

		cachedTransitRoutes.clear();
		if (!routes || routes.length === 0) {
			return [];
		}

		return routes.map((r, index) => {
			let encodedPolyline = "";
			if (r.path) {
				encodedPolyline = encoding.encodePath(r.path);
			}

			const meta = extractRouteMetadata(r, fallbackDeparture);
			const summary = {
				routeId: index,
				encodedPolyline,
				...meta,
			};
			cachedTransitRoutes.set(index, { route: r, summary });
			return summary;
		});

	} catch (err) {
		console.error("getTransitRoutes failed:", err);
		throw err;
	}
}

function selectRouteFromMap(routeId, dotNetRef) {
	if (currentActiveRouteId !== routeId) {
		setActiveRoute(routeId);
	}
	dotNetRef?.invokeMethodAsync("OnRouteSelected", routeId)?.catch(() => {});
}

async function renderMultipleRoutes(routesData, activeRouteId, dotNetRef, fitViewport = true) {
	const [{ Polyline }, { LatLngBounds }, { encoding }] = await Promise.all([
		google.maps.importLibrary("maps"),
		google.maps.importLibrary("core"),
		google.maps.importLibrary("geometry")
	]);

	clearRenderedRouteOverlays();
	renderedRoutesData = routesData.slice();
	activeRouteDotNetRef = dotNetRef;
	currentActiveRouteId = activeRouteId;

	const bounds = new LatLngBounds();
	const usedCalloutPositions = [];

	// Draw inactive alternatives first so the active route renders cleanly on top.
	const ordered = [
		...routesData.filter(r => r.id !== activeRouteId),
		...routesData.filter(r => r.id === activeRouteId),
	];

	for (const routeData of ordered) {
		const isActive = routeData.id === activeRouteId;
		const cached = cachedTransitRoutes.get(routeData.id);
		const routeObj = cached?.route;
		const segments = [];
		let fallbackPath = null;

		if (routeObj && typeof routeObj.createPolylines === "function") {
			const segmentOptionPairs = [];
			const polylines = routeObj.createPolylines({
				colorScheme: currentColorScheme,
				polylineOptions: (defaultOptions, details) => {
					const activeOpts = buildSegmentPolylineOptions(defaultOptions, details, true);
					const inactiveOpts = buildSegmentPolylineOptions(defaultOptions, details, false);
					segmentOptionPairs.push({ activeOpts, inactiveOpts });
					return isActive ? activeOpts : inactiveOpts;
				},
			});

			polylines.forEach((polyline, idx) => {
				const pair = segmentOptionPairs[idx] || {
					activeOpts: buildSegmentPolylineOptions(null, null, true),
					inactiveOpts: buildSegmentPolylineOptions(null, null, false),
				};
				polyline.addListener("click", () => selectRouteFromMap(routeData.id, dotNetRef));
				polyline.setMap(map);
				routePolylines.push(polyline);
				segments.push({ polyline, ...pair });
			});

			if (Array.isArray(routeObj.path)) {
				routeObj.path.forEach(latLng => bounds.extend(latLng));
			}
		} else {
			if (!routeData.path) continue;
			fallbackPath = encoding.decodePath(routeData.path);
			if (fallbackPath.length === 0) continue;

			const activeOpts = { strokeColor: "#4285F4", strokeWeight: 6, strokeOpacity: 1, zIndex: 100, clickable: true };
			const inactiveOpts = { strokeColor: "#7c83b8", strokeWeight: 4, strokeOpacity: 0.65, zIndex: 10, clickable: true };
			const polyline = new Polyline({
				path: fallbackPath,
				...(isActive ? activeOpts : inactiveOpts),
			});
			polyline.addListener("click", () => selectRouteFromMap(routeData.id, dotNetRef));
			polyline.setMap(map);
			routePolylines.push(polyline);
			segments.push({ polyline, activeOpts, inactiveOpts });
			fallbackPath.forEach(latLng => bounds.extend(latLng));
		}

		routeOptionsMap.set(routeData.id, { segments, hasLiveBus: routeData.hasLiveBus });

		const durationLabel = routeData.localizedDuration
			|| cached?.summary?.localizedDuration
			|| (routeData.durationSeconds > 0 ? formatDurationLabel(routeData.durationSeconds) : "");
		const headwayMinutes = routeData.headwayMinutes ?? cached?.summary?.headwayMinutes ?? null;
		const pillPos = pickCalloutPosition(routeObj, fallbackPath, usedCalloutPositions);
		if (pillPos && durationLabel) {
			await addRouteCalloutMarker(
				routeData.id,
				pillPos,
				durationLabel,
				headwayMinutes,
				isActive,
				(id) => selectRouteFromMap(id, dotNetRef),
				true
			);
		}
	}

	const activeCached = cachedTransitRoutes.get(activeRouteId)?.route;
	if (activeCached) {
		const { startPos, endPos } = getRouteEndpoints(activeCached, null, null);
		await createRouteEndpointMarkers(startPos, endPos);
	}

	if (fitViewport && routesData.length > 0 && !bounds.isEmpty()) {
		map.fitBounds(bounds);
	}
}

export async function drawMultipleRoutes(routesData, activeRouteId, dotNetRef) {
	await loadGoogleMaps();
	if (!map) throw new Error("Map not initialized.");
	await renderMultipleRoutes(routesData, activeRouteId, dotNetRef, true);
}

export function setActiveRoute(routeId) {
	currentActiveRouteId = routeId;

	routeOptionsMap.forEach((data, id) => {
		const isActive = (id === routeId);
		for (const seg of data.segments || []) {
			seg.polyline.setOptions(isActive ? seg.activeOpts : seg.inactiveOpts);
		}
	});

	for (const entry of routeCalloutMarkers) {
		const isActive = entry.routeId === routeId;
		if (entry.pill) {
			entry.pill.className = isActive ? "route-callout-pill route-callout-pill--active" : "route-callout-pill";
		}
		if (entry.marker) {
			entry.marker.zIndex = isActive ? 200 : 50;
		}
	}
}



function getTransportMarker(location) {
	const type = typeof location.transportType === "string" ? location.transportType.trim().toLowerCase() : "";
	return Object.hasOwn(transportMarkerTypes, type) ? transportMarkerTypes[type] : unknownTransportMarker;
}

const MIN_HEADING_MOVEMENT_METERS = 15;
const EARTH_RADIUS_METERS = 6371000;
const DEG_TO_RAD = Math.PI / 180;

function distanceMeters(from, to) {
	// Equirectangular approximation is accurate enough for poll-to-poll distances.
	const x = (to.lng - from.lng) * DEG_TO_RAD * Math.cos(((from.lat + to.lat) / 2) * DEG_TO_RAD);
	const y = (to.lat - from.lat) * DEG_TO_RAD;
	return Math.hypot(x, y) * EARTH_RADIUS_METERS;
}

function headingDegrees(from, to) {
	const lat1 = from.lat * DEG_TO_RAD;
	const lat2 = to.lat * DEG_TO_RAD;
	const dLng = (to.lng - from.lng) * DEG_TO_RAD;
	const y = Math.sin(dLng) * Math.cos(lat2);
	const x = Math.cos(lat1) * Math.sin(lat2) - Math.sin(lat1) * Math.cos(lat2) * Math.cos(dLng);
	return (Math.atan2(y, x) / DEG_TO_RAD + 360) % 360;
}

// Feed bearing 0 is treated as absent: the parser cannot distinguish an unset field,
// and some feeds (KTMB) send 0 for every vehicle. The anchor only moves when a heading
// is fixed, so slow vehicles still accumulate enough movement across polls.
export function resolveVehicleHeading(state, location) {
	const current = { lat: location.lat, lng: location.lng };
	const bearing = location.bearing;
	if (Number.isFinite(bearing) && bearing > 0 && bearing <= 360) {
		return { heading: bearing % 360, anchor: current };
	}
	const anchor = state?.anchor;
	if (anchor && distanceMeters(anchor, current) >= MIN_HEADING_MOVEMENT_METERS) {
		return { heading: headingDegrees(anchor, current), anchor: current };
	}
	return { heading: state?.heading ?? null, anchor: anchor ?? current };
}

// Side-view vehicle emojis face left (270deg). Flip horizontally on eastbound
// headings (0..180deg) so the wheels remain at the bottom while tilting along the route.
export function getIconHeadingTransform(heading) {
	const normalized = ((Math.round(heading) % 360) + 360) % 360;
	const isEast = normalized <= 180;
	return {
		heading: normalized,
		tilt: isEast ? normalized - 90 : normalized - 270,
		scaleX: isEast ? -1 : 1,
	};
}

function updateTransportMarkerElement(element, location, heading) {
	const markerType = getTransportMarker(location);
	element.className = `transport-marker ${markerType.className}`;
	let icon = element.querySelector(".transport-marker-icon");
	if (!icon) {
		const arrow = document.createElement("span");
		arrow.className = "transport-marker-arrow";
		icon = document.createElement("span");
		icon.className = "transport-marker-icon";
		element.replaceChildren(arrow, icon);
	}
	icon.textContent = location.icon || markerType.icon;
	const hasHeading = Number.isFinite(heading);
	element.classList.toggle("has-heading", hasHeading);
	if (hasHeading) {
		const { heading: normalized, tilt, scaleX } = getIconHeadingTransform(heading);
		element.style.setProperty("--heading", `${normalized}deg`);
		if (markerType.sideFacing) {
			element.style.setProperty("--icon-tilt", `${tilt}deg`);
			element.style.setProperty("--icon-scale-x", String(scaleX));
		} else {
			element.style.removeProperty?.("--icon-tilt");
			element.style.removeProperty?.("--icon-scale-x");
		}
	} else {
		element.style.removeProperty?.("--heading");
		element.style.removeProperty?.("--icon-tilt");
		element.style.removeProperty?.("--icon-scale-x");
	}
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
			const routeLabel = loc.routeShortName || loc.routeId;
			const title = `Route ${routeLabel || 'Unknown'} - ${id}`;
			
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
						marker.headingState = resolveVehicleHeading(marker.headingState, loc);
						if (marker.content) {
							updateTransportMarkerElement(marker.content, loc, marker.headingState.heading);
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

				marker = createMarkerWithFallback(
					() => {
						const headingState = resolveVehicleHeading(null, loc);
						const el = document.createElement("div");
						updateTransportMarkerElement(el, loc, headingState.heading);
						el.style.fontSize = "28px";

						const advanced = new AdvancedMarkerElement({
							position: pos,
							map,
							title,
							content: el,
							gmpClickable: true,
						});

						advanced.busData = loc;
						advanced.headingState = headingState;
						addAdvancedMarkerEventListener(advanced, "gmp-click", toggleInfoWindow);
						return advanced;
					},
					// Attempt fallback to google.maps.Marker when AdvancedMarkerElement fails
					() => {
						const markerType = getTransportMarker(loc);
						const legacy = new google.maps.Marker({
							position: pos,
							map,
							title,
							label: {
								text: loc.icon || markerType.icon,
								fontSize: "20px",
							},
						});
						legacy.busData = loc;
						legacy.addListener("click", toggleInfoWindow);
						return legacy;
					},
					null,
					"syncMarkers: both AdvancedMarkerElement and fallback failed:",
				);
				if (marker) activeMarkers[markerKey] = marker;
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

	appendBusInfoRow(content, "Route", location.routeShortName || location.routeId || "N/A");
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
	if (transitLayer) {
		transitLayer.setMap(null);
		transitLayer = null;
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
	delete selectedPlaceLocations[elementId];
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

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, SourceTextModule } from "node:vm";

const source = await readFile(
  new URL("../../MyTransportAppWASM/wwwroot/js/googleMap.js", import.meta.url),
  "utf8",
);

const VALID_ROUTE_FIELDS = new Set([
  "*",
  "routeLabels",
  "legs",
  "distanceMeters",
  "durationMillis",
  "staticDurationMillis",
  "path",
  "description",
  "warnings",
  "viewport",
  "travelAdvisory",
  "optimizedIntermediateWaypointIndices",
  "localizedValues",
  "routeToken",
  "polylineDetails",
  "speedPaths",
]);

const VALID_TRAVEL_MODES = new Set([
  "DRIVING",
  "WALKING",
  "BICYCLING",
  "TRANSIT",
  "TWO_WHEELER",
]);

const plain = (value) => JSON.parse(JSON.stringify(value));

function createDotNetRecorder() {
  const invocations = [];
  return {
    invocations,
    dotNetHelper: {
      invokeMethodAsync: async (method, ...args) =>
        invocations.push([method, ...args]),
    },
  };
}

function createMockGoogleMaps(overrides = {}) {
  class MockGeocoder {
    async geocode({ address, location }) {
      if (overrides.onGeocode)
        return overrides.onGeocode({ address, location });
      if (overrides.geocoderError) throw overrides.geocoderError;
      if (address === "Nowhere") throw new Error("ZERO_RESULTS");
      if (address) {
        return {
          results: [
            {
              geometry: { location: { lat: () => 3.14, lng: () => 101.69 } },
              formatted_address: `${address}, Malaysia`,
            },
          ],
        };
      }
      if (location) {
        return {
          results: [
            {
              formatted_address: "Jalan Ampang, Kuala Lumpur",
            },
          ],
        };
      }
      return { results: [] };
    }
  }

  class MockMap {
    addListener() {}
    fitBounds() {
      overrides.onFitBounds?.();
    }
  }

  class MockInfoWindow {
    close() {}
  }

  class MockPolyline {
    constructor(opts = {}) {
      this.opts = opts;
    }
    setOptions(opts) {
      Object.assign(this.opts, opts);
    }
    setMap(m) {
      this.map = m;
    }
    addListener() {}
  }

  class MockTransitLayer {
    constructor() {
      this.map = null;
      this.setMapCalls = [];
      overrides.transitLayers?.push(this);
    }
    setMap(m) {
      this.map = m;
      this.setMapCalls.push(m);
    }
    getMap() {
      return this.map;
    }
  }

  class MockLatLngBounds {
    constructor() {
      this.points = [];
    }
    extend(p) {
      this.points.push(p);
    }
    isEmpty() {
      return this.points.length === 0;
    }
  }

  class MockPinElement {
    constructor(opts) {
      this.opts = opts;
    }
  }

  class MockAdvancedMarkerElement {
    constructor(opts) {
      Object.assign(this, opts);
    }
    addEventListener(event, cb) {
      overrides.markerListeners?.set(event, cb);
    }
    removeEventListener() {}
  }

  const MockRoute = {
    async computeRoutes(request) {
      overrides.computeRequests?.push(request);
      const invalidFields = (request.fields || []).filter(
        (f) => !VALID_ROUTE_FIELDS.has(f),
      );
      if (invalidFields.length > 0) {
        throw new Error(
          `InvalidValueError: in property fields: contains invalid fields: ${invalidFields.join(", ")}`,
        );
      }
      if (!VALID_TRAVEL_MODES.has(request.travelMode)) {
        throw new Error(
          `InvalidValueError: in property travelMode: ${request.travelMode} is not an accepted value`,
        );
      }
      if (overrides.routesResult) {
        return overrides.routesResult(request);
      }
      return {
        routes: [
          {
            path: [
              { lat: 3.14, lng: 101.69 },
              { lat: 3.15, lng: 101.7 },
            ],
            durationMillis: 900000,
            distanceMeters: 4200,
            viewport: { north: 3.16, south: 3.13, east: 101.71, west: 101.68 },
            createPolylines: () => [new MockPolyline()],
            legs: [
              {
                startLocation: { lat: 3.14, lng: 101.69 },
                endLocation: { lat: 3.15, lng: 101.7 },
                steps: [
                  {
                    travelMode: "TRANSIT",
                    transitDetails: {
                      transitLine: {
                        shortName: "180",
                        name: "Taman Dato' Senu ~ Hab Pasar Seni",
                      },
                      departureStop: {
                        name: "KL50 Damai Hospital",
                        location: { lat: 3.169987, lng: 101.695148 },
                      },
                    },
                  },
                ],
              },
            ],
          },
        ],
      };
    },
  };

  return {
    maps: {
      Map: MockMap,
      importLibrary: async (lib) => {
        if (lib === "maps")
          return {
            Map: MockMap,
            InfoWindow: MockInfoWindow,
            Polyline: MockPolyline,
            TransitLayer: MockTransitLayer,
          };
        if (lib === "core") return { LatLngBounds: MockLatLngBounds };
        if (lib === "geocoding")
          return { Geocoder: overrides.Geocoder ?? MockGeocoder };
        if (lib === "routes") return { Route: MockRoute };
        if (lib === "geometry") {
          return {
            encoding: {
              encodePath: (path) => `encoded:${path.length}`,
              decodePath: (encoded) =>
                encoded === "empty" ? [] : [{ lat: 3.14, lng: 101.69 }],
            },
          };
        }
        if (lib === "marker") {
          return {
            PinElement: MockPinElement,
            AdvancedMarkerElement: MockAdvancedMarkerElement,
          };
        }
        if (lib === "places" && overrides.withPlaces) {
          return {
            PlaceAutocompleteElement: class {
              constructor() {
                this.style = {};
                this.listeners = {};
              }
              setAttribute() {}
              addEventListener(type, fn) {
                this.listeners[type] = fn;
                overrides.autocompleteListeners?.set(this.id, fn);
              }
            },
          };
        }
        return {};
      },
    },
  };
}

async function loadMapModule(
  googleMock,
  documentOverrides = {},
  windowOverrides = {},
  globalOverrides = {},
) {
  const elementStub = {
    style: {},
    setAttribute: () => {},
    append: () => {},
    appendChild: () => {},
    addEventListener: () => {},
  };
  const doc = {
    createElement: () => ({ ...elementStub }),
    createElementNS: () => ({ ...elementStub }),
    querySelector: () => null,
    getElementById: () => ({ ...elementStub }),
    head: { append: () => {} },
  };
  Object.defineProperties(doc, Object.getOwnPropertyDescriptors(documentOverrides));

  const context = createContext({
    URLSearchParams,
    Number,
    setInterval,
    clearInterval,
    document: doc,
    window: {
      google: googleMock,
      ...windowOverrides,
    },
    google: googleMock,
    console,
    ...globalOverrides,
  });

  const module = new SourceTextModule(source, { context });
  await module.link(() => assert.fail("Unexpected static dependency"));
  await module.evaluate();
  return module.namespace;
}

test("formatDisplayName safely extracts display names from string or object", async () => {
  const api = await loadMapModule(createMockGoogleMaps());
  assert.equal(api.formatDisplayName("KL Sentral"), "KL Sentral");
  assert.equal(api.formatDisplayName({ text: "Mid Valley" }), "Mid Valley");
  assert.equal(api.formatDisplayName(null), "");
  assert.equal(api.formatDisplayName(undefined), "");
});

test("loadGoogleMaps rejects when API key is missing", async () => {
  const api = await loadMapModule({ maps: {} });
  await assert.rejects(api.loadGoogleMaps(null), /API key not provided/);
});

test("geocodePlaceName returns coordinates and formatted address with Malaysian region bias", async () => {
  const api = await loadMapModule(createMockGoogleMaps());
  const res = await api.geocodePlaceName("KLCC", "test-api-key");
  assert.ok(res);
  assert.equal(res.lat, 3.14);
  assert.equal(res.lng, 101.69);
  assert.equal(res.formattedAddress, "KLCC, Malaysia");

  assert.equal(await api.geocodePlaceName("", "test-api-key"), null);
  assert.equal(await api.geocodePlaceName("   ", "test-api-key"), null);
  assert.equal(await api.geocodePlaceName("Nowhere", "test-api-key"), null);
});

test("reverseGeocode validates coordinates and returns formatted address", async () => {
  const api = await loadMapModule(createMockGoogleMaps());
  const res = await api.reverseGeocode(3.14, 101.69, "test-api-key");
  assert.equal(res, "Jalan Ampang, Kuala Lumpur");

  assert.equal(await api.reverseGeocode(NaN, 101.69, "test-api-key"), null);
  assert.equal(await api.reverseGeocode(95.0, 101.69, "test-api-key"), null);
  assert.equal(await api.reverseGeocode(3.14, 190.0, "test-api-key"), null);
});

test("callGeocoder supports legacy callback-only geocoding", async () => {
  class CallbackOnlyGeocoder {
    geocode({ address }, callback) {
      if (address === "Nowhere") {
        callback([], "ZERO_RESULTS");
      } else {
        callback(
          [
            {
              geometry: { location: { lat: 3.14, lng: 101.69 } },
              formatted_address: "Kuala Lumpur",
            },
          ],
          "OK",
        );
      }
    }
  }
  const api = await loadMapModule(
    createMockGoogleMaps({ Geocoder: CallbackOnlyGeocoder }),
  );
  const res = await api.geocodePlaceName("KLCC", "test-api-key");
  assert.ok(res);
  assert.equal(res.lat, 3.14);
  assert.equal(res.formattedAddress, "Kuala Lumpur");

  const zero = await api.geocodePlaceName("Nowhere", "test-api-key");
  assert.equal(zero, null);
});

test("getTransitRoutes requests valid top-level fields and extracts DirectionalLocation stop coordinates", async () => {
  const computeRequests = [];
  const api = await loadMapModule(createMockGoogleMaps({ computeRequests }));
  await api.loadGoogleMaps("test-api-key");

  const routes = await api.getTransitRoutes("KL Sentral", "KLCC");

  assert.equal(computeRequests.length, 1);
  assert.equal(computeRequests[0].travelMode, "TRANSIT");
  assert.deepEqual(
    [...computeRequests[0].fields],
    [
      "path",
      "durationMillis",
      "distanceMeters",
      "legs",
      "viewport",
      "localizedValues",
      "routeLabels",
    ],
  );
  assert.equal(routes.length, 1);
  assert.equal(routes[0].durationSeconds, 900);
  assert.equal(routes[0].distanceMeters, 4200);
  assert.equal(routes[0].encodedPolyline, "encoded:2");
  assert.equal(routes[0].transitSteps.length, 1);
  assert.equal(routes[0].transitSteps[0].lineShortName, "180");
  assert.equal(routes[0].transitSteps[0].departureLat, 3.169987);
  assert.equal(routes[0].transitSteps[0].departureLng, 101.695148);
  assert.equal(
    routes[0].transitSteps[0].departureStopName,
    "KL50 Damai Hospital",
  );
});

test("showRouteByName sends valid Maps JS TravelMode values and handles empty route results", async () => {
  const computeRequests = [];
  const api = await loadMapModule(createMockGoogleMaps({ computeRequests }));
  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "test-api-key",
    null,
    "DARK",
    "Asia/Kuala_Lumpur",
  );

  for (const [inputMode, expectedMode] of [
    ["DRIVING", "DRIVING"],
    ["DRIVE", "DRIVING"],
    ["WALKING", "WALKING"],
    ["WALK", "WALKING"],
    ["BICYCLING", "BICYCLING"],
    ["BICYCLE", "BICYCLING"],
    ["TRANSIT", "TRANSIT"],
  ]) {
    const found = await api.showRouteByName("KL Sentral", "KLCC", inputMode);
    assert.equal(found, true);
    assert.equal(computeRequests.at(-1).travelMode, expectedMode);
  }

  const emptyRequests = [];
  const emptyApi = await loadMapModule(
    createMockGoogleMaps({
      computeRequests: emptyRequests,
      routesResult: () => ({ routes: [] }),
    }),
  );
  await emptyApi.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "test-api-key",
    null,
    "DARK",
    "Asia/Kuala_Lumpur",
  );

  const foundEmpty = await emptyApi.showRouteByName(
    "KL Sentral",
    "KLCC",
    "BICYCLING",
  );
  assert.equal(foundEmpty, false);
});

test("drawMultipleRoutes skips fitBounds when decoded paths are empty", async () => {
  let fitBoundsCalls = 0;
  const api = await loadMapModule(
    createMockGoogleMaps({ onFitBounds: () => fitBoundsCalls++ }),
  );
  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "test-api-key",
    null,
    "DARK",
    "Asia/Kuala_Lumpur",
  );
  await api.drawMultipleRoutes(
    [{ id: 0, path: "empty", hasLiveBus: false }],
    0,
    null,
  );

  assert.equal(fitBoundsCalls, 0);
});

test("initAutocomplete prefers formattedAddress and reuses selected coordinates for route calculation", async () => {
  const computeRequests = [];
  const listeners = new Map();
  let geocodeCalls = 0;

  const mock = createMockGoogleMaps({
    computeRequests,
    withPlaces: true,
    autocompleteListeners: listeners,
    onGeocode: async ({ address }) => {
      geocodeCalls++;
      if (address === "GW8F+CW") throw new Error("ZERO_RESULTS");
      return {
        results: [
          {
            geometry: { location: { lat: () => 3.04, lng: () => 101.44 } },
            formatted_address: `${address}, Malaysia`,
          },
        ],
      };
    },
  });

  const api = await loadMapModule(mock);
  const { invocations, dotNetHelper } = createDotNetRecorder();

  await api.initAutocomplete(
    "origin-input",
    dotNetHelper,
    "UpdateOrigin",
    "test-api-key",
  );
  await api.initAutocomplete(
    "destination-input",
    dotNetHelper,
    "UpdateDestination",
    "test-api-key",
  );

  await listeners.get("origin-input-field")({
    placePrediction: {
      toPlace: () => ({
        displayName: "GW8F+CW",
        formattedAddress: "GW8F+CW, Bentong, Pahang, Malaysia",
        location: { lat: () => 3.5160625, lng: () => 101.9248125 },
        fetchFields: async () => {},
      }),
    },
  });

  await listeners.get("destination-input-field")({
    placePrediction: {
      toPlace: () => ({
        displayName: "Klang",
        formattedAddress: "Klang, Selangor, Malaysia",
        location: { lat: () => 3.0392164, lng: () => 101.4418818 },
        fetchFields: async () => {},
      }),
    },
  });

  assert.deepEqual(invocations, [
    ["UpdateOrigin", "GW8F+CW, Bentong, Pahang, Malaysia"],
    ["UpdateUserPosition", 3.5160625, 101.9248125],
    ["UpdateDestination", "Klang, Selangor, Malaysia"],
  ]);

  const routes = await api.getTransitRoutes(
    "GW8F+CW, Bentong, Pahang, Malaysia",
    "Klang, Selangor, Malaysia",
  );
  assert.equal(routes.length, 1);
  assert.equal(geocodeCalls, 0);
  assert.deepEqual(plain(computeRequests[0].origin), {
    lat: 3.5160625,
    lng: 101.9248125,
  });
  assert.deepEqual(plain(computeRequests[0].destination), {
    lat: 3.0392164,
    lng: 101.4418818,
  });
});

test("handleUserMarkerDragEnd updates user position first and preserves dragged coordinates on setAutocompleteValue", async () => {
  const computeRequests = [];
  const markerListeners = new Map();
  let forwardGeocodeCalls = 0;

  const mock = createMockGoogleMaps({
    computeRequests,
    markerListeners,
    onGeocode: async ({ address, location }) => {
      if (address) {
        forwardGeocodeCalls++;
        return {
          results: [
            {
              geometry: { location: { lat: () => 9.9, lng: () => 99.9 } },
              formatted_address: `${address}`,
            },
          ],
        };
      }
      if (location) {
        return {
          results: [{ formatted_address: "Dragged Stop, Kuala Lumpur" }],
        };
      }
      return { results: [] };
    },
  });

  const { invocations, dotNetHelper } = createDotNetRecorder();
  const api = await loadMapModule(mock);
  await api.initGoogleMaps(
    "map",
    3.139,
    101.6869,
    14,
    "test-api-key",
    dotNetHelper,
    "LIGHT",
    "Asia/Kuala_Lumpur",
  );

  const dragListener = markerListeners.get("gmp-dragend");
  assert.equal(typeof dragListener, "function");
  await api.setAutocompleteValue("destination-input", "KLCC", {
    lat: 3.1579,
    lng: 101.7123,
  });
  await dragListener();

  assert.equal(invocations[0][0], "UpdateUserPosition");
  assert.equal(invocations[1][0], "UpdateOrigin");
  assert.equal(invocations[1][1], "Dragged Stop, Kuala Lumpur");

  const routes = await api.getTransitRoutes(
    "Dragged Stop, Kuala Lumpur",
    "KLCC",
  );
  assert.equal(routes.length, 1);
  assert.equal(forwardGeocodeCalls, 0);
  assert.deepEqual(plain(computeRequests[0].origin), {
    lat: 3.139,
    lng: 101.6869,
  });
  assert.deepEqual(plain(computeRequests[0].destination), {
    lat: 3.1579,
    lng: 101.7123,
  });
});

test("setAutocompleteValue before PlaceAutocompleteElement init preserves selected location", async () => {
  const computeRequests = [];
  let geocodeCalls = 0;

  const mock = createMockGoogleMaps({
    computeRequests,
    withPlaces: true,
    onGeocode: async () => {
      geocodeCalls++;
      return { results: [] };
    },
  });

  const api = await loadMapModule(mock);
  const { dotNetHelper } = createDotNetRecorder();

  await api.setAutocompleteValue("origin-input", "Saved Origin", {
    lat: 3.1234,
    lng: 101.5678,
  });
  await api.setAutocompleteValue("destination-input", "Saved Dest", {
    lat: 3.2234,
    lng: 101.6678,
  });
  await api.initAutocomplete(
    "origin-input",
    dotNetHelper,
    "UpdateOrigin",
    "test-api-key",
  );
  await api.initAutocomplete(
    "destination-input",
    dotNetHelper,
    "UpdateDestination",
    "test-api-key",
  );

  const routes = await api.getTransitRoutes("Saved Origin", "Saved Dest");
  assert.equal(routes.length, 1);
  assert.equal(geocodeCalls, 0);
  assert.deepEqual(plain(computeRequests[0].origin), {
    lat: 3.1234,
    lng: 101.5678,
  });
  assert.deepEqual(plain(computeRequests[0].destination), {
    lat: 3.2234,
    lng: 101.6678,
  });
});

test("handleUserMarkerDragEnd falls back to coordinates when reverseGeocode returns empty", async () => {
  const markerListeners = new Map();
  const mock = createMockGoogleMaps({
    computeRequests: [],
    markerListeners,
    onGeocode: async () => ({ results: [] }),
  });

  const { invocations, dotNetHelper } = createDotNetRecorder();
  const api = await loadMapModule(mock);
  await api.initGoogleMaps(
    "map",
    3.139,
    101.6869,
    14,
    "test-api-key",
    dotNetHelper,
    "LIGHT",
    "Asia/Kuala_Lumpur",
  );

  await markerListeners.get("gmp-dragend")();

  assert.equal(invocations[0][0], "UpdateUserPosition");
  assert.deepEqual(invocations[1], ["UpdateOrigin", "3.13900, 101.68690"]);
});

test("initGoogleMaps attaches TransitLayer to map and cleanupMap detaches it", async () => {
  const transitLayers = [];
  const api = await loadMapModule(createMockGoogleMaps({ transitLayers }));

  const map = await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "test-api-key",
    null,
    "DARK",
    "Asia/Kuala_Lumpur",
  );

  assert.equal(transitLayers.length, 1);
  assert.equal(transitLayers[0].getMap(), map);
  assert.deepEqual(transitLayers[0].setMapCalls, [map]);

  api.cleanupMap();

  assert.equal(transitLayers[0].getMap(), null);
  assert.deepEqual(transitLayers[0].setMapCalls, [map, null]);
});

const METERS_PER_DEGREE_LAT = (6371000 * Math.PI) / 180;

test("resolveVehicleHeading prefers a valid feed bearing", async () => {
  const api = await loadMapModule(createMockGoogleMaps());

  const state = api.resolveVehicleHeading(null, {
    lat: 5.4,
    lng: 100.3,
    bearing: 266,
  });

  assert.equal(state.heading, 266);
  assert.deepEqual({ ...state.anchor }, { lat: 5.4, lng: 100.3 });
});

test("resolveVehicleHeading treats bearing 0 as absent and derives heading from movement", async () => {
  const api = await loadMapModule(createMockGoogleMaps());

  const first = api.resolveVehicleHeading(null, {
    lat: 5.4,
    lng: 100.3,
    bearing: 0,
  });
  assert.equal(first.heading, null);

  const eastLng =
    100.3 + 100 / (METERS_PER_DEGREE_LAT * Math.cos((5.4 * Math.PI) / 180));
  const second = api.resolveVehicleHeading(first, {
    lat: 5.4,
    lng: eastLng,
    bearing: 0,
  });
  assert.ok(
    Math.abs(second.heading - 90) < 1,
    `expected ~90, got ${second.heading}`,
  );
});

test("resolveVehicleHeading keeps last heading and anchor below the movement threshold", async () => {
  const api = await loadMapModule(createMockGoogleMaps());
  const anchor = { lat: 5.4, lng: 100.3 };

  const state = api.resolveVehicleHeading(
    { heading: 45, anchor },
    { lat: 5.4 + 5 / METERS_PER_DEGREE_LAT, lng: 100.3, bearing: 0 },
  );

  assert.equal(state.heading, 45);
  assert.equal(state.anchor, anchor);
});

test("resolveVehicleHeading accumulates slow movement from a fixed anchor", async () => {
  const api = await loadMapModule(createMockGoogleMaps());
  const step = 6 / METERS_PER_DEGREE_LAT;

  let state = api.resolveVehicleHeading(null, {
    lat: 5.4,
    lng: 100.3,
    bearing: 0,
  });
  state = api.resolveVehicleHeading(state, {
    lat: 5.4 + step,
    lng: 100.3,
    bearing: 0,
  });
  assert.equal(state.heading, null);
  state = api.resolveVehicleHeading(state, {
    lat: 5.4 + step * 2,
    lng: 100.3,
    bearing: 0,
  });
  assert.equal(state.heading, null);
  state = api.resolveVehicleHeading(state, {
    lat: 5.4 + step * 3,
    lng: 100.3,
    bearing: 0,
  });

  assert.ok(Math.abs(state.heading) < 1, `expected ~0, got ${state.heading}`);
});

function createFakeElement(tagName = "div") {
  const classes = new Set();
  const attributes = {};
  const element = {
    tagName,
    children: [],
    dataset: {},
    textContent: "",
    style: {
      props: {},
      setProperty(name, value) {
        this.props[name] = value;
      },
      removeProperty(name) {
        delete this.props[name];
      },
    },
    get className() {
      return [...classes].join(" ");
    },
    set className(value) {
      classes.clear();
      String(value)
        .split(/\s+/)
        .filter(Boolean)
        .forEach((c) => classes.add(c));
    },
    classList: {
      toggle: (name, force) =>
        force ? classes.add(name) : classes.delete(name),
      contains: (name) => classes.has(name),
    },
    querySelector(selector) {
      const name = selector.replace(/^\./, "");
      return (
        element.children.find((child) => child.classList.contains(name)) ?? null
      );
    },
    replaceChildren(...nodes) {
      element.children = nodes;
    },
    setAttribute(name, value) {
      attributes[name] = String(value);
    },
    getAttribute(name) {
      return attributes[name] ?? null;
    },
    append: () => {},
    appendChild(node) {
      element.children.push(node);
      return node;
    },
    addEventListener: () => {},
  };
  return element;
}

function createFakeDomRecorder() {
  const created = [];
  const record = (tag) => {
    const element = createFakeElement(tag);
    created.push({ tag, element });
    return element;
  };
  return {
    created,
    documentOverrides: {
      createElement: record,
      createElementNS: (_ns, tag) => record(tag),
    },
  };
}

async function initMapForMarkers() {
  const { created, documentOverrides } = createFakeDomRecorder();
  const api = await loadMapModule(createMockGoogleMaps(), documentOverrides);
  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "test-api-key",
    null,
    "DARK",
    "Asia/Kuala_Lumpur",
  );
  created.length = 0;
  return { api, created };
}

test("getIconHeadingTransform flips east headings and tilts side-view emojis", async () => {
  const api = await loadMapModule(createMockGoogleMaps());

  assert.deepEqual(
    { ...api.getIconHeadingTransform(90) },
    { heading: 90, tilt: 0, scaleX: -1 },
  );
  assert.deepEqual(
    { ...api.getIconHeadingTransform(45) },
    { heading: 45, tilt: -45, scaleX: -1 },
  );
  assert.deepEqual(
    { ...api.getIconHeadingTransform(135) },
    { heading: 135, tilt: 45, scaleX: -1 },
  );
  assert.deepEqual(
    { ...api.getIconHeadingTransform(270) },
    { heading: 270, tilt: 0, scaleX: 1 },
  );
  assert.deepEqual(
    { ...api.getIconHeadingTransform(315) },
    { heading: 315, tilt: 45, scaleX: 1 },
  );
  assert.deepEqual(
    { ...api.getIconHeadingTransform(225) },
    { heading: 225, tilt: -45, scaleX: 1 },
  );
  assert.deepEqual(
    { ...api.getIconHeadingTransform(359.8) },
    { heading: 0, tilt: -90, scaleX: -1 },
  );
});

test("syncMarkers renders flipped/tilted bus emoji with a heading arrow and updates it", async () => {
  const { api, created } = await initMapForMarkers();

  await api.syncMarkers([
    {
      vehicleId: "V1",
      transportType: "Bus",
      lat: 3.14,
      lng: 101.69,
      bearing: 117,
    },
  ]);
  const content = created.find((c) => c.tag === "div").element;
  const icon = content.querySelector(".transport-marker-icon");

  assert.ok(content.classList.contains("transport-marker"));
  assert.ok(content.classList.contains("bus-marker"));
  assert.ok(content.classList.contains("has-heading"));
  assert.equal(content.style.props["--heading"], "117deg");
  assert.equal(content.style.props["--icon-tilt"], "27deg");
  assert.equal(content.style.props["--icon-scale-x"], "-1");
  assert.equal(icon.textContent, "🚌");
  assert.ok(content.querySelector(".transport-marker-arrow"));

  // Second snapshot: switching to westbound clears the horizontal flip in place.
  created.length = 0;
  await api.syncMarkers([
    {
      vehicleId: "V1",
      transportType: "Bus",
      lat: 3.1401,
      lng: 101.69,
      bearing: 266,
    },
  ]);

  assert.equal(created.length, 0);
  assert.equal(content.style.props["--heading"], "266deg");
  assert.equal(content.style.props["--icon-tilt"], "-4deg");
  assert.equal(content.style.props["--icon-scale-x"], "1");
  assert.equal(icon.textContent, "🚌");
});

test("syncMarkers uses side-facing rail emojis and toggles heading on movement", async () => {
  const { api, created } = await initMapForMarkers();

  await api.syncMarkers([
    {
      vehicleId: "T1",
      transportType: "KTMB",
      lat: 3.14,
      lng: 101.69,
      bearing: 0,
    },
    {
      vehicleId: "M1",
      transportType: "MRT",
      lat: 3.14,
      lng: 101.69,
      bearing: 270,
    },
    {
      vehicleId: "L1",
      transportType: "LRT",
      lat: 3.14,
      lng: 101.69,
      bearing: 45,
    },
  ]);
  const contents = created.filter((c) => c.tag === "div").map((c) => c.element);

  assert.ok(contents[0].classList.contains("ktmb-marker"));
  assert.equal(contents[0].classList.contains("has-heading"), false);
  assert.equal(
    contents[0].querySelector(".transport-marker-icon").textContent,
    "🚄",
  );

  assert.equal(
    contents[1].querySelector(".transport-marker-icon").textContent,
    "🚅",
  );
  assert.equal(contents[1].style.props["--icon-tilt"], "0deg");
  assert.equal(contents[1].style.props["--icon-scale-x"], "1");

  assert.equal(
    contents[2].querySelector(".transport-marker-icon").textContent,
    "🚈",
  );
  assert.equal(contents[2].style.props["--icon-tilt"], "-45deg");
  assert.equal(contents[2].style.props["--icon-scale-x"], "-1");

  // Moving KTMB 100 m east sets heading ~90deg and flips the emoji right.
  const eastLng =
    101.69 + 100 / (METERS_PER_DEGREE_LAT * Math.cos((3.14 * Math.PI) / 180));
  await api.syncMarkers([
    {
      vehicleId: "T1",
      transportType: "KTMB",
      lat: 3.14,
      lng: eastLng,
      bearing: 0,
    },
  ]);

  assert.equal(contents[0].classList.contains("has-heading"), true);
  assert.equal(contents[0].style.props["--heading"], "90deg");
  assert.equal(contents[0].style.props["--icon-tilt"], "0deg");
  assert.equal(contents[0].style.props["--icon-scale-x"], "-1");
});

test("syncMarkers keeps unknown station emoji upright while turning its arrow", async () => {
  const { api, created } = await initMapForMarkers();

  await api.syncMarkers([
    {
      vehicleId: "U1",
      transportType: "Other",
      lat: 3.14,
      lng: 101.69,
      bearing: 120,
    },
  ]);
  const content = created.find((c) => c.tag === "div").element;

  assert.equal(
    content.querySelector(".transport-marker-icon").textContent,
    "🚉",
  );
  assert.equal(content.style.props["--heading"], "120deg");
  assert.equal(content.style.props["--icon-tilt"], undefined);
  assert.equal(content.style.props["--icon-scale-x"], undefined);
});

test("syncMarkers uses custom text icon when location.icon is provided", async () => {
  const { api, created } = await initMapForMarkers();

  await api.syncMarkers([
    {
      vehicleId: "V2",
      transportType: "Bus",
      icon: "🚐",
      lat: 3.14,
      lng: 101.69,
      bearing: 45,
    },
  ]);
  const content = created.find((c) => c.tag === "div").element;
  const icon = content.querySelector(".transport-marker-icon");

  assert.equal(icon.textContent, "🚐");
  assert.equal(content.style.props["--heading"], "45deg");
  assert.equal(content.style.props["--icon-tilt"], "-45deg");
  assert.equal(content.style.props["--icon-scale-x"], "-1");
});

test("getTransitRoutes extracts merged walk segments, line colors, headways, and departure/arrival times", async () => {
  const computeRequests = [];
  const departAt = new Date("2026-11-17T01:53:00.000Z"); // 9:53 AM MYT
  const arriveAt = new Date("2026-11-17T02:12:00.000Z"); // 10:12 AM MYT
  const mock = createMockGoogleMaps({
    computeRequests,
    routesResult: () => ({
      routes: [
        {
          path: [
            { lat: 3.14, lng: 101.69 },
            { lat: 3.15, lng: 101.7 },
          ],
          durationMillis: 1860000,
          distanceMeters: 6400,
          localizedValues: { duration: "31 min", distance: "6.4 km" },
          createPolylines: () => [],
          legs: [
            {
              startLocation: { lat: 3.14, lng: 101.69 },
              endLocation: { lat: 3.15, lng: 101.7 },
              steps: [
                {
                  travelMode: "WALKING",
                  staticDurationMillis: 180000,
                  distanceMeters: 200,
                  instructions: "Walk to stop",
                },
                {
                  travelMode: "WALKING",
                  staticDurationMillis: 180000,
                  distanceMeters: 220,
                  instructions: "Cross street",
                },
                {
                  travelMode: "TRANSIT",
                  staticDurationMillis: 1140000,
                  distanceMeters: 5400,
                  transitDetails: {
                    headsign: "Bukit Bintang",
                    stopCount: 6,
                    headwayMillis: 600000,
                    departureTime: departAt,
                    arrivalTime: arriveAt,
                    departureStop: {
                      name: "Aeon Maluri 1",
                      location: { lat: 3.124, lng: 101.727 },
                    },
                    arrivalStop: {
                      name: "Bukit Bintang",
                      location: { lat: 3.147, lng: 101.711 },
                    },
                    transitLine: {
                      shortName: "400",
                      name: "Damai Perdana - Hab Lebuh Pudu",
                      color: "#1565c0",
                      textColor: "#ffffff",
                      vehicle: { vehicleType: "BUS" },
                    },
                  },
                },
                {
                  travelMode: "WALKING",
                  staticDurationMillis: 360000,
                  distanceMeters: 450,
                  instructions: "Walk to destination",
                },
              ],
            },
          ],
        },
      ],
    }),
  });

  const api = await loadMapModule(mock);
  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "test-api-key",
    null,
    "DARK",
    "Asia/Kuala_Lumpur",
  );

  const routes = await api.getTransitRoutes("Work", "Wisma Genting", {
    departureMode: "depart",
    dateTimeIso: "2026-11-17T09:47:00+08:00",
  });

  assert.equal(
    Object.prototype.toString.call(computeRequests[0].departureTime),
    "[object Date]",
  );
  assert.equal(
    computeRequests[0].departureTime.getTime(),
    new Date("2026-11-17T09:47:00+08:00").getTime(),
  );
  assert.equal(routes.length, 1);
  assert.equal(routes[0].localizedDuration, "31 min");
  assert.equal(routes[0].headwayMinutes, 10);
  assert.ok(routes[0].departureTimeText.length > 0);
  assert.ok(routes[0].arrivalTimeText.length > 0);
  // Consecutive initial WALKING steps are merged into one 6-minute walking segment.
  assert.equal(routes[0].segments.length, 3);
  assert.equal(routes[0].segments[0].travelMode, "WALKING");
  assert.equal(routes[0].segments[0].durationSeconds, 360);
  assert.equal(routes[0].segments[1].travelMode, "TRANSIT");
  assert.equal(routes[0].segments[1].lineShortName, "400");
  assert.equal(routes[0].segments[1].lineColor, "#1565c0");
  assert.equal(routes[0].segments[1].headwayMinutes, 10);
  assert.equal(routes[0].segments[1].stopCount, 6);
});

test("drawMultipleRoutes renders cached multi-modal polylines, callout pills, and switches active route cleanly", async () => {
  const polylineCalls = [];
  const createdMarkers = [];
  const { created, documentOverrides } = createFakeDomRecorder();

  class TrackingPolyline {
    constructor(opts = {}) {
      this.opts = opts;
      this.listeners = {};
    }
    setOptions(opts) {
      Object.assign(this.opts, opts);
    }
    setMap(m) {
      this.map = m;
    }
    addListener(name, cb) {
      this.listeners[name] = cb;
    }
  }

  const baseMock = createMockGoogleMaps({
    routesResult: () => ({
      routes: [
        {
          path: [
            { lat: 3.12, lng: 101.72 },
            { lat: 3.13, lng: 101.71 },
            { lat: 3.14, lng: 101.7 },
          ],
          durationMillis: 1860000,
          distanceMeters: 5000,
          localizedValues: { duration: "31 min" },
          createPolylines: (opts) => {
            polylineCalls.push(opts.colorScheme);
            const walkLine = new TrackingPolyline();
            const transitLine = new TrackingPolyline();
            const walkOpts = opts.polylineOptions(
              { strokeOpacity: 0, icons: [{ icon: { scale: 2.5 } }] },
              { travelMode: "WALKING" },
            );
            const transitOpts = opts.polylineOptions(
              { strokeColor: "#8ab4f8", strokeWeight: 6 },
              { travelMode: "TRANSIT" },
            );
            walkLine.setOptions(walkOpts);
            transitLine.setOptions(transitOpts);
            return [walkLine, transitLine];
          },
          legs: [
            {
              startLocation: { lat: 3.12, lng: 101.72 },
              endLocation: { lat: 3.14, lng: 101.7 },
              steps: [
                {
                  travelMode: "TRANSIT",
                  transitDetails: {
                    headwayMillis: 600000,
                    transitLine: { shortName: "400" },
                    departureStop: {
                      name: "Stop A",
                      location: { lat: 3.12, lng: 101.72 },
                    },
                  },
                },
              ],
            },
          ],
        },
        {
          path: [
            { lat: 3.12, lng: 101.72 },
            { lat: 3.135, lng: 101.725 },
            { lat: 3.14, lng: 101.7 },
          ],
          durationMillis: 2220000,
          distanceMeters: 6000,
          localizedValues: { duration: "37 min" },
          createPolylines: (opts) => {
            polylineCalls.push(opts.colorScheme);
            const transitLine = new TrackingPolyline();
            const transitOpts = opts.polylineOptions(
              { strokeColor: "#34a853", strokeWeight: 6 },
              { travelMode: "TRANSIT" },
            );
            transitLine.setOptions(transitOpts);
            return [transitLine];
          },
          legs: [
            {
              startLocation: { lat: 3.12, lng: 101.72 },
              endLocation: { lat: 3.14, lng: 101.7 },
              steps: [
                {
                  travelMode: "TRANSIT",
                  transitDetails: {
                    headwayMillis: 600000,
                    transitLine: { shortName: "KGL" },
                    departureStop: {
                      name: "Stop B",
                      location: { lat: 3.12, lng: 101.72 },
                    },
                  },
                },
              ],
            },
          ],
        },
      ],
    }),
  });

  const origImport = baseMock.maps.importLibrary;
  baseMock.maps.importLibrary = async (lib) => {
    if (lib === "marker") {
      const mod = await origImport(lib);
      return {
        ...mod,
        AdvancedMarkerElement: class extends mod.AdvancedMarkerElement {
          constructor(opts) {
            super(opts);
            createdMarkers.push(this);
          }
        },
      };
    }
    return origImport(lib);
  };

  const api = await loadMapModule(baseMock, documentOverrides);
  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "test-api-key",
    null,
    "DARK",
    "Asia/Kuala_Lumpur",
  );
  createdMarkers.length = 0;

  const routes = await api.getTransitRoutes("Work", "Wisma Genting");
  const { invocations, dotNetHelper } = createDotNetRecorder();

  await api.drawMultipleRoutes(
    routes.map((r) => ({
      id: r.routeId,
      path: r.encodedPolyline,
      hasLiveBus: false,
      durationSeconds: r.durationSeconds,
      localizedDuration: r.localizedDuration,
      headwayMinutes: r.headwayMinutes,
    })),
    0,
    dotNetHelper,
  );

  assert.deepEqual(polylineCalls, ["DARK", "DARK"]);
  const pills = created.filter((c) => c.tag === "button").map((c) => c.element);
  assert.equal(pills.length, 2);
  // Inactive route is drawn first, active route second.
  assert.equal(pills[0].className, "route-callout-pill");
  assert.equal(
    pills[1].className,
    "route-callout-pill route-callout-pill--active",
  );

  api.setActiveRoute(1);
  assert.equal(
    pills[0].className,
    "route-callout-pill route-callout-pill--active",
  );
  assert.equal(pills[1].className, "route-callout-pill");
  assert.equal(invocations.length, 0);
});

test("swapAutocompleteLocations swaps cached text and coordinates cleanly", async () => {
  const computeRequests = [];
  let geocodeCalls = 0;
  const api = await loadMapModule(
    createMockGoogleMaps({
      computeRequests,
      onGeocode: async () => {
        geocodeCalls++;
        return { results: [] };
      },
    }),
  );

  await api.setAutocompleteValue("origin-input", "Origin Place", {
    lat: 3.11,
    lng: 101.61,
  });
  await api.setAutocompleteValue("destination-input", "Dest Place", {
    lat: 3.22,
    lng: 101.72,
  });
  api.swapAutocompleteLocations("origin-input", "destination-input");

  await api.getTransitRoutes("Dest Place", "Origin Place");
  assert.equal(geocodeCalls, 0);
  assert.deepEqual(plain(computeRequests[0].origin), {
    lat: 3.22,
    lng: 101.72,
  });
  assert.deepEqual(plain(computeRequests[0].destination), {
    lat: 3.11,
    lng: 101.61,
  });
});

test("syncMarkers prefers routeShortName over routeId for marker title and info window", async () => {
  const createdMarkers = [];
  const openedInfo = [];
  class RecordingMarker {
    constructor(opts) {
      this.listeners = {};
      Object.assign(this, opts);
      createdMarkers.push(this);
    }
    addEventListener(type, fn) {
      this.listeners[type] = fn;
    }
    removeEventListener() {}
  }
  class RecordingInfoWindow {
    setContent(content) {
      this.lastContent = content;
    }
    open({ anchor }) {
      openedInfo.push({ anchor, content: this.lastContent });
    }
    close() {}
  }

  const mock = createMockGoogleMaps();
  const origImport = mock.maps.importLibrary;
  mock.maps.importLibrary = async (lib) => {
    if (lib === "maps") {
      const m = await origImport(lib);
      return { ...m, InfoWindow: RecordingInfoWindow };
    }
    if (lib === "marker") {
      const m = await origImport(lib);
      return { ...m, AdvancedMarkerElement: RecordingMarker };
    }
    return origImport(lib);
  };

  const makeNode = (tag) => ({
    tag,
    style: { setProperty() {}, removeProperty() {} },
    classList: { toggle() {} },
    children: [],
    textContent: "",
    setAttribute() {},
    querySelector() {
      return null;
    },
    replaceChildren(...items) {
      this.children = [...items];
    },
    append(...items) {
      this.children.push(...items);
    },
    appendChild(item) {
      this.children.push(item);
    },
    addEventListener() {},
  });
  const api = await loadMapModule(mock, {
    createElement: makeNode,
    createElementNS: makeNode,
    getElementById: () => makeNode("div"),
  });

  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "test-api-key",
    null,
    "DARK",
    "Asia/Kuala_Lumpur",
  );
  createdMarkers.length = 0;
  await api.syncMarkers([
    {
      providerName: "Rapid Bus KL",
      transportType: "Bus",
      routeId: "U4200",
      routeShortName: "420",
      vehicleId: "BUS-420",
      lat: 3.14,
      lng: 101.69,
      speed: 5,
      timestamp: 1794885820,
    },
    {
      providerName: "Rapid Bus Penang",
      transportType: "Bus",
      routeId: "401",
      vehicleId: "BUS-401",
      lat: 5.41,
      lng: 100.33,
      speed: 0,
      timestamp: 1794885820,
    },
  ]);

  assert.equal(createdMarkers.length, 2);
  assert.equal(createdMarkers[0].title, "Route 420 - BUS-420");
  assert.equal(createdMarkers[1].title, "Route 401 - BUS-401");

  createdMarkers[0].listeners["gmp-click"]();
  const infoText = JSON.stringify(openedInfo[0].content);
  assert.match(infoText, /"420"/);
  assert.doesNotMatch(infoText, /U4200/);
});

test("clicking route callout pill invokes OnRouteSelected even for active route", async () => {
  const calloutMarkers = [];
  const baseMock = createMockGoogleMaps();
  const origImport = baseMock.maps.importLibrary;
  baseMock.maps.importLibrary = async (lib) => {
    if (lib === "marker") {
      const mod = await origImport(lib);
      return {
        ...mod,
        AdvancedMarkerElement: class extends mod.AdvancedMarkerElement {
          constructor(opts) {
            super(opts);
            this.listeners = {};
            calloutMarkers.push(this);
          }
          addEventListener(type, fn) {
            this.listeners[type] = fn;
          }
        },
      };
    }
    return origImport(lib);
  };

  const api = await loadMapModule(baseMock, {
    createElement: (tag) => ({
      tag,
      style: {},
      className: "",
      children: [],
      setAttribute() {},
      append(...items) {
        this.children.push(...items);
      },
      appendChild(item) {
        this.children.push(item);
      },
      addEventListener() {},
    }),
  });

  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "test-api-key",
    null,
    "DARK",
    "Asia/Kuala_Lumpur",
  );
  calloutMarkers.length = 0;
  const routes = await api.getTransitRoutes("Work", "Wisma Genting");
  const { invocations, dotNetHelper } = createDotNetRecorder();

  await api.drawMultipleRoutes(
    routes.map((r) => ({
      id: r.routeId,
      path: r.encodedPolyline,
      hasLiveBus: false,
      durationSeconds: r.durationSeconds,
      localizedDuration: r.localizedDuration,
      headwayMinutes: r.headwayMinutes,
    })),
    0,
    dotNetHelper,
  );

  const activeMarker = calloutMarkers.find((m) =>
    m.content?.className?.includes("route-callout-pill--active"),
  );
  assert.ok(activeMarker);
  activeMarker.listeners["gmp-click"]();
  assert.deepEqual(invocations, [["OnRouteSelected", 0]]);
});

test("clicking bus marker toggles info window and invokes OnVehicleSelected then OnVehicleDeselected", async () => {
  const createdMarkers = [];
  const openedInfo = [];
  let closedCount = 0;
  class RecordingMarker {
    constructor(opts) {
      this.listeners = {};
      Object.assign(this, opts);
      createdMarkers.push(this);
    }
    addEventListener(type, fn) {
      this.listeners[type] = fn;
    }
    removeEventListener() {}
  }
  class RecordingInfoWindow {
    setContent(content) {
      this.lastContent = content;
    }
    open({ anchor }) {
      openedInfo.push({ anchor, content: this.lastContent });
    }
    close() {
      closedCount++;
    }
  }

  const mock = createMockGoogleMaps();
  const origImport = mock.maps.importLibrary;
  mock.maps.importLibrary = async (lib) => {
    if (lib === "maps") {
      const m = await origImport(lib);
      return { ...m, InfoWindow: RecordingInfoWindow };
    }
    if (lib === "marker") {
      const m = await origImport(lib);
      return { ...m, AdvancedMarkerElement: RecordingMarker };
    }
    return origImport(lib);
  };

  const makeNode = (tag) => ({
    tag,
    style: { setProperty() {}, removeProperty() {} },
    classList: { toggle() {} },
    children: [],
    textContent: "",
    setAttribute() {},
    querySelector() {
      return null;
    },
    replaceChildren(...items) {
      this.children = [...items];
    },
    append(...items) {
      this.children.push(...items);
    },
    appendChild(item) {
      this.children.push(item);
    },
    addEventListener() {},
  });
  const api = await loadMapModule(mock, {
    createElement: makeNode,
    createElementNS: makeNode,
    getElementById: () => makeNode("div"),
  });

  const { invocations, dotNetHelper } = createDotNetRecorder();
  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "test-api-key",
    dotNetHelper,
    "DARK",
    "Asia/Kuala_Lumpur",
  );
  createdMarkers.length = 0;

  await api.syncMarkers([
    {
      providerName: "Rapid Bus KL",
      transportType: "Bus",
      routeId: "U4200",
      routeShortName: "420",
      vehicleId: "BUS-420",
      lat: 3.14,
      lng: 101.69,
      speed: 5,
      timestamp: 1794885820,
    },
  ]);

  assert.equal(createdMarkers.length, 1);

  // First click opens info window and requests the full route line.
  createdMarkers[0].listeners["gmp-click"]();
  assert.equal(openedInfo.length, 1);
  assert.deepEqual(invocations, [
    ["OnVehicleSelected", "Rapid Bus KL", "U4200", "420"],
  ]);

  // Second click closes info window and turns off the full route.
  createdMarkers[0].listeners["gmp-click"]();
  assert.equal(closedCount, 1);
  assert.deepEqual(invocations, [
    ["OnVehicleSelected", "Rapid Bus KL", "U4200", "420"],
    ["OnVehicleDeselected", "Rapid Bus KL"],
  ]);
});

test("syncMarkers skips DOM and style mutations when vehicle snapshot is unchanged", async () => {
  let styleWrites = 0;
  let classToggles = 0;
  class TrackingMarker {
    constructor(opts) {
      this.listeners = {};
      Object.assign(this, opts);
      this.positionWrites = 0;
      this._pos = opts.position;
      Object.defineProperty(this, "position", {
        get: () => this._pos,
        set: (v) => {
          this._pos = v;
          this.positionWrites++;
        },
      });
    }
    addEventListener(type, fn) {
      this.listeners[type] = fn;
    }
    removeEventListener() {}
  }

  const mock = createMockGoogleMaps();
  const origImport = mock.maps.importLibrary;
  mock.maps.importLibrary = async (lib) => {
    if (lib === "marker") {
      const m = await origImport(lib);
      return { ...m, AdvancedMarkerElement: TrackingMarker };
    }
    return origImport(lib);
  };

  const makeNode = (tag) => ({
    tag,
    style: {
      setProperty() {
        styleWrites++;
      },
      removeProperty() {
        styleWrites++;
      },
    },
    classList: {
      toggle() {
        classToggles++;
      },
    },
    children: [],
    textContent: "",
    setAttribute() {},
    querySelector() {
      return this.children[1] || null;
    },
    replaceChildren(...items) {
      this.children = [...items];
    },
    append(...items) {
      this.children.push(...items);
    },
    appendChild(item) {
      this.children.push(item);
    },
    addEventListener() {},
  });
  const api = await loadMapModule(mock, {
    createElement: makeNode,
    createElementNS: makeNode,
    getElementById: () => makeNode("div"),
  });

  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "test-api-key",
    null,
    "DARK",
    "Asia/Kuala_Lumpur",
  );

  const vehicle = {
    providerName: "Rapid Bus KL",
    transportType: "Bus",
    routeId: "U4200",
    routeShortName: "420",
    vehicleId: "BUS-420",
    lat: 3.14,
    lng: 101.69,
    bearing: 90,
    speed: 5,
    timestamp: 1794885820,
  };

  await api.syncMarkers([vehicle]);
  const writesAfterFirstSync = styleWrites;
  const togglesAfterFirstSync = classToggles;

  await api.syncMarkers([{ ...vehicle }]);
  assert.equal(styleWrites, writesAfterFirstSync);
  assert.equal(classToggles, togglesAfterFirstSync);
});

test("route polylines render repeated directional chevrons on non-walking segments only", async () => {
  const createdPolylines = [];
  const { documentOverrides } = createFakeDomRecorder();

  class TrackingPolyline {
    constructor(opts = {}) {
      this.opts = opts;
      this.listeners = {};
      createdPolylines.push(this);
    }
    setOptions(opts) {
      Object.assign(this.opts, opts);
    }
    setMap(m) {
      this.map = m;
    }
    addListener(name, cb) {
      this.listeners[name] = cb;
    }
    getPath() {
      return (
        this.opts.path || [
          { lat: 3.12, lng: 101.72 },
          { lat: 3.13, lng: 101.71 },
        ]
      );
    }
  }

  let capturedWalkOpts = null;
  let capturedTransitActiveOpts = null;
  let capturedTransitInactiveOpts = null;

  const baseMock = createMockGoogleMaps({
    routesResult: () => ({
      routes: [
        {
          path: [
            { lat: 3.12, lng: 101.72 },
            { lat: 3.13, lng: 101.71 },
          ],
          durationMillis: 900000,
          distanceMeters: 4000,
          localizedValues: { duration: "15 min" },
          createPolylines: (opts) => {
            const walkLine = new TrackingPolyline();
            const transitLine = new TrackingPolyline();
            capturedWalkOpts = opts.polylineOptions(
              { strokeOpacity: 0, icons: [{ icon: { scale: 2.5 } }] },
              { travelMode: "WALKING" },
            );
            capturedTransitActiveOpts = opts.polylineOptions(
              { strokeColor: "#8ab4f8", strokeWeight: 6 },
              { travelMode: "TRANSIT" },
            );
            walkLine.setOptions(capturedWalkOpts);
            transitLine.setOptions(capturedTransitActiveOpts);
            return [walkLine, transitLine];
          },
          legs: [
            {
              startLocation: { lat: 3.12, lng: 101.72 },
              endLocation: { lat: 3.13, lng: 101.71 },
              steps: [],
            },
          ],
        },
        {
          path: [
            { lat: 3.12, lng: 101.72 },
            { lat: 3.14, lng: 101.7 },
          ],
          durationMillis: 1200000,
          distanceMeters: 5000,
          localizedValues: { duration: "20 min" },
          createPolylines: (opts) => {
            const transitLine = new TrackingPolyline();
            capturedTransitInactiveOpts = opts.polylineOptions(
              { strokeColor: "#34a853", strokeWeight: 6 },
              { travelMode: "TRANSIT" },
            );
            transitLine.setOptions(capturedTransitInactiveOpts);
            return [transitLine];
          },
          legs: [
            {
              startLocation: { lat: 3.12, lng: 101.72 },
              endLocation: { lat: 3.14, lng: 101.7 },
              steps: [],
            },
          ],
        },
      ],
    }),
  });

  const origImport = baseMock.maps.importLibrary;
  baseMock.maps.importLibrary = async (lib) => {
    if (lib === "maps") {
      const m = await origImport(lib);
      return { ...m, Polyline: TrackingPolyline };
    }
    return origImport(lib);
  };

  const api = await loadMapModule(baseMock, documentOverrides);
  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "test-api-key",
    null,
    "DARK",
    "Asia/Kuala_Lumpur",
  );

  const routes = await api.getTransitRoutes("KL Sentral", "KLCC");
  const { dotNetHelper } = createDotNetRecorder();

  createdPolylines.length = 0;
  await api.drawMultipleRoutes(
    routes.map((r) => ({
      id: r.routeId,
      path: r.encodedPolyline,
      hasLiveBus: false,
      durationSeconds: r.durationSeconds,
    })),
    0,
    dotNetHelper,
  );

  assert.equal(capturedWalkOpts.icons.length, 1);
  assert.equal(capturedWalkOpts.icons[0].repeat, undefined);
  assert.equal(capturedTransitActiveOpts.icons, undefined);
  assert.equal(capturedTransitInactiveOpts.icons, undefined);

  // Two overlay arrow polylines are created: inactive route 1 (zIndex 15) and active route 0 (zIndex 120).
  const arrowOverlays = createdPolylines.filter(
    (p) => p.opts.strokeOpacity === 0 && p.opts.clickable === false,
  );
  assert.equal(arrowOverlays.length, 2);
  const inactiveOverlay = arrowOverlays[0];
  const activeOverlay = arrowOverlays[1];

  assert.equal(inactiveOverlay.opts.zIndex, 15);
  assert.equal(inactiveOverlay.opts.icons.length, 1);
  assert.equal(inactiveOverlay.opts.icons[0].repeat, "48px");

  assert.equal(activeOverlay.opts.zIndex, 120);
  assert.equal(activeOverlay.opts.icons.length, 1);
  assert.equal(activeOverlay.opts.icons[0].repeat, "48px");
  assert.ok(activeOverlay.opts.icons[0].offset.endsWith("px"));
  assert.equal(
    activeOverlay.opts.icons[0].icon.path,
    "M -2.5,1.8 L 0,-2.2 L 2.5,1.8 L 0,0.4 Z",
  );
  assert.ok(
    inactiveOverlay.opts.icons[0].icon.scale <
      activeOverlay.opts.icons[0].icon.scale,
  );

  // Switching active route updates overlay zIndex so the newly active route arrows sit on top.
  api.setActiveRoute(1);
  assert.equal(inactiveOverlay.opts.zIndex, 120);
  assert.equal(activeOverlay.opts.zIndex, 15);

  // Fallback polyline path (when no cached route object exists) also creates a base + arrow overlay pair.
  createdPolylines.length = 0;
  await api.drawMultipleRoutes(
    [{ id: 99, path: "fallback", hasLiveBus: false }],
    99,
    dotNetHelper,
  );
  assert.equal(createdPolylines.length, 2);
  assert.equal(createdPolylines[0].opts.zIndex, 100);
  assert.equal(createdPolylines[1].opts.zIndex, 120);
  assert.equal(createdPolylines[1].opts.strokeOpacity, 0);
  assert.equal(createdPolylines[1].opts.icons.length, 1);
  assert.equal(createdPolylines[1].opts.icons[0].repeat, "48px");
  assert.ok(createdPolylines[1].opts.icons[0].offset.endsWith("px"));
  api.clearRoute();
});

test("showStaticRoute draws route LineStrings as Polylines with repeated chevrons and keeps stops in Data layer", async () => {
  const createdPolylines = [];
  const dataFeaturesAdded = [];
  const dataFeaturesRemoved = [];

  class TrackingPolyline {
    constructor(opts = {}) {
      this.opts = opts;
      this.setOptionsCalls = 0;
      createdPolylines.push(this);
    }
    setOptions(opts) {
      this.setOptionsCalls++;
      Object.assign(this.opts, opts);
    }
    setMap(m) {
      this.map = m;
    }
  }

  class MockDataLayer {
    constructor() {
      this.features = [];
    }
    setStyle() {}
    addListener() {}
    addGeoJson(fc) {
      dataFeaturesAdded.push(...fc.features);
      const wrapped = fc.features.map((f) => ({
        raw: f,
        getGeometry: () => ({
          forEachLatLng: (cb) => {
            if (f.geometry?.type === "Point") {
              cb({
                lat: f.geometry.coordinates[1],
                lng: f.geometry.coordinates[0],
              });
            }
          },
        }),
      }));
      this.features.push(...wrapped);
      return wrapped;
    }
    forEach(cb) {
      this.features.forEach(cb);
    }
    remove(f) {
      dataFeaturesRemoved.push(f);
      this.features = this.features.filter((item) => item !== f);
    }
  }

  const mock = createMockGoogleMaps();
  mock.maps.Data = MockDataLayer;
  mock.maps.SymbolPath = { CIRCLE: 0 };
  const origImport = mock.maps.importLibrary;
  mock.maps.importLibrary = async (lib) => {
    if (lib === "maps") {
      const m = await origImport(lib);
      return { ...m, Polyline: TrackingPolyline };
    }
    return origImport(lib);
  };

  const api = await loadMapModule(mock);
  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "test-api-key",
    null,
    "DARK",
    "Asia/Kuala_Lumpur",
  );

  const geoJson = JSON.stringify({
    type: "FeatureCollection",
    features: [
      {
        type: "Feature",
        geometry: {
          type: "LineString",
          coordinates: [
            [101.69, 3.14],
            [101.7, 3.15],
          ],
        },
        properties: { kind: "route", route_color: "e91e63" },
      },
      {
        type: "Feature",
        geometry: { type: "Point", coordinates: [101.69, 3.14] },
        properties: { kind: "stop", stop_id: "S1", stop_name: "KL Sentral" },
      },
    ],
  });

  await api.showStaticRoute(geoJson, null);

  assert.equal(createdPolylines.length, 2);
  assert.deepEqual(plain(createdPolylines[0].opts.path), [
    { lat: 3.14, lng: 101.69 },
    { lat: 3.15, lng: 101.7 },
  ]);
  assert.equal(createdPolylines[0].opts.strokeColor, "#e91e63");
  assert.equal(createdPolylines[0].opts.strokeWeight, 6);
  assert.equal(createdPolylines[0].opts.zIndex, 1);
  assert.equal(createdPolylines[0].opts.icons, undefined);

  assert.equal(createdPolylines[1].opts.strokeOpacity, 0);
  assert.equal(createdPolylines[1].opts.clickable, false);
  assert.equal(createdPolylines[1].opts.zIndex, 5);
  assert.equal(createdPolylines[1].opts.icons.length, 1);
  assert.equal(createdPolylines[1].opts.icons[0].repeat, "48px");
  assert.equal(
    createdPolylines[1].opts.icons[0].icon.path,
    "M -2.5,1.8 L 0,-2.2 L 2.5,1.8 L 0,0.4 Z",
  );

  assert.ok(createdPolylines[1].opts.icons[0].offset.endsWith("px"));
  const initialOffset = createdPolylines[1].opts.icons[0].offset;
  await new Promise((resolve) => setTimeout(resolve, 100));
  const nextOffset = createdPolylines[1].opts.icons[0].offset;
  assert.notEqual(nextOffset, initialOffset);
  assert.ok(parseFloat(nextOffset) > parseFloat(initialOffset));

  assert.equal(dataFeaturesAdded.length, 1);
  assert.equal(dataFeaturesAdded[0].properties.kind, "stop");

  api.clearStaticRoute();
  assert.equal(createdPolylines[0].map, null);
  assert.equal(createdPolylines[1].map, null);
  assert.equal(dataFeaturesRemoved.length, 1);
});

test("route arrow breath animation respects prefers-reduced-motion", async () => {
  const createdPolylines = [];
  class TrackingPolyline {
    constructor(opts = {}) {
      this.opts = opts;
      this.setOptionsCalls = 0;
      createdPolylines.push(this);
    }
    setOptions(opts) {
      this.setOptionsCalls++;
      Object.assign(this.opts, opts);
    }
    setMap(m) {
      this.map = m;
    }
  }

  const mock = createMockGoogleMaps();
  const origImport = mock.maps.importLibrary;
  mock.maps.importLibrary = async (lib) => {
    if (lib === "maps") {
      const m = await origImport(lib);
      return { ...m, Polyline: TrackingPolyline };
    }
    return origImport(lib);
  };

  const api = await loadMapModule(
    mock,
    {},
    {
      matchMedia: (query) => ({
        matches: query === "(prefers-reduced-motion: reduce)",
      }),
    },
  );
  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "test-api-key",
    null,
    "DARK",
    "Asia/Kuala_Lumpur",
  );

  await api.showStaticRoute(
    JSON.stringify({
      type: "FeatureCollection",
      features: [
        {
          type: "Feature",
          geometry: {
            type: "LineString",
            coordinates: [
              [101.69, 3.14],
              [101.7, 3.15],
            ],
          },
          properties: { kind: "route" },
        },
      ],
    }),
    null,
  );

  await new Promise((resolve) => setTimeout(resolve, 110));
  assert.equal(createdPolylines.length, 2);
  assert.equal(createdPolylines[0].setOptionsCalls, 0);
  assert.equal(createdPolylines[1].setOptionsCalls, 0);
  api.clearStaticRoute();
});

test("route arrow animation uses throttled requestAnimationFrame, polyline.set, and pauses when hidden", async () => {
  const createdPolylines = [];
  class TrackingPolyline {
    constructor(opts = {}) {
      this.opts = opts;
      this.setIconsCalls = 0;
      this.setOptionsCalls = 0;
      createdPolylines.push(this);
    }
    set(key, value) {
      if (key === "icons") {
        this.setIconsCalls++;
        this.opts.icons = value;
      }
    }
    setOptions(opts) {
      this.setOptionsCalls++;
      Object.assign(this.opts, opts);
    }
    setMap(m) {
      this.map = m;
    }
  }

  const mock = createMockGoogleMaps();
  const origImport = mock.maps.importLibrary;
  mock.maps.importLibrary = async (lib) => {
    if (lib === "maps") {
      const m = await origImport(lib);
      return { ...m, Polyline: TrackingPolyline };
    }
    return origImport(lib);
  };

  let nextFrameId = 1;
  const scheduledFrames = new Map();
  let cancelledFrames = 0;
  let isDocHidden = false;
  const docListeners = new Map();
  const docState = {
    get hidden() {
      return isDocHidden;
    },
    addEventListener: (eventName, cb) => docListeners.set(eventName, cb),
  };

  const api = await loadMapModule(mock, docState, {}, {
    requestAnimationFrame: (cb) => {
      const id = nextFrameId++;
      scheduledFrames.set(id, cb);
      return id;
    },
    cancelAnimationFrame: (id) => {
      if (scheduledFrames.delete(id)) cancelledFrames++;
    },
  });

  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "test-api-key",
    null,
    "DARK",
    "Asia/Kuala_Lumpur",
  );

  await api.showStaticRoute(
    JSON.stringify({
      type: "FeatureCollection",
      features: [
        {
          type: "Feature",
          geometry: {
            type: "LineString",
            coordinates: [
              [101.69, 3.14],
              [101.7, 3.15],
            ],
          },
          properties: { kind: "route" },
        },
      ],
    }),
    null,
  );

  const arrow = createdPolylines[1];
  const runNextFrame = (ts) => {
    const first = scheduledFrames.entries().next();
    assert.equal(first.done, false);
    const [id, cb] = first.value;
    scheduledFrames.delete(id);
    cb(ts);
  };

  // First rAF sets baseline timestamp; 16ms frame does not update icons; 45ms frame advances once via polyline.set.
  runNextFrame(100);
  runNextFrame(116);
  assert.equal(arrow.setIconsCalls, 0);

  runNextFrame(145);
  assert.equal(arrow.setIconsCalls, 1);
  assert.equal(arrow.setOptionsCalls, 0);
  assert.equal(arrow.opts.icons[0].offset, "1.5px");

  // Hiding document cancels pending rAF and stops scheduling while hidden.
  isDocHidden = true;
  docListeners.get("visibilitychange")?.();
  assert.equal(scheduledFrames.size, 0);
  assert.ok(cancelledFrames >= 1);

  // Restoring visibility resumes rAF.
  isDocHidden = false;
  docListeners.get("visibilitychange")?.();
  assert.equal(scheduledFrames.size, 1);

  api.clearStaticRoute();
  assert.equal(scheduledFrames.size, 0);
});

test("drawMultipleRoutes keeps first option static until a Live Route Option is selected", async () => {
  const createdPolylines = [];
  class TrackingPolyline {
    constructor(opts = {}) {
      this.opts = opts;
      this.setIconsCalls = 0;
      this.listeners = {};
      createdPolylines.push(this);
    }
    set(key, value) {
      if (key === "icons") {
        this.setIconsCalls++;
        this.opts.icons = value;
      }
    }
    setOptions(opts) {
      Object.assign(this.opts, opts);
    }
    setMap(m) {
      this.map = m;
    }
    addListener(name, cb) {
      this.listeners[name] = cb;
    }
    getPath() {
      return [
        { lat: 3.12, lng: 101.72 },
        { lat: 3.13, lng: 101.71 },
      ];
    }
  }

  const baseMock = createMockGoogleMaps({
    routesResult: () => ({
      routes: [
        {
          path: [
            { lat: 3.12, lng: 101.72 },
            { lat: 3.13, lng: 101.71 },
          ],
          durationMillis: 900000,
          distanceMeters: 4000,
          localizedValues: { duration: "15 min" },
          createPolylines: (opts) => {
            const line = new TrackingPolyline();
            line.setOptions(opts.polylineOptions({ strokeColor: "#8ab4f8", strokeWeight: 6 }, { travelMode: "TRANSIT" }));
            return [line];
          },
          legs: [{ startLocation: { lat: 3.12, lng: 101.72 }, endLocation: { lat: 3.13, lng: 101.71 }, steps: [] }],
        },
        {
          path: [
            { lat: 3.12, lng: 101.72 },
            { lat: 3.14, lng: 101.7 },
          ],
          durationMillis: 1200000,
          distanceMeters: 5000,
          localizedValues: { duration: "20 min" },
          createPolylines: (opts) => {
            const line = new TrackingPolyline();
            line.setOptions(opts.polylineOptions({ strokeColor: "#34a853", strokeWeight: 6 }, { travelMode: "TRANSIT" }));
            return [line];
          },
          legs: [{ startLocation: { lat: 3.12, lng: 101.72 }, endLocation: { lat: 3.14, lng: 101.7 }, steps: [] }],
        },
      ],
    }),
  });

  const origImport = baseMock.maps.importLibrary;
  baseMock.maps.importLibrary = async (lib) => {
    if (lib === "maps") {
      const m = await origImport(lib);
      return { ...m, Polyline: TrackingPolyline };
    }
    return origImport(lib);
  };

  let nextFrameId = 1;
  const scheduledFrames = new Map();
  const api = await loadMapModule(baseMock, {}, {}, {
    requestAnimationFrame: (cb) => {
      const id = nextFrameId++;
      scheduledFrames.set(id, cb);
      return id;
    },
    cancelAnimationFrame: (id) => {
      scheduledFrames.delete(id);
    },
  });

  await api.initGoogleMaps("map", 3.14, 101.69, 13, "test-api-key", null, "DARK", "Asia/Kuala_Lumpur");
  const routes = await api.getTransitRoutes("KL Sentral", "KLCC");
  const { dotNetHelper } = createDotNetRecorder();

  createdPolylines.length = 0;
  await api.drawMultipleRoutes(
    routes.map((r) => ({
      id: r.routeId,
      path: r.encodedPolyline,
      hasLiveBus: false,
      durationSeconds: r.durationSeconds,
      localizedDuration: r.localizedDuration,
    })),
    0,
    dotNetHelper,
  );

  // Initial draw highlights option 0 statically without starting animation.
  assert.equal(scheduledFrames.size, 0);
  const arrowOverlays = createdPolylines.filter((p) => p.opts.strokeOpacity === 0 && p.opts.clickable === false);
  assert.equal(arrowOverlays.length, 2);
  const route1Arrow = arrowOverlays[0];
  const route0Arrow = arrowOverlays[1];
  assert.equal(route0Arrow.opts.icons[0].offset, "24px");
  assert.equal(route1Arrow.opts.icons[0].offset, "24px");

  const runNextFrame = (ts) => {
    const first = scheduledFrames.entries().next();
    assert.equal(first.done, false);
    const [id, cb] = first.value;
    scheduledFrames.delete(id);
    cb(ts);
  };

  // Selecting route 1 enables animation ONLY for route 1.
  api.setActiveRoute(1);
  assert.equal(scheduledFrames.size, 1);
  runNextFrame(100);
  runNextFrame(145);
  assert.equal(route1Arrow.setIconsCalls, 1);
  assert.equal(route0Arrow.setIconsCalls, 0);

  // Switching to route 0 stops animating route 1 and animates route 0.
  api.setActiveRoute(0);
  runNextFrame(190);
  assert.equal(route1Arrow.setIconsCalls, 1);
  assert.equal(route0Arrow.setIconsCalls, 1);

  api.clearRoute();
  assert.equal(scheduledFrames.size, 0);
});

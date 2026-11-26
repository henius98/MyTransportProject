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
                this.attributes = {};
                this.placeholder = "";
              }
              setAttribute(name, value) {
                this.attributes[name] = value;
              }
              getAttribute(name) {
                return this.attributes[name] ?? null;
              }
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

  const win = Object.defineProperties(
    { google: googleMock },
    Object.getOwnPropertyDescriptors(windowOverrides),
  );

  const context = createContext({
    URLSearchParams,
    Number,
    setInterval,
    clearInterval,
    document: doc,
    window: win,
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
    append(...nodes) {
      element.children.push(...nodes);
    },
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

test("selected bus marker stays emphasized after map click while static route is shown and cancels on second click", async () => {
  const createdMarkers = [];
  const mapListeners = {};
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
    setContent() {}
    open() {}
    close() {
      closedCount++;
    }
  }

  const mock = createMockGoogleMaps();
  const origImport = mock.maps.importLibrary;
  mock.maps.importLibrary = async (lib) => {
    const m = await origImport(lib);
    if (lib === "maps") {
      class RecordingMap extends m.Map {
        addListener(name, fn) {
          mapListeners[name] = fn;
        }
      }
      return { ...m, Map: RecordingMap, InfoWindow: RecordingInfoWindow };
    }
    if (lib === "marker") {
      return { ...m, AdvancedMarkerElement: RecordingMarker };
    }
    return m;
  };

  const api = await loadMapModule(mock, {
    createElement: createFakeElement,
    createElementNS: (_ns, tag) => createFakeElement(tag),
    getElementById: () => createFakeElement("div"),
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

  const bus = {
    providerName: "Rapid Bus KL",
    transportType: "Bus",
    routeId: "U4200",
    routeShortName: "420",
    vehicleId: "BUS-420",
    lat: 3.14,
    lng: 101.69,
    speed: 5,
    timestamp: 1794885820,
  };
  await api.syncMarkers([bus]);

  const marker = createdMarkers[0];
  assert.equal(marker.content.classList.contains("transport-marker--selected"), false);

  // Clicking the bus emphasizes it immediately.
  marker.listeners["gmp-click"]();
  assert.equal(marker.content.classList.contains("transport-marker--selected"), true);
  assert.equal(marker.zIndex, 1000);

  // clearStaticRoute(true) + showStaticRoute keeps emphasis on the selected bus.
  api.clearStaticRoute(true);
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
          properties: { route_color: "#1769aa" },
        },
      ],
    }),
    dotNetHelper,
  );
  assert.equal(marker.content.classList.contains("transport-marker--selected"), true);

  // Subsequent syncMarkers poll updates keep emphasis on the selected bus.
  await api.syncMarkers([{ ...bus, lat: 3.141, timestamp: 1794885850 }]);
  assert.equal(marker.content.classList.contains("transport-marker--selected"), true);

  // Clicking elsewhere on the map closes InfoWindow but keeps the bus emphasized while static route is active.
  mapListeners.click();
  assert.equal(closedCount, 1);
  assert.equal(marker.content.classList.contains("transport-marker--selected"), true);
  assert.equal(marker.zIndex, 1000);

  // Clicking the emphasized bus back deselects it and triggers OnVehicleDeselected.
  marker.listeners["gmp-click"]();
  assert.equal(marker.content.classList.contains("transport-marker--selected"), false);
  assert.equal(marker.zIndex, null);
  assert.deepEqual(invocations, [
    ["OnVehicleSelected", "Rapid Bus KL", "U4200", "420"],
    ["OnVehicleDeselected", "Rapid Bus KL"],
  ]);

  // Clicking again re-selects; clearStaticRoute() (without preserve) clears the emphasis.
  marker.listeners["gmp-click"]();
  assert.equal(marker.content.classList.contains("transport-marker--selected"), true);
  api.clearStaticRoute();
  assert.equal(marker.content.classList.contains("transport-marker--selected"), false);
  assert.equal(marker.zIndex, null);
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

test("clicking a static stop toggles an InfoWindow with GeoJSON properties without invoking departure API", async () => {
  const openedInfo = [];
  const createdMarkers = [];
  let closedCount = 0;
  let dataClickListener = null;
  let dataStyleFn = null;
  const mapListeners = {};

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
    setPosition(position) {
      this.lastPosition = position;
    }
    open(opts) {
      openedInfo.push({
        anchor: opts?.anchor ?? null,
        map: opts?.map ?? null,
        position: this.lastPosition ?? null,
        content: this.lastContent,
      });
    }
    close() {
      closedCount++;
    }
  }

  class MockDataLayer {
    constructor() {
      this.features = [];
    }
    setStyle(fn) {
      dataStyleFn = fn;
    }
    addListener(eventName, cb) {
      if (eventName === "click") dataClickListener = cb;
    }
    addGeoJson(fc) {
      const wrapped = fc.features.map((f) => ({
        raw: f,
        getProperty: (key) => f.properties?.[key],
        forEachProperty: (cb) => {
          for (const [key, value] of Object.entries(f.properties || {})) {
            cb(value, key);
          }
        },
        getGeometry: () => ({
          get: () =>
            f.geometry?.type === "Point"
              ? {
                  lat: () => f.geometry.coordinates[1],
                  lng: () => f.geometry.coordinates[0],
                }
              : null,
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
      this.features = this.features.filter((item) => item !== f);
    }
  }

  const mock = createMockGoogleMaps();
  mock.maps.Data = MockDataLayer;
  mock.maps.SymbolPath = { CIRCLE: 0 };
  const origImport = mock.maps.importLibrary;
  mock.maps.importLibrary = async (lib) => {
    const m = await origImport(lib);
    if (lib === "maps") {
      class RecordingMap extends m.Map {
        addListener(name, fn) {
          mapListeners[name] = fn;
        }
      }
      return { ...m, Map: RecordingMap, InfoWindow: RecordingInfoWindow };
    }
    if (lib === "marker") {
      return { ...m, AdvancedMarkerElement: RecordingMarker };
    }
    return m;
  };

  const api = await loadMapModule(mock, {
    createElement: createFakeElement,
    createElementNS: (_ns, tag) => createFakeElement(tag),
    getElementById: () => createFakeElement("div"),
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

  await api.showStaticRoute(
    JSON.stringify({
      type: "FeatureCollection",
      features: [
        {
          type: "Feature",
          geometry: { type: "Point", coordinates: [101.6952, 3.1859] },
          properties: {
            kind: "stop",
            stop_id: "AG1",
            stop_name: "SENTUL TIMUR",
            route_id: "AG",
            platform_code: "P1",
            custom_note: "Terminal station",
          },
        },
        {
          type: "Feature",
          geometry: { type: "Point", coordinates: [101.7119, 3.1348] },
          properties: {
            kind: "stop",
            stop_id: "AG10",
            stop_name: "PUDU",
            route_id: "AG",
          },
        },
      ],
    }),
    dotNetHelper,
  );

  assert.equal(typeof dataStyleFn, "function");
  assert.equal(typeof dataClickListener, "function");

  const stop1Props = {
    kind: "stop",
    stop_id: "AG1",
    stop_name: "SENTUL TIMUR",
    route_id: "AG",
    trip_ids: ["AGL_MonFri_0", "AGL_MonFri_1"],
    arrival_times: "06:25:20, 08:50:20, 20:25:20",
    platform_code: "P1",
    custom_note: "Terminal station",
  };
  const stop1 = {
    getProperty: (key) => stop1Props[key],
    forEachProperty: (cb) => {
      for (const [k, v] of Object.entries(stop1Props)) {
        cb(v, k);
      }
    },
    getGeometry: () => ({
      get: () => ({ lat: () => 3.1859, lng: () => 101.6952 }),
    }),
  };

  const stopStyle = dataStyleFn(stop1);
  assert.equal(stopStyle.clickable, true);
  assert.equal(stopStyle.cursor, "pointer");
  assert.equal(stopStyle.title, "SENTUL TIMUR");

  // First click opens InfoWindow with GeoJSON properties without invoking .NET departure callbacks.
  dataClickListener({ feature: stop1 });
  assert.equal(openedInfo.length, 1);
  assert.deepEqual(plain(openedInfo[0].position), {
    lat: 3.1859,
    lng: 101.6952,
  });
  const card = openedInfo[0].content;
  assert.equal(card.className, "bus-info-window");
  assert.equal(card.children[0].className, "bus-info-header");
  assert.equal(card.children[0].textContent, "SENTUL TIMUR");
  const rows = card.children
    .slice(1)
    .map((row) => [row.children[0].textContent, row.children[1].textContent]);
  assert.deepEqual(rows, [
    ["Stop ID", "AG1"],
    ["Route", "AG"],
    ["Arrival Times", "06:25:20, 08:50:20, 20:25:20"],
    ["Platform", "P1"],
    ["Custom Note", "Terminal station"],
  ]);
  assert.deepEqual(invocations, []);

  // Second click on the same stop toggles the InfoWindow closed.
  dataClickListener({ feature: stop1 });
  assert.equal(closedCount, 1);
  assert.equal(invocations.length, 0);

  // Opening a stop InfoWindow and calling clearStaticRoute closes it.
  dataClickListener({ feature: stop1 });
  assert.equal(openedInfo.length, 2);
  api.clearStaticRoute();
  assert.equal(closedCount, 2);

  // Clicking a bus marker opens its InfoWindow; clearStaticRoute(true) keeps it open,
  // and clicking a stop detaches the previous bus anchor before opening on the stop.
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
  createdMarkers[0].listeners["gmp-click"]();
  const closesAfterBusOpen = closedCount;
  api.clearStaticRoute(true);
  assert.equal(closedCount, closesAfterBusOpen);

  dataClickListener({ feature: stop1 });
  assert.equal(closedCount, closesAfterBusOpen + 1);
  assert.equal(openedInfo.at(-1).anchor, null);
  assert.deepEqual(plain(openedInfo.at(-1).position), {
    lat: 3.1859,
    lng: 101.6952,
  });

  // Clicking a second stop without stop_name falls back to stop_id as header and labels distinct route_id as Route ID.
  const stop2 = {
    getProperty: (key) =>
      ({
        kind: "stop",
        stop_id: "KL1079",
        route_short_name: "420",
        route_id: "U4200",
      })[key],
    forEachProperty: (cb) => {
      for (const [k, v] of Object.entries({
        kind: "stop",
        stop_id: "KL1079",
        route_short_name: "420",
        route_id: "U4200",
      })) {
        cb(v, k);
      }
    },
    getGeometry: () => ({
      get: () => ({ lat: () => 3.1348, lng: () => 101.7119 }),
    }),
  };
  dataClickListener({ feature: stop2 });
  const card2 = openedInfo.at(-1).content;
  assert.equal(card2.children[0].textContent, "KL1079");
  assert.deepEqual(
    card2.children
      .slice(1)
      .map((row) => [row.children[0].textContent, row.children[1].textContent]),
    [
      ["Stop ID", "KL1079"],
      ["Route", "420"],
      ["Route ID", "U4200"],
    ],
  );

  // Clicking the map closes the open stop InfoWindow.
  mapListeners.click();
  assert.equal(closedCount, closesAfterBusOpen + 2);
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

test("gm_authFailure persists across subsequent initGoogleMaps calls for the same API key", async () => {
  let gmAuthFailure = null;
  const api = await loadMapModule(createMockGoogleMaps(), {}, {
    set gm_authFailure(fn) {
      gmAuthFailure = fn;
    },
    get gm_authFailure() {
      return gmAuthFailure;
    },
  });
  const firstRecorder = createDotNetRecorder();

  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "invalid-key",
    firstRecorder.dotNetHelper,
    "DARK",
    "Asia/Kuala_Lumpur",
  );
  gmAuthFailure();
  assert.equal(firstRecorder.invocations.length, 1);
  assert.equal(firstRecorder.invocations[0][0], "OnMapAuthError");

  api.cleanupMap();

  const secondRecorder = createDotNetRecorder();
  const secondMap = await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "invalid-key",
    secondRecorder.dotNetHelper,
    "DARK",
    "Asia/Kuala_Lumpur",
  );
  assert.equal(secondMap, null);
  assert.equal(secondRecorder.invocations.length, 1);
  assert.equal(secondRecorder.invocations[0][0], "OnMapAuthError");
});

test("gm_authFailure after initAutocomplete replaces disabled PlaceAutocompleteElement with editable fallback input", async () => {
  let gmAuthFailure = null;
  const containers = new Map([
    ["origin-input", createFakeElement("div")],
    ["destination-input", createFakeElement("div")],
  ]);
  const createdInputs = [];

  const api = await loadMapModule(
    createMockGoogleMaps({ withPlaces: true }),
    {
      getElementById: (id) => containers.get(id) ?? createFakeElement("div"),
      createElement: (tag) => {
        const el = createFakeElement(tag);
        if (tag === "input") {
          const inputListeners = new Map();
          el.addEventListener = (type, fn) => inputListeners.set(type, fn);
          el.emitInput = (val) => {
            el.value = val;
            return inputListeners.get("input")?.({ target: { value: val } });
          };
          createdInputs.push(el);
        }
        return el;
      },
    },
    {
      set gm_authFailure(fn) {
        gmAuthFailure = fn;
      },
      get gm_authFailure() {
        return gmAuthFailure;
      },
    },
  );

  const { invocations, dotNetHelper } = createDotNetRecorder();
  await api.initAutocomplete(
    "origin-input",
    dotNetHelper,
    "UpdateOrigin",
    "invalid-key",
  );
  await api.initAutocomplete(
    "destination-input",
    dotNetHelper,
    "UpdateDestination",
    "invalid-key",
  );
  assert.equal(createdInputs.length, 0);

  // Trigger asynchronous gm_authFailure after PlaceAutocompleteElement mounted
  gmAuthFailure();
  assert.equal(createdInputs.length, 2);

  await createdInputs[0].emitInput("KL Sentral");
  await createdInputs[1].emitInput("KLCC");
  assert.deepEqual(invocations, [
    ["UpdateOrigin", "KL Sentral"],
    ["UpdateDestination", "KLCC"],
  ]);
});

test("initAutocomplete mounts even when Blazor marshals distinct DotNetObjectReference wrappers across calls", async () => {
  const containers = new Map([
    ["origin-input", createFakeElement("div")],
    ["destination-input", createFakeElement("div")],
  ]);
  const api = await loadMapModule(
    createMockGoogleMaps({ withPlaces: true }),
    {
      getElementById: (id) => containers.get(id) ?? createFakeElement("div"),
    },
  );

  const mapRecorder = createDotNetRecorder();
  const originRecorder = createDotNetRecorder();
  const destRecorder = createDotNetRecorder();

  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "test-api-key",
    mapRecorder.dotNetHelper,
    "DARK",
    "Asia/Kuala_Lumpur",
  );
  await api.initAutocomplete(
    "origin-input",
    originRecorder.dotNetHelper,
    "UpdateOrigin",
    "test-api-key",
  );
  await api.initAutocomplete(
    "destination-input",
    destRecorder.dotNetHelper,
    "UpdateDestination",
    "test-api-key",
  );

  assert.equal(containers.get("origin-input").children.length, 1);
  assert.equal(containers.get("destination-input").children.length, 1);
  assert.equal(
    containers.get("origin-input").children[0].placeholder,
    "Enter origin...",
  );
  assert.equal(
    containers.get("destination-input").children[0].placeholder,
    "Enter destination...",
  );

  // Typing then clicking internal clear button syncs both changes to Blazor
  const originWidget = containers.get("origin-input").children[0];
  originWidget.value = "KL Sentral";
  originWidget.listeners.input({ target: originWidget });
  originWidget.value = "";
  originWidget.listeners.click();
  await Promise.resolve();
  assert.deepEqual(originRecorder.invocations, [
    ["UpdateOrigin", "KL Sentral"],
    ["UpdateOrigin", ""],
  ]);
});

test("cleanupMap cancels in-flight initAutocomplete and clears state cleanly", async () => {
  const containers = new Map([
    ["origin-input", createFakeElement("div")],
  ]);
  let resolvePlaces = null;
  const placesGate = new Promise((resolve) => {
    resolvePlaces = resolve;
  });
  const mock = createMockGoogleMaps({ withPlaces: true });
  const origImportLibrary = mock.maps.importLibrary;
  mock.maps.importLibrary = async (lib) => {
    if (lib === "places") {
      await placesGate;
    }
    return origImportLibrary(lib);
  };

  const api = await loadMapModule(mock, {
    getElementById: (id) => containers.get(id) ?? createFakeElement("div"),
  });
  const { dotNetHelper } = createDotNetRecorder();

  const pendingInit = api.initAutocomplete(
    "origin-input",
    dotNetHelper,
    "UpdateOrigin",
    "test-api-key",
  );
  api.cleanupMap();
  resolvePlaces();
  await pendingInit;

  assert.equal(containers.get("origin-input").children.length, 0);
});

test("gm_authFailure for one key does not force fallback autocomplete when remounting with a different key", async () => {
  let gmAuthFailure = null;
  const containers = new Map([
    ["origin-input", createFakeElement("div")],
  ]);
  const api = await loadMapModule(
    createMockGoogleMaps({ withPlaces: true }),
    {
      getElementById: (id) => containers.get(id) ?? createFakeElement("div"),
    },
    {
      set gm_authFailure(fn) {
        gmAuthFailure = fn;
      },
      get gm_authFailure() {
        return gmAuthFailure;
      },
    },
  );

  const firstRecorder = createDotNetRecorder();
  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "invalid-key",
    firstRecorder.dotNetHelper,
    "DARK",
    "Asia/Kuala_Lumpur",
  );
  gmAuthFailure();
  api.cleanupMap();

  const autocompleteRecorder = createDotNetRecorder();
  const mapRecorder = createDotNetRecorder();
  await Promise.all([
    api.initAutocomplete(
      "origin-input",
      autocompleteRecorder.dotNetHelper,
      "UpdateOrigin",
      "valid-key",
    ),
    api.initGoogleMaps(
      "map",
      3.14,
      101.69,
      13,
      "valid-key",
      mapRecorder.dotNetHelper,
      "DARK",
      "Asia/Kuala_Lumpur",
    ),
  ]);

  const mounted = containers.get("origin-input").children[0];
  assert.ok(mounted);
  assert.notEqual(String(mounted.tagName).toLowerCase(), "input");
  assert.equal(mapRecorder.invocations.length, 0);
});

test("syncMarkers marks estimated rail vehicles with title suffix and info window status without estimated style class", async () => {
  const createdMarkers = [];
  const openedInfo = [];
  class RecordingInfoWindow {
    setContent(content) {
      this._content = content;
    }
    open(opts) {
      openedInfo.push({ ...opts, content: this._content });
    }
    close() {}
    addListener() {}
  }
  class RecordingMarker {
    constructor(opts) {
      Object.assign(this, opts);
      this.listeners = {};
      createdMarkers.push(this);
    }
    addEventListener(name, fn) {
      this.listeners[name] = fn;
    }
    addListener(name, fn) {
      this.listeners[name] = fn;
    }
  }

  const mock = createMockGoogleMaps();
  const origImport = mock.maps.importLibrary;
  mock.maps.importLibrary = async (lib) => {
    const m = await origImport(lib);
    if (lib === "maps") {
      return { ...m, InfoWindow: RecordingInfoWindow };
    }
    if (lib === "marker") {
      return { ...m, AdvancedMarkerElement: RecordingMarker };
    }
    return m;
  };
  const api = await loadMapModule(mock, {
    createElement: createFakeElement,
    createElementNS: (_ns, tag) => createFakeElement(tag),
    getElementById: () => createFakeElement("div"),
  });
  const recorder = createDotNetRecorder();
  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    13,
    "key",
    recorder.dotNetHelper,
    "LIGHT",
    "Asia/Kuala_Lumpur",
  );
  createdMarkers.length = 0;

  await api.syncMarkers([
    {
      providerName: "Rapid Rail KL",
      transportType: "LRT",
      routeId: "KJ",
      routeShortName: "KJL",
      vehicleId: "KJL-0-0800",
      lat: 3.141,
      lng: 101.692,
      bearing: 90,
      speed: 12.5,
      timestamp: 1794885820,
      isEstimated: true,
    },
  ]);

  assert.equal(createdMarkers.length, 1);
  const marker = createdMarkers[0];
  assert.equal(marker.title, "Route KJL - KJL-0-0800 (Estimated)");
  assert.equal(marker.content.classList.contains("transport-marker--estimated"), false);

  marker.listeners["gmp-click"]();
  assert.equal(openedInfo.length, 1);
  const card = openedInfo[0].content;
  const rows = card.children
    .slice(1)
    .map((row) => [row.children[0].textContent, row.children[1].textContent]);
  assert.equal(rows[0][0], "Route");
  assert.equal(rows[0][1], "KJL");
  assert.equal(rows[1][0], "Status");
  assert.equal(rows[1][1], "Estimated (schedule)");
});

test("setMapTheme recreates Map with new colorScheme while preserving camera, markers, static routes, transit routes, and open InfoWindow", async () => {
  const createdMaps = [];
  const transitLayers = [];
  const createdMarkers = [];
  const polylineSchemes = [];
  const openedInfoWindows = [];
  const clearedListenerTargets = [];
  let fitBoundsCalls = 0;

  class RecordingMap {
    constructor(el, opts) {
      this.el = el;
      this.opts = opts;
      this.center = opts.center;
      this.zoom = opts.zoom;
      this.heading = opts.heading ?? 45;
      this.tilt = opts.tilt ?? 10;
      this.mapTypeId = opts.mapTypeId ?? "roadmap";
      this.listeners = {};
      createdMaps.push(this);
    }
    getCenter() {
      return { lat: () => this.center.lat, lng: () => this.center.lng };
    }
    getZoom() {
      return this.zoom;
    }
    getHeading() {
      return this.heading;
    }
    getTilt() {
      return this.tilt;
    }
    getMapTypeId() {
      return this.mapTypeId;
    }
    addListener(name, cb) {
      this.listeners[name] = cb;
    }
    fitBounds() {
      fitBoundsCalls++;
    }
  }

  class RecordingInfoWindow {
    constructor() {
      this.openCount = 0;
    }
    setContent(content) {
      this.content = content;
    }
    setPosition(pos) {
      this.position = pos;
    }
    getPosition() {
      return this.position;
    }
    open(opts) {
      this.openCount++;
      openedInfoWindows.push({ ...opts, content: this.content });
    }
    close() {}
  }

  const createdDataLayers = [];
  const createdPolylines = [];

  class RecordingDataLayer {
    constructor({ map }) {
      this.map = map;
      this.features = [];
      createdDataLayers.push(this);
    }
    setMap(m) {
      this.map = m;
    }
    setStyle() {}
    addListener() {}
    addGeoJson(fc) {
      this.features = (fc.features || []).map((f) => ({
        props: f.properties || {},
        getProperty: (k) => f.properties?.[k],
        getGeometry: () => ({ forEachLatLng: () => {} }),
      }));
      return this.features;
    }
    forEach(cb) {
      this.features.forEach(cb);
    }
    remove(f) {
      this.features = this.features.filter((x) => x !== f);
    }
  }

  class RecordingPolyline {
    constructor(opts = {}) {
      this.opts = opts;
      this.map = opts.map ?? null;
      createdPolylines.push(this);
    }
    getPath() {
      return this.opts.path ?? [];
    }
    setOptions(opts) {
      Object.assign(this.opts, opts);
    }
    setMap(m) {
      this.map = m;
    }
    addListener() {}
  }

  class RecordingMarker {
    constructor(opts) {
      Object.assign(this, opts);
      this.listeners = {};
      createdMarkers.push(this);
    }
    addEventListener(name, cb) {
      this.listeners[name] = cb;
    }
    removeEventListener() {}
  }

  function createFakeElement(tag = "div") {
    const classes = new Set();
    return {
      tagName: tag.toUpperCase(),
      style: {
        setProperty() {},
        removeProperty() {},
      },
      children: [],
      isConnected: true,
      classList: {
        add: (c) => classes.add(c),
        remove: (c) => classes.delete(c),
        toggle: (c, force) => (force ? classes.add(c) : classes.delete(c)),
        contains: (c) => classes.has(c),
      },
      setAttribute() {},
      append(...nodes) {
        this.children.push(...nodes);
      },
      appendChild(node) {
        this.children.push(node);
        return node;
      },
      replaceChildren(...nodes) {
        this.children = [...nodes];
      },
      querySelector() {
        return null;
      },
      addEventListener() {},
    };
  }

  const mapEl = createFakeElement("div");
  const mock = createMockGoogleMaps({
    transitLayers,
    routesResult: () => ({
      routes: [
        {
          path: [
            { lat: 3.14, lng: 101.69 },
            { lat: 3.15, lng: 101.7 },
          ],
          durationMillis: 900000,
          distanceMeters: 4200,
          localizedValues: { duration: "15 min" },
          createPolylines: (opts) => {
            polylineSchemes.push(opts.colorScheme);
            return [new RecordingPolyline({ path: [{ lat: 3.14, lng: 101.69 }, { lat: 3.15, lng: 101.7 }] })];
          },
          legs: [
            {
              startLocation: { lat: 3.14, lng: 101.69 },
              endLocation: { lat: 3.15, lng: 101.7 },
              steps: [],
            },
          ],
        },
      ],
    }),
  });
  mock.maps.Data = RecordingDataLayer;
  mock.maps.SymbolPath = { CIRCLE: 0 };
  mock.maps.event = {
    clearInstanceListeners: (target) => clearedListenerTargets.push(target),
  };
  const origImport = mock.maps.importLibrary;
  mock.maps.importLibrary = async (lib) => {
    const mod = await origImport(lib);
    if (lib === "maps") {
      return {
        ...mod,
        Map: RecordingMap,
        InfoWindow: RecordingInfoWindow,
        Polyline: RecordingPolyline,
      };
    }
    if (lib === "marker") {
      return { ...mod, AdvancedMarkerElement: RecordingMarker };
    }
    return mod;
  };

  const api = await loadMapModule(mock, {
    createElement: (tag) => createFakeElement(tag),
    createElementNS: (_ns, tag) => createFakeElement(tag),
    getElementById: (id) => (id === "map" ? mapEl : createFakeElement("div")),
  });

  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    14,
    "test-api-key",
    null,
    "DARK",
    "Asia/Kuala_Lumpur",
  );
  assert.equal(createdMaps.length, 1);
  assert.equal(createdMaps[0].opts.colorScheme, "DARK");
  const userMarker = createdMarkers[0];

  // Pan and zoom the map before toggling theme.
  createdMaps[0].center = { lat: 3.16, lng: 101.71 };
  createdMaps[0].zoom = 16;

  // Add a live vehicle marker and open its InfoWindow.
  await api.syncMarkers([
    {
      providerName: "Rapid Bus KL",
      transportType: "Bus",
      routeId: "400",
      routeShortName: "400",
      vehicleId: "BUS-1",
      lat: 3.15,
      lng: 101.7,
      bearing: 90,
      speed: 10,
      timestamp: 1794885820,
    },
  ]);
  const busMarker = createdMarkers[1];
  busMarker.listeners["gmp-click"]();
  assert.equal(openedInfoWindows.length, 1);

  // Add a static route and a multi-route transit overlay.
  await api.showStaticRoute(
    JSON.stringify({
      type: "FeatureCollection",
      features: [
        {
          type: "Feature",
          properties: { route_color: "#ff0000" },
          geometry: {
            type: "LineString",
            coordinates: [
              [101.69, 3.14],
              [101.7, 3.15],
            ],
          },
        },
        {
          type: "Feature",
          properties: { kind: "stop", stop_name: "KLCC" },
          geometry: { type: "Point", coordinates: [101.7, 3.15] },
        },
      ],
    }),
  );

  const routes = await api.getTransitRoutes("KLCC", "Pasar Seni");
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
    null,
  );
  const fitBoundsBeforeSwitch = fitBoundsCalls;

  // Calling setMapTheme with the same theme is a no-op.
  await api.setMapTheme("DARK");
  assert.equal(createdMaps.length, 1);

  // Switching to LIGHT recreates the Map with preserved camera and overlays.
  await api.setMapTheme("LIGHT");
  assert.equal(createdMaps.length, 2);
  const newMap = createdMaps[1];
  assert.equal(newMap.opts.colorScheme, "LIGHT");
  assert.deepEqual(plain(newMap.opts.center), { lat: 3.16, lng: 101.71 });
  assert.equal(newMap.opts.zoom, 16);
  assert.equal(newMap.opts.heading, 45);
  assert.equal(newMap.opts.tilt, 10);
  assert.equal(clearedListenerTargets.includes(createdMaps[0]), true);

  // Old TransitLayer is detached and a fresh TransitLayer is attached to the new Map instance.
  assert.equal(transitLayers.length, 2);
  assert.equal(transitLayers[0].getMap(), null);
  assert.equal(transitLayers[1].getMap(), newMap);
  assert.equal(userMarker.map, newMap);
  assert.equal(busMarker.map, newMap);
  assert.equal(createdDataLayers.length, 1);
  assert.equal(createdDataLayers[0].map, newMap);
  assert.equal(createdPolylines[0].map, newMap);
  assert.equal(createdPolylines[1].map, newMap);

  // Transit route redrawn in LIGHT without resetting viewport.
  assert.deepEqual(polylineSchemes, ["DARK", "LIGHT"]);
  assert.equal(fitBoundsCalls, fitBoundsBeforeSwitch);

  // Open InfoWindow reopened on the new Map for the same vehicle marker.
  assert.equal(openedInfoWindows.length, 2);
  assert.equal(openedInfoWindows[1].anchor, busMarker);
  assert.equal(openedInfoWindows[1].map, newMap);
});

test("setMapTheme redraws cached non-transit route without recomputing Route.computeRoutes", async () => {
  const createdMaps = [];
  const computeRequests = [];
  const polylineSchemes = [];
  let fitBoundsCalls = 0;

  class RecordingMap {
    constructor(_el, opts) {
      this.opts = opts;
      createdMaps.push(this);
    }
    getCenter() {
      return this.opts.center;
    }
    getZoom() {
      return this.opts.zoom;
    }
    addListener() {}
    fitBounds() {
      fitBoundsCalls++;
    }
  }

  const mock = createMockGoogleMaps({
    computeRequests,
    routesResult: () => ({
      routes: [
        {
          path: [
            { lat: 3.14, lng: 101.69 },
            { lat: 3.15, lng: 101.7 },
          ],
          durationMillis: 600000,
          distanceMeters: 3000,
          viewport: { north: 3.16, south: 3.13, east: 101.71, west: 101.68 },
          createPolylines: (opts) => {
            polylineSchemes.push(opts.colorScheme);
            return [
              {
                setMap() {},
                setOptions() {},
                addListener() {},
              },
            ];
          },
          legs: [
            {
              startLocation: { lat: 3.14, lng: 101.69 },
              endLocation: { lat: 3.15, lng: 101.7 },
              steps: [],
            },
          ],
        },
      ],
    }),
  });
  const origImport = mock.maps.importLibrary;
  mock.maps.importLibrary = async (lib) => {
    const mod = await origImport(lib);
    if (lib === "maps") {
      return { ...mod, Map: RecordingMap };
    }
    return mod;
  };

  const api = await loadMapModule(mock);
  await api.initGoogleMaps(
    "map",
    3.14,
    101.69,
    14,
    "test-api-key",
    null,
    "LIGHT",
    "Asia/Kuala_Lumpur",
  );

  const ok = await api.showRouteByName("KLCC", "Mid Valley", "DRIVING");
  assert.equal(ok, true);
  assert.equal(computeRequests.length, 1);
  assert.deepEqual(polylineSchemes, ["LIGHT"]);
  assert.equal(fitBoundsCalls, 1);

  await api.setMapTheme("DARK");
  assert.equal(createdMaps.length, 2);
  assert.equal(createdMaps[1].opts.colorScheme, "DARK");
  assert.equal(computeRequests.length, 1);
  assert.deepEqual(polylineSchemes, ["LIGHT", "DARK"]);
  assert.equal(fitBoundsCalls, 1);
});

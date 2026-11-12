import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, SourceTextModule } from "node:vm";

const source = await readFile(
  new URL("../../MyTransportAppWASM/wwwroot/js/googleMap.js", import.meta.url),
  "utf8"
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

function createMockGoogleMaps(overrides = {}) {
  class MockGeocoder {
    async geocode({ address, location }) {
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
    fitBounds() {}
  }

  class MockInfoWindow {
    close() {}
  }

  class MockPolyline {
    constructor(opts = {}) { this.opts = opts; }
    setOptions(opts) { Object.assign(this.opts, opts); }
    setMap(m) { this.map = m; }
    addListener() {}
  }

  class MockLatLngBounds {
    constructor() { this.points = []; }
    extend(p) { this.points.push(p); }
    isEmpty() { return this.points.length === 0; }
  }

  class MockPinElement {
    constructor(opts) { this.opts = opts; }
  }

  class MockAdvancedMarkerElement {
    constructor(opts) { Object.assign(this, opts); }
    addEventListener() {}
    removeEventListener() {}
  }

  const MockRoute = {
    async computeRoutes(request) {
      overrides.computeRequests?.push(request);
      const invalidFields = (request.fields || []).filter((f) => !VALID_ROUTE_FIELDS.has(f));
      if (invalidFields.length > 0) {
        throw new Error(`InvalidValueError: in property fields: contains invalid fields: ${invalidFields.join(", ")}`);
      }
      if (!VALID_TRAVEL_MODES.has(request.travelMode)) {
        throw new Error(`InvalidValueError: in property travelMode: ${request.travelMode} is not an accepted value`);
      }
      if (overrides.routesResult) {
        return overrides.routesResult(request);
      }
      return {
        routes: [
          {
            path: [{ lat: 3.14, lng: 101.69 }, { lat: 3.15, lng: 101.70 }],
            durationMillis: 900000,
            distanceMeters: 4200,
            viewport: { north: 3.16, south: 3.13, east: 101.71, west: 101.68 },
            createPolylines: () => [new MockPolyline()],
            legs: [
              {
                startLocation: { lat: 3.14, lng: 101.69 },
                endLocation: { lat: 3.15, lng: 101.70 },
                steps: [
                  {
                    travelMode: "TRANSIT",
                    transitDetails: {
                      transitLine: { shortName: "180", name: "Taman Dato' Senu ~ Hab Pasar Seni" },
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
        if (lib === "maps") return { Map: MockMap, InfoWindow: MockInfoWindow, Polyline: MockPolyline };
        if (lib === "core") return { LatLngBounds: MockLatLngBounds };
        if (lib === "geocoding") return { Geocoder: MockGeocoder };
        if (lib === "routes") return { Route: MockRoute };
        if (lib === "geometry") {
          return {
            encoding: {
              encodePath: (path) => `encoded:${path.length}`,
              decodePath: (encoded) => (encoded === "empty" ? [] : [{ lat: 3.14, lng: 101.69 }]),
            },
          };
        }
        if (lib === "marker") {
          return { PinElement: MockPinElement, AdvancedMarkerElement: MockAdvancedMarkerElement };
        }
        return {};
      },
    },
  };
}

async function loadMapModule(googleMock) {
  const elementStub = {
    style: {},
    setAttribute: () => {},
    append: () => {},
    appendChild: () => {},
    addEventListener: () => {},
  };
  const context = createContext({
    URLSearchParams,
    Number,
    document: {
      createElement: () => ({ ...elementStub }),
      querySelector: () => null,
      getElementById: () => ({ ...elementStub }),
      head: { append: () => {} },
    },
    window: {
      google: googleMock,
    },
    google: googleMock,
    console,
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
        callback([{
          geometry: { location: { lat: 3.14, lng: 101.69 } },
          formatted_address: "Kuala Lumpur",
        }], "OK");
      }
    }
  }
  const api = await loadMapModule({
    maps: {
      Map: class {},
      importLibrary: async (lib) => (lib === "geocoding" ? { Geocoder: CallbackOnlyGeocoder } : {}),
    },
  });
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
  assert.deepEqual([...computeRequests[0].fields], ["path", "durationMillis", "distanceMeters", "legs"]);
  assert.equal(routes.length, 1);
  assert.equal(routes[0].durationSeconds, 900);
  assert.equal(routes[0].distanceMeters, 4200);
  assert.equal(routes[0].encodedPolyline, "encoded:2");
  assert.equal(routes[0].transitSteps.length, 1);
  assert.equal(routes[0].transitSteps[0].lineShortName, "180");
  assert.equal(routes[0].transitSteps[0].departureLat, 3.169987);
  assert.equal(routes[0].transitSteps[0].departureLng, 101.695148);
  assert.equal(routes[0].transitSteps[0].departureStopName, "KL50 Damai Hospital");
});

test("showRouteByName sends valid Maps JS TravelMode values and handles empty route results", async () => {
  const computeRequests = [];
  const api = await loadMapModule(createMockGoogleMaps({ computeRequests }));
  await api.initGoogleMaps("map", 3.14, 101.69, 13, "test-api-key", null, "DARK", "Asia/Kuala_Lumpur");

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
  const emptyApi = await loadMapModule(createMockGoogleMaps({
    computeRequests: emptyRequests,
    routesResult: () => ({ routes: [] }),
  }));
  await emptyApi.initGoogleMaps("map", 3.14, 101.69, 13, "test-api-key", null, "DARK", "Asia/Kuala_Lumpur");

  const foundEmpty = await emptyApi.showRouteByName("KL Sentral", "KLCC", "BICYCLING");
  assert.equal(foundEmpty, false);
});

test("drawMultipleRoutes skips fitBounds when decoded paths are empty", async () => {
  let fitBoundsCalls = 0;
  const mock = createMockGoogleMaps();
  mock.maps.Map = class {
    addListener() {}
    fitBounds() { fitBoundsCalls++; }
  };
  const origImport = mock.maps.importLibrary;
  mock.maps.importLibrary = async (lib) => {
    if (lib === "maps") {
      const m = await origImport(lib);
      return { ...m, Map: mock.maps.Map };
    }
    return origImport(lib);
  };

  const api = await loadMapModule(mock);
  await api.initGoogleMaps("map", 3.14, 101.69, 13, "test-api-key", null, "DARK", "Asia/Kuala_Lumpur");
  await api.drawMultipleRoutes([{ id: 0, path: "empty", hasLiveBus: false }], 0, null);

  assert.equal(fitBoundsCalls, 0);
});

test("initAutocomplete prefers formattedAddress and reuses selected coordinates for route calculation", async () => {
  const computeRequests = [];
  const listeners = new Map();
  let geocodeCalls = 0;

  class MockPlaceAutocompleteElement {
    constructor() {
      this.style = {};
      this.listeners = {};
    }
    setAttribute() {}
    addEventListener(type, fn) {
      this.listeners[type] = fn;
      listeners.set(this.id, fn);
    }
  }

  class CountingGeocoder {
    async geocode({ address }) {
      geocodeCalls++;
      if (address === "GW8F+CW") throw new Error("ZERO_RESULTS");
      return {
        results: [{
          geometry: { location: { lat: () => 3.04, lng: () => 101.44 } },
          formatted_address: `${address}, Malaysia`,
        }],
      };
    }
  }

  const mock = createMockGoogleMaps({ computeRequests });
  const origImport = mock.maps.importLibrary;
  mock.maps.importLibrary = async (lib) => {
    if (lib === "places") return { PlaceAutocompleteElement: MockPlaceAutocompleteElement };
    if (lib === "geocoding") return { Geocoder: CountingGeocoder };
    return origImport(lib);
  };

  const api = await loadMapModule(mock);
  const invocations = [];
  const dotNetHelper = {
    invokeMethodAsync: async (method, ...args) => {
      invocations.push([method, ...args]);
    },
  };

  await api.initAutocomplete("origin-input", dotNetHelper, "UpdateOrigin", "test-api-key");
  await api.initAutocomplete("destination-input", dotNetHelper, "UpdateDestination", "test-api-key");

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
    "Klang, Selangor, Malaysia"
  );
  assert.equal(routes.length, 1);
  assert.equal(geocodeCalls, 0);
  assert.deepEqual(JSON.parse(JSON.stringify(computeRequests[0].origin)), { lat: 3.5160625, lng: 101.9248125 });
  assert.deepEqual(JSON.parse(JSON.stringify(computeRequests[0].destination)), { lat: 3.0392164, lng: 101.4418818 });
});

import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, SourceTextModule } from "node:vm";

const source = await readFile(
  new URL("../../MyTransportAppWASM/wwwroot/js/googleMap.js", import.meta.url),
  "utf8"
);

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
              formatted_address: "Kuala Lumpur, Malaysia",
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

  return {
    maps: {
      Map: class {},
      importLibrary: async (lib) => {
        if (lib === "geocoding") return { Geocoder: MockGeocoder };
        return {};
      },
    },
  };
}

async function loadMapModule(googleMock) {
  const context = createContext({
    URLSearchParams,
    Number,
    document: {
      createElement: () => ({ setAttribute: () => {}, append: () => {} }),
      querySelector: () => null,
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
  assert.equal(res.formattedAddress, "Kuala Lumpur, Malaysia");

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

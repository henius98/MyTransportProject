import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, SourceTextModule } from "node:vm";

const source = await readFile(
  new URL("../../MyTransportAppWASM/wwwroot/js/geolocation.js", import.meta.url),
  "utf8"
);

async function loadGeolocationModule({ navigatorMock, localStorageMock } = {}) {
  const storage = new Map();
  const defaultLocalStorage = {
    getItem: (key) => (storage.has(key) ? storage.get(key) : null),
    setItem: (key, val) => storage.set(key, String(val)),
  };

  const context = createContext({
    Date,
    JSON,
    Math,
    Number,
    Error,
    Promise,
    setTimeout,
    clearTimeout,
    console,
    navigator: navigatorMock ?? {},
    localStorage: localStorageMock ?? defaultLocalStorage,
  });

  const module = new SourceTextModule(source, { context });
  await module.link(() => assert.fail("Unexpected static dependency"));
  await module.evaluate();
  return { api: module.namespace, storage };
}

test("getCachedLocation returns cached coordinates regardless of age", async () => {
  const { api, storage } = await loadGeolocationModule();
  assert.equal(api.getCachedLocation(), null);

  storage.set(
    "user_location_cache_v1",
    JSON.stringify({
      latitude: 3.139,
      longitude: 101.6869,
      accuracy: 42,
      timestamp: Date.now() - 3600_000,
    })
  );

  const cached = api.getCachedLocation();
  assert.ok(cached);
  assert.equal(cached.latitude, 3.139);
  assert.equal(cached.longitude, 101.6869);
  assert.equal(cached.accuracy, 42);
  assert.equal(cached.source, "cache");
});

test("getUserLocation resolves immediately on coarse initial accuracy without blocking 5s in watchPosition", async () => {
  let watchCalled = false;
  let capturedOptions = null;

  const { api } = await loadGeolocationModule({
    navigatorMock: {
      geolocation: {
        getCurrentPosition(success, _error, options) {
          capturedOptions = options;
          success({
            coords: {
              latitude: 5.4141,
              longitude: 100.3288,
              accuracy: 350, // > 100m (typical Wi-Fi/desktop accuracy)
            },
          });
        },
        watchPosition() {
          watchCalled = true;
          return 1;
        },
        clearWatch() {},
      },
    },
  });

  const result = await api.getUserLocation({
    useCache: true,
    cacheMaxAge: 60000,
    enableHighAccuracy: false,
  });

  assert.ok(result);
  assert.equal(result.latitude, 5.4141);
  assert.equal(result.longitude, 100.3288);
  assert.equal(result.accuracy, 350);
  assert.equal(result.source, "fresh");
  assert.equal(watchCalled, false);
  assert.equal(capturedOptions.enableHighAccuracy, false);
  assert.equal(capturedOptions.maximumAge, 60000);
});

test("getUserLocation falls back to low-accuracy network location when high-accuracy times out", async () => {
  const calls = [];

  const { api } = await loadGeolocationModule({
    navigatorMock: {
      geolocation: {
        getCurrentPosition(success, error, options) {
          calls.push(options);
          if (options.enableHighAccuracy) {
            error({ code: 3, TIMEOUT: 3, message: "High accuracy timed out" });
            return;
          }
          success({
            coords: {
              latitude: 3.1478,
              longitude: 101.6953,
              accuracy: 120,
            },
          });
        },
      },
    },
  });

  const result = await api.getUserLocation({
    useCache: false,
    enableHighAccuracy: true,
  });

  assert.equal(calls.length, 2);
  assert.equal(calls[0].enableHighAccuracy, true);
  assert.equal(calls[1].enableHighAccuracy, false);
  assert.ok(result);
  assert.equal(result.latitude, 3.1478);
  assert.equal(result.longitude, 101.6953);
});

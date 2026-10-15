import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, runInContext } from "node:vm";

const devSource = await readFile(
  new URL("../../MyTransportAppWASM/wwwroot/service-worker.js", import.meta.url),
  "utf8"
);
const prodSource = await readFile(
  new URL("../../MyTransportAppWASM/wwwroot/service-worker.published.js", import.meta.url),
  "utf8"
);

function createMockCache() {
  const store = new Map();
  return {
    async match(req) {
      const key = typeof req === "string" ? req : req.url;
      return store.get(key) || null;
    },
    async put(req, res) {
      const key = typeof req === "string" ? req : req.url;
      store.set(key, res);
    },
    async addAll(requests) {
      for (const req of requests) {
        store.set(req.url, { url: req.url, status: 200, cloned: true });
      }
    },
    _store: store
  };
}

function createMockCaches() {
  const cachesMap = new Map();
  return {
    async open(name) {
      if (!cachesMap.has(name)) {
        cachesMap.set(name, createMockCache());
      }
      return cachesMap.get(name);
    },
    async keys() {
      return Array.from(cachesMap.keys());
    },
    async delete(name) {
      return cachesMap.delete(name);
    },
    async match(req) {
      for (const cache of cachesMap.values()) {
        const found = await cache.match(req);
        if (found) return found;
      }
      return null;
    },
    _map: cachesMap
  };
}

function setupDevWorker(mockFetch) {
  const handlers = {};
  const mockCaches = createMockCaches();
  const self = {
    location: new URL("https://example.com/service-worker.js"),
    addEventListener: (name, fn) => { handlers[name] = fn; },
    skipWaiting: () => {},
    clients: { claim: async () => {} }
  };

  runInContext(
    devSource,
    createContext({
      self,
      URL,
      caches: mockCaches,
      fetch: mockFetch
    })
  );

  return { handlers, caches: mockCaches };
}

function setupProdWorker(mockFetch, manifestAssets = [], registrationScope = null) {
  const handlers = {};
  const mockCaches = createMockCaches();
  const self = {
    origin: "https://example.com",
    location: new URL(registrationScope ? `${registrationScope}service-worker.js` : "https://example.com/service-worker.js"),
    registration: registrationScope ? { scope: registrationScope } : undefined,
    assetsManifest: {
      version: "test-v1",
      assets: manifestAssets
    },
    importScripts: () => {},
    addEventListener: (name, fn) => { handlers[name] = fn; },
    clients: { claim: async () => {} }
  };

  class MockRequest {
    constructor(url, opts) {
      const base = registrationScope || "https://example.com/";
      this.url = new URL(url, base).href;
      this.opts = opts;
    }
  }

  runInContext(
    prodSource,
    createContext({
      self,
      URL,
      Request: MockRequest,
      caches: mockCaches,
      fetch: mockFetch
    })
  );

  return { handlers, caches: mockCaches };
}

test("dev worker intercepts and caches GTFS static routes via stale-while-revalidate", async () => {
  let fetchCount = 0;
  const mockResponse = {
    status: 200,
    clone() { return { ...this }; }
  };
  const mockFetch = async (req) => {
    fetchCount++;
    return mockResponse;
  };

  const { handlers, caches } = setupDevWorker(mockFetch);

  let respondedWithPromise = null;
  const event = {
    request: {
      method: "GET",
      url: "https://api.data.gov.my/gtfs-static/rapid-bus-kl/routes"
    },
    respondWith: (p) => { respondedWithPromise = p; }
  };

  handlers.fetch(event);
  assert.ok(respondedWithPromise, "Expected event.respondWith to be called for GTFS static routes");

  const response = await respondedWithPromise;
  assert.equal(response.status, 200);
  assert.equal(fetchCount, 1);

  // Subsequent fetch: should return cached version immediately
  let cachedPromise = null;
  const event2 = {
    request: {
      method: "GET",
      url: "https://api.data.gov.my/gtfs-static/rapid-bus-kl/routes"
    },
    respondWith: (p) => { cachedPromise = p; }
  };

  handlers.fetch(event2);
  const response2 = await cachedPromise;
  assert.equal(response2.status, 200);
});

test("dev worker caches recreation_centres.json and bypasses non-static requests", async () => {
  const { handlers } = setupDevWorker(async () => ({ status: 200, clone() { return this; } }));

  // recreation_centres.json should be intercepted
  let rcPromise = null;
  handlers.fetch({
    request: { method: "GET", url: "https://example.com/data/recreation_centres.json" },
    respondWith: (p) => { rcPromise = p; }
  });
  assert.ok(rcPromise, "recreation_centres.json should be intercepted by dev worker");
  const rcRes = await rcPromise;
  assert.equal(rcRes.status, 200);

  // app assembly should NOT be intercepted in dev worker
  let dllPromise = null;
  handlers.fetch({
    request: { method: "GET", url: "https://example.com/_framework/MyTransportAppWASM.wasm" },
    respondWith: (p) => { dllPromise = p; }
  });
  assert.equal(dllPromise, null, "wasm framework file should bypass dev worker to avoid stale hot reload");
});

test("prod worker pre-caches manifest assets and caches GTFS static routes with offline fallback", async () => {
  const manifest = [
    { url: "data/recreation_centres.json", hash: "sha256-abc" },
    { url: "index.html", hash: "sha256-def" },
    { url: "myTransport.svg", hash: "sha256-ghi" }
  ];

  const mockResponse = {
    status: 200,
    clone() { return { ...this }; }
  };

  let online = true;
  const mockFetch = async () => {
    if (!online) throw new Error("Offline network failure");
    return mockResponse;
  };

  const { handlers, caches } = setupProdWorker(mockFetch, manifest);

  // Install event
  let installPromise = null;
  handlers.install({ waitUntil: (p) => { installPromise = p; } });
  await installPromise;

  const offlineCache = await caches.open("offline-cache-test-v1");
  const cachedRc = await offlineCache.match("https://example.com/data/recreation_centres.json");
  assert.ok(cachedRc, "recreation_centres.json must be pre-cached on install");

  // Fetch GTFS static data online first
  let gtfsPromise = null;
  handlers.fetch({
    request: {
      method: "GET",
      url: "https://api.data.gov.my/gtfs-static/rapid-bus-kl/routes"
    },
    respondWith: (p) => { gtfsPromise = p; }
  });
  await gtfsPromise;

  // Now go offline and verify GTFS static lookup succeeds from cache
  online = false;
  let offlineGtfsPromise = null;
  handlers.fetch({
    request: {
      method: "GET",
      url: "https://api.data.gov.my/gtfs-static/rapid-bus-kl/routes"
    },
    respondWith: (p) => { offlineGtfsPromise = p; }
  });
  const offlineResult = await offlineGtfsPromise;
  assert.equal(offlineResult.status, 200);
});

test("dev worker intercepts and caches configured static routes, departures, and map endpoints", async () => {
  let fetchCount = 0;
  const mockResponse = { status: 200, clone() { return { ...this }; } };
  const mockFetch = async () => {
    fetchCount++;
    return mockResponse;
  };

  const { handlers } = setupDevWorker(mockFetch);

  // 1. /data/routes endpoint
  let routesPromise = null;
  handlers.fetch({
    request: { method: "GET", url: "http://localhost:8787/rapid-bus-kl/data/routes?limit=100" },
    respondWith: (p) => { routesPromise = p; }
  });
  assert.ok(routesPromise, "/data/routes should be intercepted by dev worker");
  const res1 = await routesPromise;
  assert.equal(res1.status, 200);

  // 2. /departures endpoint
  let depPromise = null;
  handlers.fetch({
    request: { method: "GET", url: "http://localhost:8787/rapid-bus-kl/departures?stop_id=123" },
    respondWith: (p) => { depPromise = p; }
  });
  assert.ok(depPromise, "/departures should be intercepted by dev worker");
  const res2 = await depPromise;
  assert.equal(res2.status, 200);

  // 3. /map endpoint
  let mapPromise = null;
  handlers.fetch({
    request: { method: "GET", url: "http://localhost:8787/rapid-bus-kl/map?route_id=U60" },
    respondWith: (p) => { mapPromise = p; }
  });
  assert.ok(mapPromise, "/map should be intercepted by dev worker");
  const res3 = await mapPromise;
  assert.equal(res3.status, 200);
  assert.equal(fetchCount, 3);
});

test("prod worker caches configured static routes and departures with offline fallback", async () => {
  const manifest = [
    { url: "data/recreation_centres.json", hash: "sha256-abc" },
    { url: "index.html", hash: "sha256-def" }
  ];

  let online = true;
  const mockFetch = async () => {
    if (!online) throw new Error("Network unreachable");
    return { status: 200, clone() { return { ...this }; } };
  };

  const { handlers } = setupProdWorker(mockFetch, manifest);

  // Fetch online
  let onlinePromise = null;
  handlers.fetch({
    request: { method: "GET", url: "http://localhost:8787/rapid-rail-kl/data/routes" },
    respondWith: (p) => { onlinePromise = p; }
  });
  await onlinePromise;

  // Now go offline
  online = false;
  let offlinePromise = null;
  handlers.fetch({
    request: { method: "GET", url: "http://localhost:8787/rapid-rail-kl/data/routes" },
    respondWith: (p) => { offlinePromise = p; }
  });
  assert.ok(offlinePromise, "Static route should be served offline");
  const result = await offlinePromise;
  assert.equal(result.status, 200);
});

test("prod worker correctly scopes subfolder hosting (e.g. /MyTransportProject/)", async () => {
  const manifest = [
    { url: "data/recreation_centres.json", hash: "sha256-rc" },
    { url: "index.html", hash: "sha256-idx" }
  ];

  const subfolderScope = "https://example.com/MyTransportProject/";
  const { handlers, caches } = setupProdWorker(async () => ({ status: 200, clone() { return this; } }), manifest, subfolderScope);

  let installPromise = null;
  handlers.install({ waitUntil: (p) => { installPromise = p; } });
  await installPromise;

  const offlineCache = await caches.open("offline-cache-test-v1");
  const cachedRc = await offlineCache.match("https://example.com/MyTransportProject/data/recreation_centres.json");
  assert.ok(cachedRc, "recreation_centres.json must be pre-cached under the subfolder path");

  // Navigation request under subfolder should match
  let navPromise = null;
  handlers.fetch({
    request: {
      mode: "navigate",
      method: "GET",
      url: "https://example.com/MyTransportProject/weather"
    },
    respondWith: (p) => { navPromise = p; }
  });
  assert.ok(navPromise, "Navigation request under subfolder should be intercepted");
  const navResponse = await navPromise;
  assert.ok(navResponse, "Navigation request under subfolder should return response");
});

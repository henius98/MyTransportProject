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
        store.set(req.url, { url: req.url, opts: req.opts, status: 200, cloned: true });
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
      Response,
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
      Response,
      Request: MockRequest,
      caches: mockCaches,
      fetch: mockFetch
    })
  );

  return { handlers, caches: mockCaches };
}

function dispatchFetch(handlers, url, mode) {
  let promise = null;
  handlers.fetch({
    request: mode ? { mode, method: "GET", url } : { method: "GET", url },
    respondWith: (p) => { promise = p; }
  });
  return promise;
}

function dispatchInstall(handlers) {
  let promise = null;
  handlers.install({ waitUntil: (p) => { promise = p; } });
  return promise;
}

test("dev worker intercepts and caches GTFS static routes via stale-while-revalidate", async () => {
  let fetchCount = 0;
  const mockResponse = {
    status: 200,
    clone() { return { ...this }; }
  };
  const mockFetch = async () => {
    fetchCount++;
    return mockResponse;
  };

  const { handlers } = setupDevWorker(mockFetch);

  const respondedWithPromise = dispatchFetch(handlers, "https://api.data.gov.my/gtfs-static/rapid-bus-kl/routes");
  assert.ok(respondedWithPromise, "Expected event.respondWith to be called for GTFS static routes");

  const response = await respondedWithPromise;
  assert.equal(response.status, 200);
  assert.equal(fetchCount, 1);

  // Subsequent fetch: should return cached version immediately
  const cachedPromise = dispatchFetch(handlers, "https://api.data.gov.my/gtfs-static/rapid-bus-kl/routes");
  const response2 = await cachedPromise;
  assert.equal(response2.status, 200);
});

test("dev worker caches recreation_centres.json and bypasses non-static requests", async () => {
  const { handlers } = setupDevWorker(async () => ({ status: 200, clone() { return this; } }));

  // recreation_centres.json should be intercepted
  const rcPromise = dispatchFetch(handlers, "https://example.com/data/recreation_centres.json");
  assert.ok(rcPromise, "recreation_centres.json should be intercepted by dev worker");
  assert.equal((await rcPromise).status, 200);

  // app assembly should NOT be intercepted in dev worker
  const dllPromise = dispatchFetch(handlers, "https://example.com/_framework/MyTransportAppWASM.wasm");
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
  await dispatchInstall(handlers);

  const offlineCache = await caches.open("offline-cache-test-v1");
  const cachedRc = await offlineCache.match("https://example.com/data/recreation_centres.json");
  assert.ok(cachedRc, "recreation_centres.json must be pre-cached on install");

  // Fetch GTFS static data online first
  await dispatchFetch(handlers, "https://api.data.gov.my/gtfs-static/rapid-bus-kl/routes");

  // Now go offline and verify GTFS static lookup succeeds from cache
  online = false;
  const offlineResult = await dispatchFetch(handlers, "https://api.data.gov.my/gtfs-static/rapid-bus-kl/routes");
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

  for (const [label, url] of [
    ["/data/routes", "http://localhost:8787/rapid-bus-kl/data/routes?limit=100"],
    ["/departures", "http://localhost:8787/rapid-bus-kl/departures?stop_id=123"],
    ["/map", "http://localhost:8787/rapid-bus-kl/map?route_id=U60"]
  ]) {
    const resPromise = dispatchFetch(handlers, url);
    assert.ok(resPromise, `${label} should be intercepted by dev worker`);
    assert.equal((await resPromise).status, 200);
  }
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
  await dispatchFetch(handlers, "http://localhost:8787/rapid-rail-kl/data/routes");

  // Now go offline
  online = false;
  const offlinePromise = dispatchFetch(handlers, "http://localhost:8787/rapid-rail-kl/data/routes");
  assert.ok(offlinePromise, "Static route should be served offline");
  assert.equal((await offlinePromise).status, 200);
});

test("prod worker correctly scopes subfolder hosting (e.g. /MyTransportProject/)", async () => {
  const manifest = [
    { url: "data/recreation_centres.json", hash: "sha256-rc" },
    { url: "index.html", hash: "sha256-idx" }
  ];

  const subfolderScope = "https://example.com/MyTransportProject/";
  const { handlers, caches } = setupProdWorker(async () => ({ status: 200, clone() { return this; } }), manifest, subfolderScope);

  await dispatchInstall(handlers);

  const offlineCache = await caches.open("offline-cache-test-v1");
  const cachedRc = await offlineCache.match("https://example.com/MyTransportProject/data/recreation_centres.json");
  assert.ok(cachedRc, "recreation_centres.json must be pre-cached under the subfolder path");

  // Navigation request under subfolder should match
  const navPromise = dispatchFetch(handlers, "https://example.com/MyTransportProject/weather", "navigate");
  assert.ok(navPromise, "Navigation request under subfolder should be intercepted");
  assert.ok(await navPromise, "Navigation request under subfolder should return response");
});

test("dev and prod workers do not mistake /map SPA navigation for GTFS static data and return 503 on uncached fetch failure", async () => {
  const failingFetch = async () => {
    throw new TypeError("Failed to fetch");
  };

  const { handlers: devHandlers } = setupDevWorker(failingFetch);

  // 1. SPA navigation to /map must bypass dev worker
  assert.equal(
    dispatchFetch(devHandlers, "https://example.com/map", "navigate"),
    null,
    "/map navigation should not be intercepted by dev worker"
  );

  // 2. Uncached GTFS static fetch failure resolves to 503 instead of unhandled rejection
  const devFailResponse = await dispatchFetch(devHandlers, "http://localhost:8787/rapid-bus-kl/data/routes");
  assert.equal(devFailResponse.status, 503);

  // 3. Prod worker serves index.html on offline /map navigation and 503 on uncached GTFS static failure
  const manifest = [{ url: "index.html", hash: "sha256-idx" }];
  const { handlers: prodHandlers } = setupProdWorker(failingFetch, manifest);
  await dispatchInstall(prodHandlers);

  const prodNavResponse = await dispatchFetch(prodHandlers, "https://example.com/map", "navigate");
  assert.equal(prodNavResponse.status, 200);
  assert.equal(prodNavResponse.url, "https://example.com/index.html");

  const prodFailResponse = await dispatchFetch(prodHandlers, "http://localhost:8787/rapid-bus-kl/data/routes");
  assert.equal(prodFailResponse.status, 503);
});

test("prod worker uses force-cache only for fingerprinted _framework assets", async () => {
  const manifest = [
    { url: "_framework/dotnet.js", hash: "sha256-dotnet" },
    { url: "_framework/blazor.webassembly.js", hash: "sha256-blazor" },
    { url: "_framework/System.Private.CoreLib.abc123def0.wasm", hash: "sha256-core" },
    { url: "css/app.css", hash: "sha256-css" }
  ];

  const { handlers, caches } = setupProdWorker(async () => ({ status: 200, clone() { return this; } }), manifest);
  await dispatchInstall(handlers);

  const offlineCache = await caches.open("offline-cache-test-v1");
  assert.equal((await offlineCache.match("https://example.com/_framework/dotnet.js"))?.opts?.cache, "no-cache");
  assert.equal((await offlineCache.match("https://example.com/_framework/blazor.webassembly.js"))?.opts?.cache, "no-cache");
  assert.equal((await offlineCache.match("https://example.com/_framework/System.Private.CoreLib.abc123def0.wasm"))?.opts?.cache, "force-cache");
  assert.equal((await offlineCache.match("https://example.com/css/app.css"))?.opts?.cache, "no-cache");
});

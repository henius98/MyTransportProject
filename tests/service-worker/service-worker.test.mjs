import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, runInContext } from "node:vm";

const wwwrootDir = new URL("../../MyTransportAppWASM/wwwroot/", import.meta.url);
const devSource = await readFile(new URL("service-worker.js", wwwrootDir), "utf8");
const prodSource = await readFile(new URL("service-worker.published.js", wwwrootDir), "utf8");
const wasmGzipSource = await readFile(new URL("js/wasm-gzip.js", wwwrootDir), "utf8");
const indexHtmlSource = await readFile(new URL("index.html", wwwrootDir), "utf8");
const localScripts = new Map([
  ["./js/wasm-gzip.js", wasmGzipSource]
]);

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

function setupProdWorker(mockFetch, manifestAssets = [], registrationScope = null, extraGlobals = {}) {
  const handlers = {};
  const mockCaches = createMockCaches();
  let context;
  const self = {
    origin: "https://example.com",
    location: new URL(registrationScope ? `${registrationScope}service-worker.js` : "https://example.com/service-worker.js"),
    registration: registrationScope ? { scope: registrationScope } : undefined,
    assetsManifest: {
      version: "test-v1",
      assets: manifestAssets
    },
    importScripts: (...paths) => {
      for (const path of paths) {
        const script = localScripts.get(path);
        if (script) {
          runInContext(script, context);
        }
      }
    },
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

  context = createContext({
    self,
    URL,
    Response,
    Headers,
    Request: MockRequest,
    caches: mockCaches,
    fetch: mockFetch,
    ...extraGlobals
  });

  runInContext(prodSource, context);

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

async function createWasmGzipFixture() {
  const rawWasm = new Uint8Array([0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00]);
  const digest = await globalThis.crypto.subtle.digest("SHA-256", rawWasm);
  const hash = "sha256-" + Buffer.from(digest).toString("base64");
  const gzBytes = await new Response(
    new Response(rawWasm).body.pipeThrough(new globalThis.CompressionStream("gzip"))
  ).arrayBuffer();
  return { rawWasm, hash, gzBytes };
}

test("prod worker pre-caches compressed _framework .wasm.gz via DecompressionStream and verifies SHA-256", async () => {
  const { rawWasm, hash, gzBytes } = await createWasmGzipFixture();

  const fetched = [];
  const manifest = [
    { url: "_framework/app.abc12345.wasm", hash },
    { url: "_framework/tampered.def67890.wasm", hash: "sha256-wrong" }
  ];

  const { handlers, caches } = setupProdWorker(
    async (url, opts) => {
      fetched.push({ url, opts });
      if (url.endsWith(".gz")) {
        return new Response(gzBytes, { status: 200, headers: { "Content-Type": "application/gzip" } });
      }
      return { status: 200, clone() { return this; } };
    },
    manifest,
    null,
    {
      DecompressionStream: globalThis.DecompressionStream,
      crypto: globalThis.crypto,
      btoa: globalThis.btoa
    }
  );
  // Attach crypto to self as well since service-worker checks self.crypto
  await dispatchInstall(handlers);

  const offlineCache = await caches.open("offline-cache-test-v1");
  const cachedValid = await offlineCache.match("https://example.com/_framework/app.abc12345.wasm");
  assert.ok(cachedValid, "valid .wasm.gz should be decompressed and cached under .wasm URL");
  assert.equal(cachedValid.headers.get("Content-Type"), "application/wasm");
  assert.deepEqual(new Uint8Array(await cachedValid.arrayBuffer()), rawWasm);

  const cachedTampered = await offlineCache.match("https://example.com/_framework/tampered.def67890.wasm");
  assert.ok(cachedTampered?.opts?.integrity === "sha256-wrong", "mismatched hash falls back to standard Request with SRI");
  assert.equal(fetched.filter(f => f.url.endsWith(".gz")).length, 2);
});

test("prod worker handles Content-Encoding: gzip and Content-Encoding: br on .wasm.gz and strips Content-Encoding", async () => {
  const { rawWasm, hash, gzBytes } = await createWasmGzipFixture();

  const manifest = [
    { url: "_framework/predecoded.11111111.wasm", hash },
    { url: "_framework/brotli-wrapped.22222222.wasm", hash }
  ];

  const { handlers, caches } = setupProdWorker(
    async (url) => {
      if (url.endsWith("predecoded.11111111.wasm.gz")) {
        // Browser already decoded Content-Encoding: gzip, so body is raw wasm bytes.
        return new Response(rawWasm, {
          status: 200,
          headers: { "Content-Type": "application/octet-stream", "Content-Encoding": "gzip" }
        });
      }
      if (url.endsWith("brotli-wrapped.22222222.wasm.gz")) {
        // Intermediary added Content-Encoding: br around .wasm.gz; browser decoded br so body is still gzip bytes.
        return new Response(gzBytes, {
          status: 200,
          headers: { "Content-Type": "application/gzip", "Content-Encoding": "br" }
        });
      }
      return { status: 200, clone() { return this; } };
    },
    manifest,
    null,
    {
      DecompressionStream: globalThis.DecompressionStream,
      crypto: globalThis.crypto,
      btoa: globalThis.btoa
    }
  );

  await dispatchInstall(handlers);
  const offlineCache = await caches.open("offline-cache-test-v1");

  for (const name of ["predecoded.11111111.wasm", "brotli-wrapped.22222222.wasm"]) {
    const cached = await offlineCache.match(`https://example.com/_framework/${name}`);
    assert.ok(cached, `${name} should be cached`);
    assert.equal(cached.headers.get("Content-Type"), "application/wasm");
    assert.equal(cached.headers.get("Content-Encoding"), null, `${name} must strip Content-Encoding header`);
    assert.deepEqual(new Uint8Array(await cached.arrayBuffer()), rawWasm);
  }
});

test("wasm-gzip skips .gz fetch when crypto.subtle is absent or integrity is not sha256-, falling back to SRI", async () => {
  const { hash: sha256Hash, gzBytes } = await createWasmGzipFixture();

  // 1. crypto.subtle absent (plain non-localhost HTTP)
  const noSubtleGzFetches = [];
  const manifestNoSubtle = [{ url: "_framework/nosubtle.11111111.wasm", hash: sha256Hash }];
  const { handlers: noSubtleHandlers, caches: noSubtleCaches } = setupProdWorker(
    async (url) => {
      if (url.endsWith(".gz")) {
        noSubtleGzFetches.push(url);
        return new Response(gzBytes, { status: 200, headers: { "Content-Type": "application/gzip" } });
      }
      return { status: 200, clone() { return this; } };
    },
    manifestNoSubtle,
    null,
    {
      DecompressionStream: globalThis.DecompressionStream,
      crypto: {},
      btoa: globalThis.btoa
    }
  );
  await dispatchInstall(noSubtleHandlers);
  assert.equal(noSubtleGzFetches.length, 0, "missing crypto.subtle must skip .wasm.gz fetch");
  const noSubtleCache = await noSubtleCaches.open("offline-cache-test-v1");
  const fallbackNoSubtle = await noSubtleCache.match("https://example.com/_framework/nosubtle.11111111.wasm");
  assert.equal(fallbackNoSubtle?.opts?.integrity, sha256Hash, "missing crypto.subtle must fall back to SRI Request");

  // 2. Non-sha256 integrity hash (e.g. sha384-)
  const sha384GzFetches = [];
  const sha384Hash = "sha384-unsupported";
  const manifestSha384 = [{ url: "_framework/sha384.22222222.wasm", hash: sha384Hash }];
  const { handlers: sha384Handlers, caches: sha384Caches } = setupProdWorker(
    async (url) => {
      if (url.endsWith(".gz")) {
        sha384GzFetches.push(url);
        return new Response(gzBytes, { status: 200, headers: { "Content-Type": "application/gzip" } });
      }
      return { status: 200, clone() { return this; } };
    },
    manifestSha384,
    null,
    {
      DecompressionStream: globalThis.DecompressionStream,
      crypto: globalThis.crypto,
      btoa: globalThis.btoa
    }
  );
  await dispatchInstall(sha384Handlers);
  assert.equal(sha384GzFetches.length, 0, "non-sha256 integrity must skip .wasm.gz fetch");
  const sha384Cache = await sha384Caches.open("offline-cache-test-v1");
  const fallbackSha384 = await sha384Cache.match("https://example.com/_framework/sha384.22222222.wasm");
  assert.equal(fallbackSha384?.opts?.integrity, sha384Hash, "non-sha256 integrity must fall back to SRI Request");
});

test("prod worker bounds concurrent .wasm.gz pre-caching during install", async () => {
  const { hash, gzBytes } = await createWasmGzipFixture();

  const manifest = Array.from({ length: 12 }, (_, i) => ({
    url: `_framework/mod${i}.12345678.wasm`,
    hash
  }));

  let active = 0;
  let peak = 0;
  const { handlers } = setupProdWorker(
    async (url) => {
      if (url.endsWith(".gz")) {
        active++;
        peak = Math.max(peak, active);
        await new Promise(resolve => setTimeout(resolve, 5));
        active--;
        return new Response(gzBytes, { status: 200, headers: { "Content-Type": "application/gzip" } });
      }
      return { status: 200, clone() { return this; } };
    },
    manifest,
    null,
    {
      DecompressionStream: globalThis.DecompressionStream,
      crypto: globalThis.crypto,
      btoa: globalThis.btoa
    }
  );

  await dispatchInstall(handlers);
  assert.ok(peak <= 6, `Expected at most 6 concurrent .wasm.gz fetches, saw ${peak}`);
  assert.ok(peak > 1, `Expected parallel .wasm.gz fetches, saw peak ${peak}`);
});

test("index.html loadBootResource falls back once to defaultUri on rejected .gz fetch or integrity mismatch and skips .gz when unverifiable", async () => {
  const match = indexHtmlSource.match(/<script>\s*\(function \(\) \{\s*function startBlazor\(\)[\s\S]*?<\/script>/);
  assert.ok(match, "Expected startBlazor inline script in index.html");
  const bootScript = match[0].replace(/^<script>|<\/script>$/g, "");

  const { rawWasm, gzBytes } = await createWasmGzipFixture();

  const runBootLoader = async (mockFetch, cryptoOverride = globalThis.crypto) => {
    let loadBootResource;
    const windowObj = {
      crypto: cryptoOverride
    };
    const context = createContext({
      window: windowObj,
      self: windowObj,
      document: { readyState: "complete", baseURI: "https://example.com/" },
      navigator: {},
      URL,
      Response,
      Headers,
      DecompressionStream: globalThis.DecompressionStream,
      crypto: cryptoOverride,
      btoa: globalThis.btoa,
      fetch: mockFetch,
      Blazor: {
        start: (opts) => { loadBootResource = opts.loadBootResource; }
      }
    });
    runInContext(wasmGzipSource, context);
    windowObj.wasmGzip = context.wasmGzip;
    runInContext(bootScript, context);
    return loadBootResource;
  };

  // Case 1: rejected fetch(.wasm.gz) falls back to defaultUri with integrity
  const calls1 = [];
  const loader1 = await runBootLoader(async (url, opts) => {
    calls1.push({ url: String(url), integrity: opts?.integrity });
    if (String(url).endsWith(".gz")) {
      throw new TypeError("Network error on .gz");
    }
    return new Response(rawWasm, { status: 200, headers: { "Content-Type": "application/wasm" } });
  });
  const res1 = await loader1("assembly", "app.wasm", "_framework/app.wasm", "sha256-expected");
  assert.equal(res1.status, 200);
  assert.deepEqual(calls1, [
    { url: "https://example.com/_framework/app.wasm.gz", integrity: undefined },
    { url: "_framework/app.wasm", integrity: "sha256-expected" }
  ]);

  // Case 2: integrity mismatch on .wasm.gz falls back once to defaultUri
  const calls2 = [];
  const loader2 = await runBootLoader(async (url, opts) => {
    calls2.push({ url: String(url), integrity: opts?.integrity });
    if (String(url).endsWith(".gz")) {
      return new Response(gzBytes, { status: 200, headers: { "Content-Type": "application/gzip" } });
    }
    return new Response(rawWasm, { status: 200, headers: { "Content-Type": "application/wasm" } });
  });
  const res2 = await loader2("dotnetwasm", "dotnet.wasm", "_framework/dotnet.wasm", "sha256-wrong");
  assert.equal(res2.status, 200);
  assert.deepEqual(calls2, [
    { url: "https://example.com/_framework/dotnet.wasm.gz", integrity: undefined },
    { url: "_framework/dotnet.wasm", integrity: "sha256-wrong" }
  ]);

  // Case 3: missing crypto.subtle, empty integrity, or non-sha256 integrity returns undefined without fetching .gz
  const calls3 = [];
  const loaderNoSubtle = await runBootLoader(async (url) => {
    calls3.push(String(url));
    return new Response(gzBytes, { status: 200 });
  }, {});
  assert.equal(loaderNoSubtle("assembly", "app.wasm", "_framework/app.wasm", "sha256-expected"), undefined);
  assert.equal(loader2("assembly", "app.wasm", "_framework/app.wasm", "sha384-unsupported"), undefined);
  assert.equal(loader2("assembly", "app.wasm", "_framework/app.wasm", ""), undefined);
  assert.equal(loader2("assembly", "app.wasm", "_framework/app.wasm", null), undefined);
  assert.deepEqual(calls3, []);
});

test("wasm-gzip rejects empty integrity and gzipped non-WASM HTML fallback responses", async () => {
  const htmlBytes = new TextEncoder().encode("<!doctype html><html><body>SPA fallback</body></html>");
  const digest = await globalThis.crypto.subtle.digest("SHA-256", htmlBytes);
  const htmlHash = "sha256-" + Buffer.from(digest).toString("base64");

  const context = createContext({
    self: { crypto: globalThis.crypto },
    Response,
    Headers,
    DecompressionStream: globalThis.DecompressionStream,
    crypto: globalThis.crypto,
    btoa: globalThis.btoa,
    fetch: async () => new Response(htmlBytes, {
      status: 200,
      headers: { "Content-Type": "text/html", "Content-Encoding": "gzip" }
    })
  });
  runInContext(wasmGzipSource, context);

  assert.equal(context.wasmGzip.canVerifyIntegrity(""), false);
  assert.equal(context.wasmGzip.canVerifyIntegrity(undefined), false);
  assert.equal(await context.wasmGzip.verifySha256(htmlBytes.buffer, ""), false);

  // Even if an attacker or proxy matched SHA-256 or served gzipped HTML, non-WASM bytes must be rejected.
  const rejected = await context.wasmGzip.fetchDecompressedWasm("https://example.com/_framework/app.wasm.gz", htmlHash);
  assert.equal(rejected, null);
});

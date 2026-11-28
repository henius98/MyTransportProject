import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, runInContext } from "node:vm";

const wwwrootDir = new URL("../../MyTransportAppWASM/wwwroot/", import.meta.url);
const devSource = await readFile(new URL("service-worker.js", wwwrootDir), "utf8");
const prodSource = await readFile(new URL("service-worker.published.js", wwwrootDir), "utf8");
const wasmGzipSource = await readFile(new URL("js/wasm-gzip.js", wwwrootDir), "utf8");
const staticDataCacheSource = await readFile(new URL("js/static-data-cache.js", wwwrootDir), "utf8");
const indexHtmlSource = await readFile(new URL("index.html", wwwrootDir), "utf8");
const localScripts = new Map([
  ["./js/wasm-gzip.js", wasmGzipSource],
  ["./js/static-data-cache.js", staticDataCacheSource]
]);

function createMockCache() {
  const store = new Map();
  return {
    async match(req) {
      const key = typeof req === "string" ? req : req.url;
      const entry = store.get(key);
      if (!entry) return null;
      return typeof entry.clone === "function" ? entry.clone() : entry;
    },
    async put(req, res) {
      const key = typeof req === "string" ? req : req.url;
      store.set(key, res);
    },
    async keys() {
      return Array.from(store.keys()).map(url => ({ url }));
    },
    async delete(req) {
      const key = typeof req === "string" ? req : req.url;
      return store.delete(key);
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

function createFakeDate(getNowMs) {
  return class FakeDate extends Date {
    constructor(...args) {
      if (args.length === 0) {
        super(getNowMs());
      } else {
        super(...args);
      }
    }
    static now() {
      return getNowMs();
    }
  };
}

function setupDevWorker(mockFetch, options = {}) {
  const handlers = {};
  const mockCaches = options.caches || createMockCaches();
  let context;
  const self = {
    origin: "https://example.com",
    location: new URL("https://example.com/service-worker.js"),
    importScripts: (...paths) => {
      for (const path of paths) {
        const script = localScripts.get(path);
        if (script) {
          runInContext(script, context);
        }
      }
    },
    addEventListener: (name, fn) => { handlers[name] = fn; },
    skipWaiting: () => {},
    clients: { claim: async () => {} }
  };

  context = createContext({
    self,
    URL,
    Response,
    Headers,
    caches: mockCaches,
    fetch: mockFetch,
    ...(options.getNowMs ? { Date: createFakeDate(options.getNowMs) } : {})
  });

  runInContext(devSource, context);

  return { handlers, caches: mockCaches };
}

function setupProdWorker(mockFetch, manifestAssets = [], registrationScope = null, extraGlobals = {}, sharedCaches = null) {
  const handlers = {};
  const mockCaches = sharedCaches || createMockCaches();
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

function dispatchMessage(handlers, data) {
  let promise = null;
  handlers.message({
    data,
    waitUntil: (p) => { promise = p; }
  });
  return promise;
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

test("dev worker intercepts and caches GTFS static routes via cache-first with zero background revalidation", async () => {
  let fetchCount = 0;
  const mockFetch = async () => {
    fetchCount++;
    return new Response(JSON.stringify({ data: [{ route_id: "U60" }] }), {
      status: 200,
      headers: { "Content-Type": "application/json" }
    });
  };

  const { handlers } = setupDevWorker(mockFetch);

  const respondedWithPromise = dispatchFetch(handlers, "https://api.data.gov.my/gtfs-static/rapid-bus-kl/routes");
  assert.ok(respondedWithPromise, "Expected event.respondWith to be called for GTFS static routes");

  const response = await respondedWithPromise;
  assert.equal(response.status, 200);
  assert.equal(fetchCount, 1);

  // Subsequent fetch within the same service day: served cache-first without hitting network
  const cachedPromise = dispatchFetch(handlers, "https://api.data.gov.my/gtfs-static/rapid-bus-kl/routes");
  const response2 = await cachedPromise;
  assert.equal(response2.status, 200);
  assert.equal(fetchCount, 1);
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

test("dev worker intercepts and caches configured static routes and map endpoints", async () => {
  let fetchCount = 0;
  const mockResponse = { status: 200, clone() { return { ...this }; } };
  const mockFetch = async () => {
    fetchCount++;
    return mockResponse;
  };

  const { handlers } = setupDevWorker(mockFetch);

  for (const [label, url] of [
    ["/data/routes", "http://localhost:8787/rapid-bus-kl/data/routes?limit=100"],
    ["/map", "http://localhost:8787/rapid-bus-kl/map?route_id=U60"]
  ]) {
    const resPromise = dispatchFetch(handlers, url);
    assert.ok(resPromise, `${label} should be intercepted by dev worker`);
    assert.equal((await resPromise).status, 200);
  }
  assert.equal(fetchCount, 2);
});

test("prod worker caches configured static routes with offline fallback", async () => {
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

test("static-data-cache-v1 expires at daily 04:00 MYT, prunes old dated maps, and never serves expired entries on failure", async () => {
  let nowMs = Date.parse("2026-11-26T23:00:00+08:00");
  let fetchCount = 0;
  let nextStatus = 200;
  let failNetwork = false;

  const mockFetch = async (req) => {
    fetchCount++;
    if (failNetwork) {
      throw new TypeError("Offline");
    }
    const url = typeof req === "string" ? req : req.url;
    return new Response(JSON.stringify({ url, fetchCount }), {
      status: nextStatus,
      headers: { "Content-Type": "application/json" }
    });
  };

  const { handlers, caches } = setupProdWorker(
    mockFetch,
    [],
    null,
    { Date: createFakeDate(() => nowMs) }
  );

  const routesUrl = "https://worker.example/rapid-rail-kl/data/routes";
  const oldMapUrl = "https://worker.example/rapid-rail-kl/map?date=2026-11-26";
  const freqUrl = "https://worker.example/rapid-rail-kl/data/frequencies";

  // Initial fetches at 23:00 MYT populate static-data-cache-v1 with X-Cache-Day = 2026-11-26
  assert.equal((await dispatchFetch(handlers, routesUrl)).status, 200);
  assert.equal((await dispatchFetch(handlers, oldMapUrl)).status, 200);
  assert.equal((await dispatchFetch(handlers, freqUrl)).status, 200);
  assert.equal(fetchCount, 3);

  // At 03:59:59 MYT next morning: still same service day -> cache-first with zero network calls
  nowMs = Date.parse("2026-11-27T03:59:59+08:00");
  const hitRoutes = await dispatchFetch(handlers, routesUrl);
  assert.equal(hitRoutes.status, 200);
  assert.equal(hitRoutes.headers.get("X-Cache-Day"), "2026-11-26");
  assert.equal(fetchCount, 3);

  // At 04:00:00 MYT: expired -> prunes all 2026-11-26 entries (including unrequested oldMapUrl) and fetches fresh
  nowMs = Date.parse("2026-11-27T04:00:00+08:00");
  const refreshedRoutes = await dispatchFetch(handlers, routesUrl);
  assert.equal(refreshedRoutes.status, 200);
  assert.equal(fetchCount, 4);

  const staticCache = await caches.open("static-data-cache-v1");
  assert.equal(await staticCache.match(oldMapUrl), null, "Old dated /map entry must be pruned after 04:00 MYT");
  assert.equal(await staticCache.match(freqUrl), null, "Old /data/frequencies entry must be pruned after 04:00 MYT");
  assert.equal((await staticCache.match(routesUrl))?.headers.get("X-Cache-Day"), "2026-11-27");

  // Next day at 04:00:00 MYT with network failure: never serve expired entry, return 503
  nowMs = Date.parse("2026-11-28T04:00:00+08:00");
  failNetwork = true;
  const offlineExpired = await dispatchFetch(handlers, routesUrl);
  assert.equal(offlineExpired.status, 503);

  // Non-200 response is passed through without caching
  failNetwork = false;
  nextStatus = 502;
  const badGateway = await dispatchFetch(handlers, routesUrl);
  assert.equal(badGateway.status, 502);
  assert.equal(await staticCache.match(routesUrl), null);
});

test("static-data-cache-v1 persists custom StaticDataResetHour from postMessage across SW restarts", async () => {
  let nowMs = Date.parse("2026-11-27T05:30:00+08:00");
  let fetchCount = 0;
  const sharedCaches = createMockCaches();
  const mockFetch = async () => {
    fetchCount++;
    return new Response("{\"data\":[]}", { status: 200, headers: { "Content-Type": "application/json" } });
  };

  // First SW instance receives StaticDataResetHour = 6 from Blazor startup
  const sw1 = setupDevWorker(mockFetch, { caches: sharedCaches, getNowMs: () => nowMs });
  await dispatchMessage(sw1.handlers, { type: "SET_STATIC_DATA_RESET_HOUR", resetHour: 6 });

  const routesUrl = "http://localhost:8787/rapid-bus-kl/data/routes";
  assert.equal((await dispatchFetch(sw1.handlers, routesUrl)).status, 200);
  assert.equal(fetchCount, 1);

  // Simulate SW idle termination + fresh restart sharing Cache Storage (no new postMessage)
  const sw2 = setupDevWorker(mockFetch, { caches: sharedCaches, getNowMs: () => nowMs });

  // At 05:59:59 MYT: still before 06:00 MYT -> cache hit
  nowMs = Date.parse("2026-11-27T05:59:59+08:00");
  assert.equal((await dispatchFetch(sw2.handlers, routesUrl)).status, 200);
  assert.equal(fetchCount, 1);

  // At 06:00:00 MYT: expires cleanly and preserves persisted config entry during prune
  nowMs = Date.parse("2026-11-27T06:00:00+08:00");
  assert.equal((await dispatchFetch(sw2.handlers, routesUrl)).status, 200);
  assert.equal(fetchCount, 2);

  const staticCache = await sharedCaches.open("static-data-cache-v1");
  const configEntry = await staticCache.match("https://example.com/__static-data-cache-config");
  assert.ok(configEntry, "Persisted resetHour config entry must survive daily prune");
  assert.deepEqual(await configEntry.json(), { resetHour: 6 });
});

test("static-data-cache-v1 strips Content-Encoding/Content-Length and returns live 200 when Cache Storage fails", async () => {
  const routesUrl = "https://worker.example/rapid-rail-kl/data/routes";
  const payload = JSON.stringify({ data: [{ route_id: "KJ" }] });

  // Compressed response headers must not be kept on the decompressed cached copy.
  const swHeader = setupDevWorker(async () => new Response(payload, {
    status: 200,
    headers: {
      "Content-Type": "application/json",
      "Content-Encoding": "gzip",
      "Content-Length": "12"
    }
  }));
  const liveRes = await dispatchFetch(swHeader.handlers, routesUrl);
  assert.equal(liveRes.status, 200);
  const cachedRes = await (await swHeader.caches.open("static-data-cache-v1")).match(routesUrl);
  assert.ok(cachedRes);
  assert.equal(cachedRes.headers.get("Content-Encoding"), null);
  assert.equal(cachedRes.headers.get("Content-Length"), null);
  assert.equal(cachedRes.headers.get("Content-Type"), "application/json");
  assert.equal(await cachedRes.text(), payload);

  // QuotaExceededError on cache.put must still return the live 200 response.
  const baseCaches = createMockCaches();
  const quotaCaches = {
    async open(name) {
      const inner = await baseCaches.open(name);
      return {
        match: (req) => inner.match(req),
        keys: () => inner.keys(),
        delete: (req) => inner.delete(req),
        async put() {
          throw new DOMException("Quota exceeded", "QuotaExceededError");
        }
      };
    }
  };
  const swQuota = setupDevWorker(
    async () => new Response(payload, { status: 200, headers: { "Content-Type": "application/json" } }),
    { caches: quotaCaches }
  );
  const quotaRes = await dispatchFetch(swQuota.handlers, routesUrl);
  assert.equal(quotaRes.status, 200);
  assert.equal(await quotaRes.text(), payload);

  // caches.open failure must still fall through to network fetch.
  const brokenCaches = {
    async open() {
      throw new Error("Cache Storage unavailable");
    }
  };
  const swBroken = setupDevWorker(
    async () => new Response(payload, { status: 200, headers: { "Content-Type": "application/json" } }),
    { caches: brokenCaches }
  );
  const brokenRes = await dispatchFetch(swBroken.handlers, routesUrl);
  assert.equal(brokenRes.status, 200);
  assert.equal(await brokenRes.text(), payload);
});

test("prod worker routes uncached recreation_centres.json through static-data-cache-v1 with X-Cache-Day and index.html reuses CONFIG_MESSAGE_TYPE", async () => {
  let fetchCount = 0;
  const rcUrl = "https://example.com/data/recreation_centres.json";
  const { handlers, caches } = setupProdWorker(
    async () => {
      fetchCount++;
      return new Response("{\"stations\":[]}", {
        status: 200,
        headers: { "Content-Type": "application/json" }
      });
    },
    [] // intentionally omit recreation_centres.json from offline manifest
  );

  const first = await dispatchFetch(handlers, rcUrl);
  assert.equal(first.status, 200);
  assert.equal(fetchCount, 1);

  const staticCache = await caches.open("static-data-cache-v1");
  const cachedRc = await staticCache.match(rcUrl);
  assert.ok(cachedRc, "Uncached recreation_centres.json must be stored in static-data-cache-v1");
  assert.ok(cachedRc.headers.get("X-Cache-Day"), "Stored recreation_centres.json must be tagged with X-Cache-Day");

  const second = await dispatchFetch(handlers, rcUrl);
  assert.equal(second.status, 200);
  assert.equal(fetchCount, 1);

  // Verify index.html loads static-data-cache.js and reuses CONFIG_MESSAGE_TYPE without hardcoding it
  assert.match(indexHtmlSource, /<script src="js\/static-data-cache\.js" defer><\/script>/);
  assert.doesNotMatch(indexHtmlSource, /SET_STATIC_DATA_RESET_HOUR/);

  const swMatch = indexHtmlSource.match(/<script>\s*\(function \(\) \{\s*var pendingStaticDataResetHour[\s\S]*?<\/script>/);
  assert.ok(swMatch, "Expected syncStaticDataResetHour inline script in index.html");
  const swScript = swMatch[0].replace(/^<script>|<\/script>$/g, "");

  const posted = [];
  const controller = { postMessage: (msg) => posted.push(msg) };
  const windowObj = { addEventListener: () => {} };
  const context = createContext({
    window: windowObj,
    self: windowObj,
    navigator: {
      serviceWorker: {
        controller,
        addEventListener: () => {},
        ready: Promise.resolve({ active: controller })
      }
    }
  });
  runInContext(staticDataCacheSource, context);
  windowObj.staticDataCache = context.staticDataCache;
  runInContext(swScript, context);

  windowObj.syncStaticDataResetHour(6);
  assert.equal(posted.length, 1);
  assert.deepEqual({ ...posted[0] }, { type: "SET_STATIC_DATA_RESET_HOUR", resetHour: 6 });
});

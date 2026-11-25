// Caution! Be sure you understand the caveats before publishing an application with
// offline support. See https://aka.ms/blazor-offline-considerations

self.importScripts('./service-worker-assets.js', './js/wasm-gzip.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const staticDataCacheName = 'static-data-cache-v1';
const offlineAssetsInclude = [ /\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/, /\.webmanifest$/, /\.svg$/ ];
const offlineAssetsExclude = [ /^service-worker\.js$/, /^firebase-messaging-sw\.js$/ ];

// Base path resolved from registration scope to automatically support root and subfolder deployments (e.g. GitHub Pages)
const base = (self.registration && self.registration.scope)
  ? new URL(self.registration.scope).pathname
  : "/";
const baseUrl = new URL(base, self.origin);
const manifestUrlList = self.assetsManifest.assets.map(asset => new URL(asset.url, baseUrl).href);

function isGtfsStaticRequest(url) {
  const selfOrigin = self.origin || (self.location && self.location.origin);
  const isSelfMapRoute = (selfOrigin && url.origin === selfOrigin) && (url.pathname === '/map' || url.pathname === '/map/');
  return !isSelfMapRoute && (
    (url.hostname === 'api.data.gov.my' && url.pathname.includes('/gtfs-static/'))
    || url.pathname.includes('/data/routes')
    || url.pathname.includes('/data/frequencies')
    || url.pathname.includes('/departures')
    || (url.pathname.endsWith('/map') || url.pathname.includes('/map?') || url.pathname.includes('/map/'))
  );
}

function createAssetRequest(asset) {
  return new Request(asset.url, {
    integrity: asset.hash,
    cache: asset.url.startsWith('_framework/') && !/^_framework\/(blazor\.webassembly|dotnet)\.js$/.test(asset.url)
      ? 'force-cache'
      : 'no-cache'
  });
}

const maxConcurrentWasmCaches = 6;

async function cacheCompressedWasm(cache, asset) {
  try {
    const wasmGzip = globalThis.wasmGzip || self.wasmGzip;
    if (!wasmGzip) {
      return false;
    }
    const decompressed = await wasmGzip.fetchDecompressedWasm(
      `${asset.url}.gz`,
      asset.hash,
      { cache: 'force-cache' }
    );
    if (!decompressed) {
      return false;
    }
    await cache.put(new Request(asset.url), decompressed);
    return true;
  } catch {
    return false;
  }
}

async function onInstall(event) {
  // Pre-cache published assets from manifest (including data/recreation_centres.json, i18n, etc.)
  const matchingAssets = self.assetsManifest.assets
    .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
    .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)));

  const cache = await caches.open(cacheName);
  const canDecompressGzip = typeof DecompressionStream === 'function';
  const wasmAssets = canDecompressGzip
    ? matchingAssets.filter(asset => asset.url.startsWith('_framework/') && asset.url.endsWith('.wasm'))
    : [];
  const standardAssets = canDecompressGzip
    ? matchingAssets.filter(asset => !(asset.url.startsWith('_framework/') && asset.url.endsWith('.wasm')))
    : matchingAssets;

  const fallbackAssets = [];
  if (wasmAssets.length > 0) {
    let nextIndex = 0;
    const workerCount = Math.min(maxConcurrentWasmCaches, wasmAssets.length);
    await Promise.all(Array.from({ length: workerCount }, async () => {
      while (nextIndex < wasmAssets.length) {
        const asset = wasmAssets[nextIndex++];
        if (!(await cacheCompressedWasm(cache, asset))) {
          fallbackAssets.push(asset);
        }
      }
    }));
  }

  const assetsRequests = standardAssets.concat(fallbackAssets).map(createAssetRequest);
  if (assetsRequests.length > 0) {
    await cache.addAll(assetsRequests);
  }
}

async function onActivate(event) {
  // Delete unused old offline caches
  const cacheKeys = await caches.keys();
  await Promise.all(cacheKeys
    .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
    .map(key => caches.delete(key)));
  await self.clients.claim();
}

async function onFetch(event) {
  if (event.request.method !== 'GET') {
    return fetch(event.request);
  }

  const url = new URL(event.request.url);

  // GTFS static routes, departures, and shapes
  if (event.request.mode !== 'navigate' && isGtfsStaticRequest(url)) {
    const dataCache = await caches.open(staticDataCacheName);
    const cachedResponse = await dataCache.match(event.request);

    // Stale-while-revalidate for GTFS static data to enable offline lookups and instant rendering
    const networkFetch = fetch(event.request).then(async response => {
      if (response && response.status === 200) {
        await dataCache.put(event.request, response.clone());
      }
      return response;
    }).catch(() => cachedResponse || new Response(null, { status: 503, statusText: 'Service Unavailable' }));

    if (cachedResponse) {
      if (event.waitUntil) {
        event.waitUntil(networkFetch);
      }
      return cachedResponse;
    }

    return networkFetch;
  }

  // Navigation requests: serve index.html from cache
  const shouldServeIndexHtml = event.request.mode === 'navigate'
    && !manifestUrlList.some(u => u === event.request.url);

  const request = shouldServeIndexHtml ? new URL('index.html', baseUrl).href : event.request;
  const cache = await caches.open(cacheName);
  const cachedResponse = await cache.match(request);

  if (cachedResponse) {
    return cachedResponse;
  }

  // Check static data cache if not present in main cache
  const dataCache = await caches.open(staticDataCacheName);
  const dataCached = await dataCache.match(event.request);
  if (dataCached) {
    return dataCached;
  }

  try {
    const networkResponse = await fetch(event.request);
    if (networkResponse && networkResponse.status === 200 && url.pathname.includes('recreation_centres.json')) {
      await dataCache.put(event.request, networkResponse.clone());
    }
    return networkResponse;
  } catch (error) {
    const fallback = await cache.match(request) || await dataCache.match(event.request);
    if (fallback) return fallback;
    return new Response(null, { status: 503, statusText: 'Service Unavailable' });
  }
}

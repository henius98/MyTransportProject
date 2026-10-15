/* Manifest version: J+yWxW6W */
// Caution! Be sure you understand the caveats before publishing an application with
// offline support. See https://aka.ms/blazor-offline-considerations

self.importScripts('./service-worker-assets.js');
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
  return (url.hostname === 'api.data.gov.my' && url.pathname.includes('/gtfs-static/'))
    || url.pathname.includes('/data/routes')
    || url.pathname.includes('/departures')
    || (url.pathname.endsWith('/map') || url.pathname.includes('/map?') || url.pathname.includes('/map/'));
}

async function onInstall(event) {
  // Pre-cache published assets from manifest (including data/recreation_centres.json, i18n, etc.)
  const assetsRequests = self.assetsManifest.assets
    .filter(asset => offlineAssetsInclude.some(pattern => pattern.test(asset.url)))
    .filter(asset => !offlineAssetsExclude.some(pattern => pattern.test(asset.url)))
    .map(asset => new Request(asset.url, { integrity: asset.hash, cache: 'no-cache' }));
  await caches.open(cacheName).then(cache => cache.addAll(assetsRequests));
}

async function onActivate(event) {
  // Delete unused old offline caches
  const cacheKeys = await caches.keys();
  await Promise.all(cacheKeys
    .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
    .map(key => caches.delete(key)));
}

async function onFetch(event) {
  if (event.request.method !== 'GET') {
    return fetch(event.request);
  }

  const url = new URL(event.request.url);

  // GTFS static routes, departures, and shapes
  if (isGtfsStaticRequest(url)) {
    const dataCache = await caches.open(staticDataCacheName);
    const cachedResponse = await dataCache.match(event.request);

    // Stale-while-revalidate for GTFS static data to enable offline lookups and instant rendering
    const networkFetch = fetch(event.request).then(response => {
      if (response && response.status === 200) {
        dataCache.put(event.request, response.clone());
      }
      return response;
    }).catch(err => {
      if (cachedResponse) return cachedResponse;
      throw err;
    });

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

  const request = shouldServeIndexHtml ? 'index.html' : event.request;
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
      dataCache.put(event.request, networkResponse.clone());
    }
    return networkResponse;
  } catch (error) {
    const fallback = await cache.match(request) || await dataCache.match(event.request);
    if (fallback) return fallback;
    throw error;
  }
}

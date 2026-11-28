// In development, cache static JSON assets (GTFS static schedules & MetMalaysia station catalog)
// while allowing framework and application assemblies to fetch fresh from network without breaking hot reload.

self.importScripts('./js/static-data-cache.js');

self.addEventListener('install', event => {
  self.skipWaiting();
});

self.addEventListener('activate', event => {
  event.waitUntil(self.clients.claim());
});

self.addEventListener('message', event => {
  self.staticDataCache.handleMessage(event);
});

self.addEventListener('fetch', event => {
  if (event.request.method !== 'GET' || event.request.mode === 'navigate') return;

  const url = new URL(event.request.url);

  // GTFS static routes and shapes from api.data.gov.my or configured static endpoints
  const isGtfsStatic = self.staticDataCache.isGtfsStaticRequest(url);
  // MetMalaysia recreation centres station catalog
  const isStationCatalog = self.staticDataCache.isStationCatalogRequest(url);

  if (isGtfsStatic || isStationCatalog) {
    event.respondWith(self.staticDataCache.handleStaticDataRequest(event.request));
  }
});

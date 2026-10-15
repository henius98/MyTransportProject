// In development, cache static JSON assets (GTFS static schedules & MetMalaysia station catalog)
// while allowing framework and application assemblies to fetch fresh from network without breaking hot reload.

const staticDataCacheName = 'static-data-cache-v1';

self.addEventListener('install', event => {
  self.skipWaiting();
});

self.addEventListener('activate', event => {
  event.waitUntil(self.clients.claim());
});

self.addEventListener('fetch', event => {
  if (event.request.method !== 'GET') return;

  const url = new URL(event.request.url);

  // GTFS static routes, departures, and shapes from api.data.gov.my or configured static endpoints
  const isGtfsStatic = (url.hostname === 'api.data.gov.my' && url.pathname.includes('/gtfs-static/'))
    || url.pathname.includes('/data/routes')
    || url.pathname.includes('/departures')
    || (url.pathname.endsWith('/map') || url.pathname.includes('/map?') || url.pathname.includes('/map/'));
  // MetMalaysia recreation centres station catalog
  const isStationCatalog = url.pathname.includes('recreation_centres.json');

  if (isGtfsStatic || isStationCatalog) {
    event.respondWith((async () => {
      const cache = await caches.open(staticDataCacheName);
      const cachedResponse = await cache.match(event.request);

      const fetchPromise = fetch(event.request)
        .then(response => {
          if (response && response.status === 200) {
            cache.put(event.request, response.clone());
          }
          return response;
        })
        .catch(err => {
          if (cachedResponse) return cachedResponse;
          throw err;
        });

      if (cachedResponse) {
        if (event.waitUntil) {
          event.waitUntil(fetchPromise);
        }
        return cachedResponse;
      }

      return await fetchPromise;
    })());
  }
});

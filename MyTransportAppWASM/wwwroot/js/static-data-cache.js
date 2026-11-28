(function (root) {
  var STATIC_DATA_CACHE_NAME = "static-data-cache-v1";
  var CACHE_DAY_HEADER = "X-Cache-Day";
  var CONFIG_MESSAGE_TYPE = "SET_STATIC_DATA_RESET_HOUR";
  var CONFIG_CACHE_PATH = "/__static-data-cache-config";
  var DEFAULT_RESET_HOUR = 4;
  var MALAYSIA_OFFSET_MS = 8 * 60 * 60 * 1000;
  var HOUR_MS = 60 * 60 * 1000;

  var cachedResetHour = null;
  var lastPrunedServiceDay = null;

  function clampResetHour(value) {
    var parsed = typeof value === "number" ? value : Number(value);
    if (!Number.isFinite(parsed)) {
      return DEFAULT_RESET_HOUR;
    }
    var truncated = Math.trunc(parsed);
    if (truncated < 0) return 0;
    if (truncated > 23) return 23;
    return truncated;
  }

  function getSelfOrigin() {
    if (typeof self !== "undefined") {
      if (self.origin) return self.origin;
      if (self.location && self.location.origin) return self.location.origin;
    }
    return "";
  }

  function getOrigin() {
    return getSelfOrigin() || "https://static-cache.local";
  }

  function getConfigUrl() {
    return new URL(CONFIG_CACHE_PATH, getOrigin()).href;
  }

  function getMalaysiaServiceDay(nowMs, resetHour) {
    var hour = clampResetHour(resetHour);
    var shifted = new Date(nowMs + MALAYSIA_OFFSET_MS - hour * HOUR_MS);
    return shifted.toISOString().slice(0, 10);
  }

  function isGtfsStaticRequest(url) {
    var selfOrigin = getSelfOrigin();
    var isSelfMapRoute = Boolean(selfOrigin && url.origin === selfOrigin) &&
      (url.pathname === "/map" || url.pathname === "/map/");
    return !isSelfMapRoute && (
      (url.hostname === "api.data.gov.my" && url.pathname.includes("/gtfs-static/")) ||
      url.pathname.includes("/data/routes") ||
      url.pathname.includes("/data/frequencies") ||
      url.pathname.endsWith("/map") ||
      url.pathname.includes("/map?") ||
      url.pathname.includes("/map/")
    );
  }

  function isStationCatalogRequest(url) {
    return url.pathname.includes("recreation_centres.json");
  }

  function getCacheDay(response) {
    return response && response.headers && typeof response.headers.get === "function"
      ? response.headers.get(CACHE_DAY_HEADER)
      : null;
  }

  async function getResetHour(cache) {
    if (cachedResetHour !== null) {
      return cachedResetHour;
    }
    try {
      var configRes = await cache.match(getConfigUrl());
      if (configRes && typeof configRes.json === "function") {
        var readable = typeof configRes.clone === "function" ? configRes.clone() : configRes;
        var data = await readable.json();
        if (data && typeof data.resetHour !== "undefined") {
          cachedResetHour = clampResetHour(data.resetHour);
          return cachedResetHour;
        }
      }
    } catch (_) {
    }
    cachedResetHour = DEFAULT_RESET_HOUR;
    return cachedResetHour;
  }

  async function setResetHour(resetHour) {
    var clamped = clampResetHour(resetHour);
    if (cachedResetHour !== clamped) {
      lastPrunedServiceDay = null;
    }
    cachedResetHour = clamped;
    try {
      var cache = await caches.open(STATIC_DATA_CACHE_NAME);
      var configResponse = new Response(JSON.stringify({ resetHour: clamped }), {
        status: 200,
        headers: { "Content-Type": "application/json" }
      });
      await cache.put(getConfigUrl(), configResponse);
    } catch (_) {
    }
    return clamped;
  }

  function handleMessage(event) {
    if (!event || !event.data || event.data.type !== CONFIG_MESSAGE_TYPE) {
      return null;
    }
    var promise = setResetHour(event.data.resetHour);
    if (typeof event.waitUntil === "function") {
      event.waitUntil(promise);
    }
    return promise;
  }

  async function pruneExpiredEntries(cache, currentServiceDay) {
    if (lastPrunedServiceDay === currentServiceDay) {
      return;
    }
    if (typeof cache.keys !== "function" || typeof cache.delete !== "function") {
      lastPrunedServiceDay = currentServiceDay;
      return;
    }
    var configUrl = getConfigUrl();
    var keys = await cache.keys();
    for (var i = 0; i < keys.length; i++) {
      var req = keys[i];
      var reqUrl = typeof req === "string" ? req : req.url;
      if (reqUrl === configUrl) {
        continue;
      }
      var entry = await cache.match(req);
      if (getCacheDay(entry) !== currentServiceDay) {
        await cache.delete(req);
      }
    }
    lastPrunedServiceDay = currentServiceDay;
  }

  async function tagResponseWithServiceDay(response, serviceDay) {
    var headers = new Headers(response.headers || undefined);
    headers.delete("Content-Encoding");
    headers.delete("Content-Length");
    headers.set(CACHE_DAY_HEADER, serviceDay);
    var body = null;
    if (typeof response.arrayBuffer === "function") {
      var cloned = typeof response.clone === "function" ? response.clone() : response;
      body = await cloned.arrayBuffer();
    }
    return new Response(body, {
      status: response.status,
      statusText: response.statusText || "",
      headers: headers
    });
  }

  async function handleStaticDataRequest(request) {
    var cache = null;
    var currentServiceDay = null;
    try {
      cache = await caches.open(STATIC_DATA_CACHE_NAME);
      var resetHour = await getResetHour(cache);
      currentServiceDay = getMalaysiaServiceDay(Date.now(), resetHour);

      var cachedResponse = await cache.match(request);
      if (cachedResponse && getCacheDay(cachedResponse) === currentServiceDay) {
        return cachedResponse;
      }

      await pruneExpiredEntries(cache, currentServiceDay);
    } catch (_) {
      cache = null;
    }

    var networkResponse;
    try {
      networkResponse = await fetch(request);
    } catch (_) {
      return new Response(null, { status: 503, statusText: "Service Unavailable" });
    }

    if (cache && currentServiceDay && networkResponse && networkResponse.status === 200) {
      try {
        var tagged = await tagResponseWithServiceDay(networkResponse, currentServiceDay);
        await cache.put(request, tagged);
      } catch (_) {
      }
    }

    return networkResponse;
  }

  var api = {
    CACHE_NAME: STATIC_DATA_CACHE_NAME,
    CACHE_DAY_HEADER: CACHE_DAY_HEADER,
    CONFIG_MESSAGE_TYPE: CONFIG_MESSAGE_TYPE,
    CONFIG_CACHE_PATH: CONFIG_CACHE_PATH,
    DEFAULT_RESET_HOUR: DEFAULT_RESET_HOUR,
    clampResetHour: clampResetHour,
    getMalaysiaServiceDay: getMalaysiaServiceDay,
    isGtfsStaticRequest: isGtfsStaticRequest,
    isStationCatalogRequest: isStationCatalogRequest,
    getResetHour: getResetHour,
    setResetHour: setResetHour,
    handleMessage: handleMessage,
    handleStaticDataRequest: handleStaticDataRequest
  };

  root.staticDataCache = api;
  if (typeof self !== "undefined" && self !== root) {
    self.staticDataCache = api;
  }
})(typeof globalThis !== "undefined" ? globalThis : self);

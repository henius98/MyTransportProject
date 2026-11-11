/**
 * @typedef {Object} GetUserLocationOptions
 * @property {boolean} [enableHighAccuracy=true] Request high-accuracy if available.
 * @property {number}  [timeout=8000]            Max time (ms) for initial getCurrentPosition.
 * @property {number}  [maximumAge]              Allow cached position age (ms).
 * @property {boolean} [useCache=true]           Use localStorage cache when valid.
 * @property {number}  [cacheMaxAge=0]           Max cache age (ms) before ignoring.
 */

let permissionDeniedNotified = false;
const cacheKey = "user_location_cache_v1";

/**
 * Synchronously reads the last cached location from localStorage regardless of age.
 * Used for instant initial map centering while a fresh position fix resolves.
 *
 * @returns {{ latitude:number; longitude:number; accuracy:number|null; timestamp:number; source:"cache" } | null}
 */
export function getCachedLocation() {
  if (typeof localStorage === "undefined") {
    return null;
  }

  try {
    const raw = localStorage.getItem(cacheKey);
    if (!raw) {
      return null;
    }

    const cached = JSON.parse(raw);
    if (
      cached &&
      typeof cached.latitude === "number" &&
      Number.isFinite(cached.latitude) &&
      typeof cached.longitude === "number" &&
      Number.isFinite(cached.longitude)
    ) {
      return {
        latitude: cached.latitude,
        longitude: cached.longitude,
        accuracy: typeof cached.accuracy === "number" ? cached.accuracy : null,
        timestamp: typeof cached.timestamp === "number" ? cached.timestamp : 0,
        source: "cache"
      };
    }
  } catch (cacheErr) {
    // ignore cache errors, continue with live lookup
    console.debug("Location cache read failed", cacheErr);
  }

  return null;
}

/**
 * Get user location with sane defaults, fast network fallback and optional caching.
 * Throws on hard errors (e.g. browser unsupported).
 *
 * @param {GetUserLocationOptions} [options]
 * @returns {Promise<{ latitude:number; longitude:number; accuracy:number; source:"fresh"|"cache" } | null>}
 */
export async function getUserLocation(options = {}) {
  const {
    enableHighAccuracy = true,
    timeout = 8000,
    useCache = true,
    cacheMaxAge = 0
  } = options;
  const maximumAge =
    typeof options.maximumAge === "number"
      ? options.maximumAge
      : useCache
        ? Math.max(cacheMaxAge, 60000)
        : 0;

  if (!("geolocation" in navigator)) {
    throw new Error("Geolocation not supported by this browser");
  }

  const geo = navigator.geolocation;
  // 1) Fast path: cached location (optional)
  if (useCache) {
    const cached = getCachedLocation();
    if (cached) {
      const age = Date.now() - cached.timestamp;
      if (age >= 0 && age <= cacheMaxAge) {
        return {
          latitude: cached.latitude,
          longitude: cached.longitude,
          accuracy: cached.accuracy,
          source: "cache"
        };
      }
    }
  }
  
  const settings = { enableHighAccuracy, timeout, maximumAge };

  function requestPosition(posSettings) {
    return new Promise(function (resolve, reject) {
      geo.getCurrentPosition(
        function (pos) {
          resolve(pos.coords);
        },
        function (err) {
          reject(err);
        },
        posSettings
      );
    });
  }

  async function getPositionOnce() {
    try {
      return await requestPosition(settings);
    } catch (err) {
      // Fall back to fast network/Wi-Fi location when high-accuracy GPS times out or is unavailable.
      if (
        enableHighAccuracy &&
        err &&
        typeof err === "object" &&
        (err.code === err.POSITION_UNAVAILABLE || err.code === err.TIMEOUT || err.code === 2 || err.code === 3)
      ) {
        return await requestPosition({
          enableHighAccuracy: false,
          timeout: Math.min(timeout, 4000),
          maximumAge: Math.max(maximumAge, 300000)
        });
      }
      throw err;
    }
  }

  try {
    const coords = await getPositionOnce();

    const result = {
      latitude: coords.latitude,
      longitude: coords.longitude,
      accuracy: coords.accuracy,
      source: "fresh"
    };

    // Persist to cache if enabled
    if (useCache && typeof localStorage !== "undefined") {
      try {
        localStorage.setItem(
          cacheKey,
          JSON.stringify({
            latitude: result.latitude,
            longitude: result.longitude,
            accuracy: result.accuracy,
            timestamp: Date.now()
          })
        );
      } catch (cacheErr) {
        // ignore cache failures
        console.debug("Location cache write failed", cacheErr);
      }
    }

    return result;
  } catch (err) {
    // Normalize errors and avoid noisy retries on permission denial.
    if (err && typeof err === "object" && "code" in err) {
      var e = err;
      switch (e.code) {
        case e.PERMISSION_DENIED:
          if (!permissionDeniedNotified) {
            console.warn("Geolocation denied by browser/OS. Using fallback location.");
            permissionDeniedNotified = true;
          }
          return null;
        case e.POSITION_UNAVAILABLE:
          console.warn("Geolocation position unavailable; using fallback.");
          return null;
        case e.TIMEOUT:
          console.warn("Geolocation request timed out; using last known or fallback.");
          return null;
        default:
          console.warn("Unknown geolocation error", err);
          return null;
      }
    }

    console.error("Failed to get user location:", err);
    return null;
  }
}

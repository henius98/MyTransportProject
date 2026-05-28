/**
 * @typedef {Object} GetUserLocationOptions
 * @property {boolean} [enableHighAccuracy=true] Request high-accuracy if available.
 * @property {number}  [timeout=15000]           Max time (ms) for initial getCurrentPosition.
 * @property {number}  [maximumAge=0]            Allow cached position age (ms).
 * @property {number}  [minAccuracy=100]         Desired accuracy in meters before we stop retrying.
 * @property {number}  [watchTimeout=5000]       Max extra time (ms) we wait with watchPosition.
 * @property {boolean} [useCache=true]           Use localStorage cache when valid.
 * @property {number}  [cacheMaxAge=60000]       Max cache age (ms) before ignoring.
 */

let permissionDeniedNotified = false;

/**
 * Get user location with sane defaults, retry + watch fallback and optional caching.
 * Throws on hard errors (e.g. permission denied).
 *
 * @param {GetUserLocationOptions} [options]
 * @returns {Promise<{ latitude:number; longitude:number; accuracy:number; source:"fresh"|"watch"|"cache" } | null>}
 */
export async function getUserLocation(options = {}) {
  const {
    enableHighAccuracy = true,
    timeout = 15000,
    maximumAge = 0,
    minAccuracy = 100,
    watchTimeout = 5000,
    useCache = true,
    cacheMaxAge = 60000
  } = options;

  if (!("geolocation" in navigator)) {
    throw new Error("Geolocation not supported by this browser");
  }

  const geo = navigator.geolocation;
  const cacheKey = "user_location_cache_v1";
  // 1) Fast path: cached location (optional)
  if (useCache && typeof localStorage !== "undefined") {
    try {
      const raw = localStorage.getItem(cacheKey);
      if (raw) {
        const cached = JSON.parse(raw);
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
    } catch (cacheErr) {
      // ignore cache errors, continue with live lookup
      console.warn("Location cache read failed", cacheErr);
    }
  }
  
  const settings = { enableHighAccuracy, timeout, maximumAge };

  function getPositionOnce() {
    return new Promise(function (resolve, reject) {
      geo.getCurrentPosition(
        function (pos) {
          resolve(pos.coords);
        },
        function (err) {
          reject(err);
        },
        settings
      );
    });
  }

  async function getWithWatchFallback() {
    // First shot
    const initial = await getPositionOnce();
    if (initial.accuracy <= minAccuracy) {
      return { coords: initial, source: "fresh" };
    }

    // Try to refine via watchPosition for a short window.
    return new Promise(function (resolve, reject) {
      let settled = false;
      let watchId = null;

      function finish(value, fromWatch) {
        if (settled) return;
        settled = true;
        if (watchId !== null) {
          geo.clearWatch(watchId);
        }
        resolve({
          coords: value,
          source: fromWatch ? "watch" : "fresh"
        });
      }

      function handleError(err) {
        if (settled) return;
        settled = true;
        if (watchId !== null) {
          geo.clearWatch(watchId);
        }
        reject(err);
      }

      watchId = geo.watchPosition(
        function (p) {
          const c = p.coords;
          if (c.accuracy <= minAccuracy) {
            finish(c, true);
          }
        },
        handleError,
        settings
      );

      // If no better fix within watchTimeout, fall back to the initial reading.
      setTimeout(function () {
        if (!settled) {
          finish(initial, false);
        }
      }, watchTimeout);
    });
  }

  try {
    const resultWithSource = await getWithWatchFallback();
    const coords = resultWithSource.coords;
    const source = resultWithSource.source;

    const result = {
      latitude: coords.latitude,
      longitude: coords.longitude,
      accuracy: coords.accuracy,
      source: source
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
        console.warn("Location cache write failed", cacheErr);
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

import { getAuthContext } from "./firebase-auth.js";
import { clearConnections, getConnection, setConnection } from "./google-token-store.js";
import { authorizeConnection, restoreConnection, invalidateConnection, prepareAuthorization } from "./google-authorization.js";

const services = {
    calendar: {
        name: "Google Calendar",
        base: "https://www.googleapis.com/calendar/v3/"
    },
    tasks: {
        name: "Google Tasks",
        base: "https://tasks.googleapis.com/tasks/v1/"
    }
};

// Google access tokens are cached in this browser; the backend restores and renews them from Firestore.
let observingAuth = false;
let currentUid = null;
let sessionVersion = 0;
let connecting = false;
const restoring = new Map();

function serviceDetails(service) {
    if (!Object.hasOwn(services, service)) throw new Error("This Google service is not supported.");
    return services[service];
}

function accountChanged(user) {
    const uid = user?.uid ?? null;
    if (uid !== currentUid) {
        currentUid = uid;
        sessionVersion++;
        clearConnections();
    }
}

async function contextFor(uid) {
    const context = await getAuthContext();
    await context.auth.authStateReady();
    if (!observingAuth) {
        currentUid = context.auth.currentUser?.uid ?? null;
        context.sdk.onAuthStateChanged(context.auth, accountChanged);
        observingAuth = true;
    }
    requireAccount(context, uid);
    return context;
}

function requireAccount(context, uid, version = sessionVersion) {
    accountChanged(context.auth.currentUser);
    if (!uid || currentUid !== uid || version !== sessionVersion) {
        throw new Error("The signed-in account has changed. Please reload this page and try again.");
    }
}

function activeConnection(uid, service) {
    const connection = getConnection(service);
    if (connection && (connection.uid !== uid || typeof connection.accessToken !== "string" || !connection.accessToken ||
        !Number.isFinite(connection.expiresAt) || connection.expiresAt <= Date.now() + 60000)) {
        setConnection(service, null);
        return null;
    }
    return connection;
}

async function connectionFor(context, uid, service, rejectedAccessToken = null) {
    const cached = activeConnection(uid, service);
    if (cached && cached.accessToken !== rejectedAccessToken) return cached;
    const version = sessionVersion;
    const key = JSON.stringify([uid, service, version, rejectedAccessToken]);
    if (!restoring.has(key)) {
        const pending = (async () => {
            const connection = await restoreConnection(uid, service, rejectedAccessToken);
            requireAccount(context, uid, version);
            const newer = activeConnection(uid, service);
            if (newer && newer.accessToken !== rejectedAccessToken) return newer;
            setConnection(service, connection ? { uid, accessToken: connection.accessToken, expiresAt: connection.expiresAt } : null);
            return activeConnection(uid, service);
        })();
        restoring.set(key, pending);
        void pending.finally(() => restoring.delete(key)).catch(() => {});
    }
    return restoring.get(key);
}

export async function isConnected(uid, service) {
    serviceDetails(service);
    const context = await contextFor(uid);
    requireAccount(context, uid);
    const version = sessionVersion;
    const connected = Boolean(await connectionFor(context, uid, service));
    // Prepare Google's popup before the Connect button is shown, preserving the click's user activation.
    await prepareAuthorization(uid, service).catch(() => {});
    requireAccount(context, uid, version);
    return connected;
}

export async function connect(uid, service) {
    const details = serviceDetails(service);
    const context = await contextFor(uid);
    requireAccount(context, uid);
    const { auth } = context;
    if (connecting) throw new Error("Finish the current Google permission window before connecting another service.");
    if (!auth.currentUser.providerData.some(provider => provider.providerId === "google.com")) {
        throw new Error("Sign in with a Google account to connect this service.");
    }

    const version = sessionVersion;
    connecting = true;
    try {
        // The backend verifies that permission consent belongs to the current Firebase account.
        const connection = await authorizeConnection(uid, service, auth.currentUser.email ?? "", () => requireAccount(context, uid, version));
        requireAccount(context, uid, version);
        if (!connection?.accessToken) throw new Error(`Google did not grant access to ${details.name}. Connect again and allow the requested permissions.`);

        // The backend uses Google's actual token expiry and retains the refresh token in Firestore;
        // an earlier revocation or expiry is also handled by the API's 401 response.
        setConnection(service, { uid, accessToken: connection.accessToken, expiresAt: connection.expiresAt });
    } finally {
        connecting = false;
    }
}

function apiError(service, status, payload) {
    const name = serviceDetails(service).name;
    const reasons = [payload?.error?.status, ...(payload?.error?.errors ?? []).map(item => item.reason),
        ...(payload?.error?.details ?? []).map(item => item.reason)].filter(Boolean).join(" ");
    if (status === 401) return new Error(`Your ${name} connection expired or was revoked. Connect again to continue.`);
    if (/accessNotConfigured|SERVICE_DISABLED/.test(reasons)) {
        return new Error(`The app owner must enable the ${name} API in the Firebase project's Google Cloud console, then try again.`);
    }
    if (status === 429 || /rateLimitExceeded|userRateLimitExceeded|quotaExceeded|RESOURCE_EXHAUSTED/.test(reasons)) {
        return new Error(`${name} is receiving too many requests. Wait a moment and try again.`);
    }
    if (status === 403) return new Error(`${name} denied access. Connect again and allow the requested permissions. If this item is read-only or your Workspace administrator restricts access, open it in Google instead.`);
    if (status === 404 || status === 410) return new Error(`This ${name} item is no longer available. Refresh the list and try again.`);
    if (status === 409 || status === 412) return new Error("This item changed in Google after you opened it. Refresh the list before editing it again.");
    if (status === 400) return new Error(`${name} could not accept these details. Check the dates and required fields, then try again.`);
    return new Error(`${name} is unavailable right now. Please try again later.`);
}

async function request(uid, service, path, method = "GET", body = null, etag = null, retry = true) {
    const details = serviceDetails(service);
    const context = await contextFor(uid);
    requireAccount(context, uid);
    const version = sessionVersion;
    const connection = await connectionFor(context, uid, service);
    requireAccount(context, uid, version);
    if (!connection) throw new Error(`Connect ${details.name} to continue. Permissions are requested separately from signing in.`);
    const headers = { Authorization: `Bearer ${connection.accessToken}`, Accept: "application/json" };
    if (body !== null) headers["Content-Type"] = "application/json";
    if (etag) headers["If-Match"] = etag;
    let response;
    try {
        response = await fetch(details.base + path, {
            method, headers, ...(body !== null ? { body: JSON.stringify(body) } : {}), cache: "no-store", credentials: "omit"
        });
    } catch {
        requireAccount(context, uid, version);
        throw new Error(`${details.name} could not be reached. Check your connection and try again.`);
    }
    requireAccount(context, uid, version);
    if (response.status === 401 && retry) {
        const renewed = await connectionFor(context, uid, service, connection.accessToken);
        requireAccount(context, uid, version);
        if (renewed) return request(uid, service, path, method, body, etag, false);
    }
    let payload = null;
    if (response.status !== 204) {
        try { payload = await response.json(); } catch { /* Non-JSON errors are mapped using the HTTP status. */ }
    }
    requireAccount(context, uid, version);
    if ((response.status === 401 || (response.status === 403 && /insufficientPermissions|ACCESS_TOKEN_SCOPE_INSUFFICIENT/.test(JSON.stringify(payload)))) && getConnection(service)?.accessToken === connection.accessToken) {
        setConnection(service, null);
        await invalidateConnection(uid, service, connection.accessToken);
        requireAccount(context, uid, version);
    }
    if (!response.ok) throw apiError(service, response.status, payload);
    if (response.status !== 204 && payload === null) throw new Error(`${details.name} returned an unreadable response. Refresh the list and try again.`);
    return payload;
}

async function listAll(uid, service, path, parameters = {}) {
    const context = await contextFor(uid);
    const version = sessionVersion;
    const items = [];
    const query = new URLSearchParams(parameters);
    do {
        const page = await request(uid, service, `${path}?${query}`);
        requireAccount(context, uid, version);
        items.push(...(page.items ?? []));
        if (!page.nextPageToken) return items;
        query.set("pageToken", page.nextPageToken);
    } while (true);
}

function eventsPath(calendarId, eventId = null) {
    return `calendars/${encodeURIComponent(calendarId)}/events${eventId ? `/${encodeURIComponent(eventId)}` : ""}`;
}

function tasksPath(listId, taskId = null) {
    return `lists/${encodeURIComponent(listId)}/tasks${taskId ? `/${encodeURIComponent(taskId)}` : ""}`;
}

export function listCalendars(uid) {
    return listAll(uid, "calendar", "users/me/calendarList", { maxResults: "250" });
}

export function listEvents(uid, calendarId, startIso, endIso) {
    return listAll(uid, "calendar", eventsPath(calendarId), {
        timeMin: startIso, timeMax: endIso, singleEvents: "true", orderBy: "startTime", maxResults: "250"
    });
}

export function saveEvent(uid, calendarId, eventId, body, etag) {
    const event = { ...body };
    if (eventId) {
        // PATCH merges nested objects; switching between all-day and timed events must clear the old date type.
        for (const field of ["start", "end"]) {
            if (event[field]?.date) event[field] = { ...event[field], dateTime: null, timeZone: null };
            else if (event[field]?.dateTime) event[field] = { ...event[field], date: null };
        }
    }
    return request(uid, "calendar", eventsPath(calendarId, eventId), eventId ? "PATCH" : "POST", event, etag);
}

export function deleteEvent(uid, calendarId, eventId, etag) {
    return request(uid, "calendar", eventsPath(calendarId, eventId), "DELETE", null, etag);
}

export function listTaskLists(uid) {
    return listAll(uid, "tasks", "users/@me/lists", { maxResults: "100" });
}

export function listTasks(uid, listId) {
    return listAll(uid, "tasks", tasksPath(listId), { maxResults: "100", showCompleted: "true", showHidden: "true", showAssigned: "true" });
}

export function saveTask(uid, listId, taskId, body, etag) {
    return request(uid, "tasks", tasksPath(listId, taskId), taskId ? "PATCH" : "POST", body, etag);
}

export function deleteTask(uid, listId, taskId, etag) {
    return request(uid, "tasks", tasksPath(listId, taskId), "DELETE", null, etag);
}

export async function listTaskExtensions(uid) {
    const extensions = await import("./firebase-task-extensions.js");
    return extensions.listTaskExtensions(uid);
}

export async function saveTaskExtension(uid, extension) {
    const extensions = await import("./firebase-task-extensions.js");
    return extensions.saveTaskExtension(uid, extension);
}

export async function deleteTaskExtensions(uid, deleted) {
    const extensions = await import("./firebase-task-extensions.js");
    return extensions.deleteTaskExtensions(uid, deleted);
}

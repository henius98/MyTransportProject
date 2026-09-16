import { getAuthContext } from "./firebase-auth.js";

const services = {
    calendar: {
        name: "Google Calendar",
        base: "https://www.googleapis.com/calendar/v3/",
        scopes: ["https://www.googleapis.com/auth/calendar.calendarlist.readonly", "https://www.googleapis.com/auth/calendar.events"]
    },
    tasks: {
        name: "Google Tasks",
        base: "https://tasks.googleapis.com/tasks/v1/",
        scopes: ["https://www.googleapis.com/auth/tasks"]
    }
};

// Google access tokens stay in this module's memory; Firebase ID tokens cannot authorize these APIs.
const connections = new Map();
let observingAuth = false;
let currentUid = null;
let sessionVersion = 0;
let connecting = false;

function serviceDetails(service) {
    if (!Object.hasOwn(services, service)) throw new Error("This Google service is not supported.");
    return services[service];
}

function accountChanged(user) {
    const uid = user?.uid ?? null;
    if (uid !== currentUid) {
        currentUid = uid;
        sessionVersion++;
        connections.clear();
    }
}

async function contextFor(uid) {
    const context = await getAuthContext();
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
    const connection = connections.get(service);
    if (connection && (connection.uid !== uid || connection.expiresAt <= Date.now())) {
        connections.delete(service);
        return null;
    }
    return connection;
}

export async function isConnected(uid, service) {
    serviceDetails(service);
    const context = await contextFor(uid);
    requireAccount(context, uid);
    return Boolean(activeConnection(uid, service));
}

export async function connect(uid, service) {
    const details = serviceDetails(service);
    const context = await contextFor(uid);
    requireAccount(context, uid);
    const { auth, sdk } = context;
    if (connecting) throw new Error("Finish the current Google permission window before connecting another service.");
    if (!auth.currentUser.providerData.some(provider => provider.providerId === "google.com")) {
        throw new Error("Sign in with a Google account to connect this service.");
    }

    const version = sessionVersion;
    const provider = new sdk.GoogleAuthProvider();
    details.scopes.forEach(scope => provider.addScope(scope));
    provider.setCustomParameters({ prompt: "consent", login_hint: auth.currentUser.email ?? "", include_granted_scopes: "true" });
    connections.delete(service);
    connecting = true;
    try {
        // Reauthentication binds permission consent to the current Firebase account.
        const result = await sdk.reauthenticateWithPopup(auth.currentUser, provider);
        requireAccount(context, uid, version);
        if (result.user.uid !== uid) throw new Error("Choose the same Google account you used to sign in to this app.");
        const credential = sdk.GoogleAuthProvider.credentialFromResult(result);
        if (!credential?.accessToken) throw new Error(`Google did not grant access to ${details.name}. Connect again and allow the requested permissions.`);

        // The public Firebase credential does not expose Google token expiry. Reconnect conservatively;
        // an earlier revocation or expiry is also handled by the API's 401 response.
        connections.set(service, { uid, accessToken: credential.accessToken, expiresAt: Date.now() + 50 * 60 * 1000 });
    } catch (error) {
        const messages = {
            "auth/popup-blocked": "Allow pop-ups for this site, then connect again.",
            "auth/popup-closed-by-user": "The Google permission window was closed. Connect again when you are ready.",
            "auth/cancelled-popup-request": "The Google permission request was cancelled. Connect again.",
            "auth/user-mismatch": "Choose the same Google account you used to sign in to this app.",
            "auth/unauthorized-domain": "This site's domain must be added to Firebase Authentication's authorized domains by the app owner.",
            "auth/admin-restricted-operation": "Your Google Workspace administrator has blocked this connection. Ask your administrator to allow it.",
            "auth/operation-not-allowed": "Google sign-in must be enabled in Firebase Authentication by the app owner.",
            "auth/network-request-failed": "Google could not be reached. Check your connection and try again."
        };
        throw new Error(messages[error.code] ?? error.message ?? `Could not connect ${details.name}. Please try again.`);
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

async function request(uid, service, path, method = "GET", body = null, etag = null) {
    const details = serviceDetails(service);
    const context = await contextFor(uid);
    requireAccount(context, uid);
    const version = sessionVersion;
    const connection = activeConnection(uid, service);
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
    if (response.status === 401 && connections.get(service) === connection) connections.delete(service);
    let payload = null;
    if (response.status !== 204) {
        try { payload = await response.json(); } catch { /* Non-JSON errors are mapped using the HTTP status. */ }
    }
    requireAccount(context, uid, version);
    if (response.status === 403 && /insufficientPermissions|ACCESS_TOKEN_SCOPE_INSUFFICIENT/.test(JSON.stringify(payload)) && connections.get(service) === connection) {
        connections.delete(service);
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

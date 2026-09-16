import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, SourceTextModule, SyntheticModule } from "node:vm";

const source = await readFile(new URL("../../MyTransportAppWASM/wwwroot/js/google-services.js", import.meta.url), "utf8");
const user = (uid = "user-a") => ({ uid, email: `${uid}@example.com`, providerData: [{ providerId: "google.com" }] });
const response = (payload = {}, status = 200) => ({ ok: status >= 200 && status < 300, status, json: async () => payload });
const plain = value => JSON.parse(JSON.stringify(value));

async function setup() {
    const state = { auth: { currentUser: user() }, requests: [], popups: [], now: 1000 };
    const sdk = {
        GoogleAuthProvider: class {
            scopes = [];
            addScope(scope) { this.scopes.push(scope); }
            setCustomParameters(parameters) { this.parameters = parameters; }
            static credentialFromResult(result) { return result.credential; }
        },
        onAuthStateChanged(_auth, callback) { state.authChanged = callback; return () => {}; },
        async reauthenticateWithPopup(currentUser, provider) {
            state.popups.push({ user: currentUser, scopes: provider.scopes, parameters: provider.parameters });
            return state.reauthenticate ? state.reauthenticate() : { user: currentUser, credential: { accessToken: `google-token-${state.popups.length}` } };
        }
    };
    state.changeUser = value => { state.auth.currentUser = value; state.authChanged?.(value); };
    const context = createContext({
        URLSearchParams,
        Date: class extends Date { static now() { return state.now; } },
        fetch: async (url, options) => {
            state.requests.push({ url, ...options });
            return state.fetch ? state.fetch(url, options) : response({ items: [] });
        }
    });
    // No production dependency injection or token export is needed: only Firebase is mocked at the module boundary.
    const firebase = new SyntheticModule(["getAuthContext"], function () {
        this.setExport("getAuthContext", async () => ({ auth: state.auth, sdk }));
    }, { context });
    const module = new SourceTextModule(source, { context });
    await module.link(specifier => {
        assert.equal(specifier, "./firebase-auth.js");
        return firebase;
    });
    await module.evaluate();
    state.api = module.namespace;
    state.context = context;
    return state;
}

test("requires separate service consent, requests narrow scopes, and uses only the Google token", async () => {
    const state = await setup();
    await assert.rejects(state.api.listCalendars("user-a"), /Connect Google Calendar/);
    assert.equal(state.requests.length, 0);
    assert.equal(await state.api.isConnected("user-a", "calendar"), false);
    assert.equal(await state.api.connect("user-a", "calendar"), undefined);
    assert.equal(state.popups[0].user, state.auth.currentUser);
    assert.deepEqual(plain(state.popups[0].scopes), [
        "https://www.googleapis.com/auth/calendar.calendarlist.readonly", "https://www.googleapis.com/auth/calendar.events"
    ]);
    assert.equal(state.popups[0].parameters.login_hint, "user-a@example.com");
    await state.api.listCalendars("user-a");
    assert.equal(state.requests[0].headers.Authorization, "Bearer google-token-1");
    assert.equal(state.requests[0].credentials, "omit");
    assert.equal(state.requests[0].cache, "no-store");
    await assert.rejects(state.api.listTaskLists("user-a"), /Connect Google Tasks/);
    await state.api.connect("user-a", "tasks");
    assert.deepEqual(plain(state.popups[1].scopes), ["https://www.googleapis.com/auth/tasks"]);
    await state.api.listTaskLists("user-a");
    assert.equal(state.requests[1].headers.Authorization, "Bearer google-token-2");
});

test("tokens never touch browser storage and disappear on a fresh module load", async () => {
    const state = await setup();
    for (const name of ["localStorage", "sessionStorage", "indexedDB", "document"]) {
        Object.defineProperty(state.context, name, { get() { assert.fail(`Unexpected access to ${name}`); } });
    }
    await state.api.connect("user-a", "calendar");
    await state.api.listCalendars("user-a");
    assert.equal(await state.api.isConnected("user-a", "calendar"), true);
    const fresh = await setup();
    assert.equal(await fresh.api.isConnected("user-a", "calendar"), false);
});

test("expires memory credentials and invalidates revoked credentials on 401", async () => {
    const state = await setup();
    await state.api.connect("user-a", "calendar");
    state.now += 50 * 60 * 1000;
    assert.equal(await state.api.isConnected("user-a", "calendar"), false);
    await assert.rejects(state.api.listCalendars("user-a"), /Connect Google Calendar/);
    assert.equal(state.requests.length, 0);
    await state.api.connect("user-a", "calendar");
    state.fetch = () => response({}, 401);
    await assert.rejects(state.api.listCalendars("user-a"), /expired or was revoked/);
    assert.equal(await state.api.isConnected("user-a", "calendar"), false);
});

test("sign-out and account changes discard all credentials, including when the same account returns", async () => {
    const state = await setup();
    await state.api.connect("user-a", "calendar");
    await state.api.connect("user-a", "tasks");
    state.changeUser(null);
    await assert.rejects(state.api.listTaskLists("user-a"), /account has changed/);
    state.changeUser(user());
    assert.equal(await state.api.isConnected("user-a", "calendar"), false);
    assert.equal(await state.api.isConnected("user-a", "tasks"), false);
    await state.api.connect("user-a", "calendar");
    state.changeUser(user("user-b"));
    await assert.rejects(state.api.listCalendars("user-a"), /account has changed/);
    assert.equal(await state.api.isConnected("user-b", "calendar"), false);
    assert.equal(state.requests.length, 0);
});

test("rejects mismatched popup accounts and sign-out during consent", async () => {
    const state = await setup();
    state.reauthenticate = () => ({ user: user("user-b"), credential: { accessToken: "wrong-account" } });
    await assert.rejects(state.api.connect("user-a", "calendar"), /same Google account/);
    assert.equal(await state.api.isConnected("user-a", "calendar"), false);
    state.reauthenticate = () => {
        state.changeUser(null);
        state.changeUser(user());
        return { user: user(), credential: { accessToken: "stale-session" } };
    };
    await assert.rejects(state.api.connect("user-a", "calendar"), /account has changed/);
    assert.equal(await state.api.isConnected("user-a", "calendar"), false);
});

test("does not return private responses after an account change while fetching or parsing JSON", async () => {
    for (const changeDuringJson of [false, true]) {
        const state = await setup();
        await state.api.connect("user-a", "calendar");
        const change = () => { state.changeUser(null); state.changeUser(user()); };
        state.fetch = () => {
            if (!changeDuringJson) change();
            return { ok: true, status: 200, json: async () => {
                if (changeDuringJson) change();
                return { items: [{ summary: "Private event" }] };
            } };
        };
        await assert.rejects(state.api.listCalendars("user-a"), /account has changed/);
    }
});

test("paginates calendars, bounded event dates, task lists, and tasks including completed/assigned", async () => {
    for (const [method, service, args] of [
        ["listCalendars", "calendar", []],
        ["listEvents", "calendar", ["team/calendar@example.com", "2026-09-01T00:00:00+08:00", "2026-10-01T00:00:00+08:00"]],
        ["listTaskLists", "tasks", []],
        ["listTasks", "tasks", ["my/list"]]
    ]) {
        const state = await setup();
        await state.api.connect("user-a", service);
        state.fetch = () => state.requests.length === 1
            ? response({ items: [], nextPageToken: "page +&/2" })
            : response({ items: [{ id: "last", etag: '"version"' }] });
        const items = await state.api[method]("user-a", ...args);
        assert.deepEqual(plain(items), [{ id: "last", etag: '"version"' }]);
        assert.equal(state.requests.length, 2);
        assert.equal(new URL(state.requests[1].url).searchParams.get("pageToken"), "page +&/2");
        if (method === "listEvents") {
            const url = new URL(state.requests[1].url);
            assert.ok(url.pathname.includes("team%2Fcalendar%40example.com"));
            assert.equal(url.searchParams.get("timeMin"), args[1]);
            assert.equal(url.searchParams.get("timeMax"), args[2]);
            assert.equal(url.searchParams.get("singleEvents"), "true");
            assert.equal(url.searchParams.get("orderBy"), "startTime");
        }
        if (method === "listTasks") {
            const url = new URL(state.requests[0].url);
            assert.ok(url.pathname.includes("my%2Flist"));
            assert.equal(url.searchParams.get("showHidden"), "true");
            assert.equal(url.searchParams.get("showCompleted"), "true");
            assert.equal(url.searchParams.get("showAssigned"), "true");
        }
    }
});

test("patches only supplied fields and uses etags, preserving recurrence, attendees, and task metadata", async () => {
    const state = await setup();
    await state.api.connect("user-a", "calendar");
    const original = { id: "event/1", summary: "Original", recurrence: ["RRULE:FREQ=WEEKLY"], attendees: [{ email: "guest@example.com" }], reminders: { useDefault: true } };
    state.fetch = (_url, options) => response({ ...original, ...JSON.parse(options.body) });
    const updated = await state.api.saveEvent("user-a", "primary", original.id, { summary: "Edited" }, '"v1"');
    assert.equal(state.requests[0].method, "PATCH");
    assert.equal(state.requests[0].headers["If-Match"], '"v1"');
    assert.equal(state.requests[0].body, '{"summary":"Edited"}');
    assert.ok(state.requests[0].url.endsWith("/events/event%2F1"));
    assert.deepEqual(plain(updated.attendees), original.attendees);
    assert.deepEqual(plain(updated.recurrence), original.recurrence);
    await state.api.connect("user-a", "tasks");
    state.fetch = (_url, options) => response({ id: "task", parent: "parent", position: "001", notes: "Keep these notes", ...JSON.parse(options.body) });
    const completed = await state.api.saveTask("user-a", "list", "task", { status: "completed" }, '"t1"');
    assert.equal(state.requests[1].method, "PATCH");
    assert.equal(state.requests[1].headers["If-Match"], '"t1"');
    assert.equal(state.requests[1].body, '{"status":"completed"}');
    assert.equal(completed.notes, "Keep these notes");
    assert.equal(completed.parent, "parent");
});

test("creates with POST and deletes with DELETE/If-Match, accepting empty 204 responses", async () => {
    const state = await setup();
    for (const [service, save, remove] of [["calendar", "saveEvent", "deleteEvent"], ["tasks", "saveTask", "deleteTask"]]) {
        await state.api.connect("user-a", service);
        state.fetch = () => response({ id: "created" });
        await state.api[save]("user-a", "list", null, { title: "New" }, null);
        const create = state.requests.at(-1);
        assert.equal(create.method, "POST");
        assert.equal(create.headers["If-Match"], undefined);
        state.fetch = () => ({ ok: true, status: 204, json: () => assert.fail("Must not parse empty response") });
        assert.equal(await state.api[remove]("user-a", "list", "created", '"v1"'), null);
        const deleted = state.requests.at(-1);
        assert.equal(deleted.method, "DELETE");
        assert.equal(deleted.headers["If-Match"], '"v1"');
        assert.equal(deleted.body, undefined);
    }
});

test("switches between all-day and timed events by clearing the opposite nested date fields", async () => {
    const state = await setup();
    await state.api.connect("user-a", "calendar");
    state.fetch = () => response({ id: "event" });
    const dates = { start: { date: "2026-09-12" }, end: { date: "2026-09-13" } };
    await state.api.saveEvent("user-a", "primary", "event", dates, '"v1"');
    assert.deepEqual(JSON.parse(state.requests[0].body), {
        start: { date: "2026-09-12", dateTime: null, timeZone: null },
        end: { date: "2026-09-13", dateTime: null, timeZone: null }
    });
    assert.deepEqual(dates, { start: { date: "2026-09-12" }, end: { date: "2026-09-13" } });
    await state.api.saveEvent("user-a", "primary", "event", {
        start: { dateTime: "2026-09-12T09:00:00+08:00", timeZone: "Asia/Kuala_Lumpur" },
        end: { dateTime: "2026-09-12T10:00:00+08:00", timeZone: "Asia/Kuala_Lumpur" }
    }, '"v2"');
    const timed = JSON.parse(state.requests[1].body);
    assert.equal(timed.start.date, null);
    assert.equal(timed.end.date, null);
    assert.equal(timed.start.timeZone, "Asia/Kuala_Lumpur");
});

test("checks the active Firebase user even before its auth observer has delivered the change", async () => {
    const state = await setup();
    await state.api.connect("user-a", "tasks");
    state.auth.currentUser = user("user-b");
    await assert.rejects(state.api.saveTask("user-a", "list", "task", { title: "Change" }, '"v1"'), /account has changed/);
    assert.equal(state.requests.length, 0);
});

test("gives actionable API errors without exposing access tokens or Google payload details", async () => {
    for (const [status, payload, expected] of [
        [403, { error: { details: [{ reason: "SERVICE_DISABLED" }] } }, /app owner must enable the Google Calendar API/],
        [403, { error: { errors: [{ reason: "accessNotConfigured" }] } }, /app owner must enable/],
        [403, { error: { errors: [{ reason: "forbidden" }] } }, /denied access.*read-only/],
        [403, { error: { errors: [{ reason: "rateLimitExceeded" }] } }, /too many requests/],
        [412, {}, /changed in Google.*Refresh/],
        [404, {}, /no longer available/],
        [429, {}, /too many requests/],
        [500, { error: { message: "sensitive Google server detail" } }, /unavailable right now/]
    ]) {
        const state = await setup();
        await state.api.connect("user-a", "calendar");
        state.fetch = () => response(payload, status);
        await assert.rejects(state.api.listCalendars("user-a"), error => {
            assert.match(error.message, expected);
            assert.doesNotMatch(error.message, /google-token|sensitive Google server detail/);
            return true;
        });
        assert.equal(state.requests.length, 1);
    }
});

test("partial scope denial disconnects only the affected service so it can be reconnected", async () => {
    const state = await setup();
    await state.api.connect("user-a", "calendar");
    await state.api.connect("user-a", "tasks");
    state.fetch = () => response({ error: { details: [{ reason: "ACCESS_TOKEN_SCOPE_INSUFFICIENT" }] } }, 403);
    await assert.rejects(state.api.listCalendars("user-a"), /denied access/);
    assert.equal(await state.api.isConnected("user-a", "calendar"), false);
    assert.equal(await state.api.isConnected("user-a", "tasks"), true);
});

test("consent failures can be retried and never grant a connection", async () => {
    for (const [code, expected] of [
        ["auth/popup-blocked", /Allow pop-ups/], ["auth/popup-closed-by-user", /closed/],
        ["auth/user-mismatch", /same Google account/], ["auth/unauthorized-domain", /authorized domains/]
    ]) {
        const state = await setup();
        state.reauthenticate = () => { throw Object.assign(new Error(code), { code }); };
        await assert.rejects(state.api.connect("user-a", "tasks"), expected);
        assert.equal(await state.api.isConnected("user-a", "tasks"), false);
        state.reauthenticate = null;
        await state.api.connect("user-a", "tasks");
        assert.equal(await state.api.isConnected("user-a", "tasks"), true);
    }
});

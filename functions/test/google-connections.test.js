import assert from "node:assert/strict";
import {test} from "node:test";
import {handleGoogleConnection} from "../google-connections.js";

const calendarScopes = ["https://www.googleapis.com/auth/calendar.calendarlist.readonly", "https://www.googleapis.com/auth/calendar.events"];
const path = "users/alice/googleConnections/calendar";
const response = (body, status = 200) => ({ok: status >= 200 && status < 300, status, json: async () => body});

function setup() {
  const records = new Map();
  let revision = 0;
  const state = {records, requests: [], now: 1000};
  state.save = (key, data) => records.set(key, {data: structuredClone(data), revision: ++revision});
  const snapshot = (key) => {
    const record = records.get(key);
    return {
      exists: Boolean(record), data: () => record ? structuredClone(record.data) : undefined,
      updateTime: {revision: record?.revision, isEqual: (other) => record?.revision === other.revision},
    };
  };
  const dependencies = {
    clientId: "client", clientSecret: "secret", allowedOrigins: ["https://app.test"], now: () => state.now,
    auth: {getUser: async (uid) => ({disabled: false, providerData: [{providerId: "google.com", uid: `google-${uid}`}]})},
    db: {
      doc: (key) => ({key, get: async () => snapshot(key)}),
      runTransaction: async (run) => run({
        get: async (ref) => snapshot(ref.key),
        set: (ref, data) => state.save(ref.key, data),
        delete: (ref) => records.delete(ref.key),
      }),
    },
    fetch: async (url, options) => {
      state.requests.push({url, ...options});
      if (state.fetch) return state.fetch(url, options);
      return url.endsWith("/userinfo") ? response({sub: "google-alice"}) : response({
        access_token: "new-access", refresh_token: "new-refresh", expires_in: 3600,
        scope: ["openid", ...calendarScopes].join(" "),
      });
    },
  };
  state.call = (data = {}, uid = "alice", origin = "https://app.test") => handleGoogleConnection({
    auth: uid ? {uid} : null, data: {service: "calendar", action: "get", ...data},
    rawRequest: {get: (name) => name === "origin" ? origin : null},
  }, dependencies);
  state.seed = (overrides = {}) => state.save(path, {
    googleSubject: "google-alice", accessToken: "old-access", refreshToken: "old-refresh",
    expiresAt: 500, scopes: calendarScopes, ...overrides,
  });
  return state;
}

test("code exchange saves both tokens with Google's expiry and never returns the refresh token", async () => {
  const state = setup();
  const result = await state.call({action: "exchange", code: "auth-code", uid: "victim"});
  assert.deepEqual(result, {accessToken: "new-access", expiresAt: 3601000});
  assert.equal(state.records.get(path).data.refreshToken, "new-refresh");
  assert.equal(state.records.size, 1);
  const request = new URLSearchParams(state.requests[0].body);
  assert.equal(request.get("redirect_uri"), "https://app.test");
  assert.equal(request.get("code"), "auth-code");
  assert.equal(request.get("client_secret"), "secret");
  assert.equal(state.requests[1].headers.Authorization, "Bearer new-access");
});

test("valid tokens survive repeated sessions and remain isolated to the authenticated UID", async () => {
  const state = setup();
  state.seed({expiresAt: 3601000});
  assert.deepEqual(await state.call(), {accessToken: "old-access", expiresAt: 3601000});
  assert.deepEqual(await state.call(), {accessToken: "old-access", expiresAt: 3601000});
  assert.equal(await state.call({uid: "alice"}, "bob"), null);
  assert.equal(state.requests.length, 0);
});

test("expired and nearly expired tokens renew while retaining an omitted refresh token", async () => {
  for (const expiresAt of [500, 61000]) {
    const state = setup();
    state.seed({expiresAt});
    state.fetch = () => response({access_token: "renewed", expires_in: 3600});
    assert.deepEqual(await state.call(), {accessToken: "renewed", expiresAt: 3601000});
    assert.equal(state.records.get(path).data.refreshToken, "old-refresh");
    assert.equal(new URLSearchParams(state.requests[0].body).get("refresh_token"), "old-refresh");
    assert.equal(new URLSearchParams(state.requests[0].body).get("grant_type"), "refresh_token");
  }
});

test("a 401 forces refresh only if the rejected token is still current", async () => {
  const state = setup();
  state.seed({expiresAt: 3601000});
  assert.equal((await state.call({rejectedAccessToken: "older-access"})).accessToken, "old-access");
  assert.equal(state.requests.length, 0);
  assert.equal((await state.call({rejectedAccessToken: "old-access"})).accessToken, "new-access");
  assert.equal(state.requests.length, 1);
});

test("invalid_grant removes unusable credentials; transient and configuration failures preserve them", async () => {
  const state = setup();
  state.seed();
  state.fetch = () => response({error: "invalid_grant"}, 400);
  assert.equal(await state.call(), null);
  assert.equal(state.records.size, 0);
  for (const failure of [
    () => response({error: "invalid_client"}, 400),
    () => response({error: "temporarily_unavailable"}, 503),
    () => response({}, 429),
    () => {
      throw new Error("Network failed with secret data");
    },
    () => response({access_token: "incomplete"}),
  ]) {
    state.seed();
    state.fetch = failure;
    await assert.rejects(state.call(), (error) => error.code === "unavailable" && !/secret data/.test(error.message));
    assert.equal(state.records.get(path).data.refreshToken, "old-refresh");
  }
});

test("rotated refresh tokens are persisted", async () => {
  const state = setup();
  state.seed();
  await state.call();
  assert.equal(state.records.get(path).data.refreshToken, "new-refresh");
});

test("a delayed refresh or invalid_grant cannot replace a newer consent grant", async () => {
  for (const status of [200, 400]) {
    const state = setup();
    state.seed();
    state.fetch = () => {
      state.seed({accessToken: "newer-consent", refreshToken: "newer-refresh", expiresAt: 7201000});
      return status === 400 ? response({error: "invalid_grant"}, 400) : response({access_token: "stale-refresh", expires_in: 3600});
    };
    assert.equal((await state.call()).accessToken, "newer-consent");
    assert.equal(state.records.get(path).data.refreshToken, "newer-refresh");
  }
});

test("denied service scopes and mismatched Google accounts never replace saved credentials", async () => {
  for (const mismatch of [false, true]) {
    const state = setup();
    state.seed();
    state.fetch = (url) => url.endsWith("/userinfo") ?
            response({sub: "google-bob"}) :
            response({access_token: "wrong-access", refresh_token: "wrong-refresh", expires_in: 3600, scope: mismatch ? calendarScopes.join(" ") : "openid"});
    await assert.rejects(state.call({action: "exchange", code: "code"}), (error) => error.code === "permission-denied");
    assert.equal(state.records.get(path).data.refreshToken, "old-refresh");
  }
});

test("repeated consent retains the existing refresh token when Google omits it", async () => {
  const state = setup();
  state.seed();
  state.fetch = (url) => url.endsWith("/userinfo") ? response({sub: "google-alice"}) :
        response({access_token: "new-access", expires_in: 3600, scope: calendarScopes.join(" ")});
  await state.call({action: "exchange", code: "code"});
  assert.equal(state.records.get(path).data.refreshToken, "old-refresh");
  state.records.clear();
  await assert.rejects(state.call({action: "exchange", code: "code"}), /offline access/);
  assert.equal(state.records.size, 0);
});

test("missing service scopes require consent without repeatedly refreshing", async () => {
  const state = setup();
  state.seed();
  state.fetch = () => response({access_token: "partial", expires_in: 3600, scope: "openid"});
  assert.equal(await state.call(), null);
  assert.equal(await state.call(), null);
  assert.equal(state.requests.length, 1);
  assert.equal(state.records.get(path).data.refreshToken, "old-refresh");
});

test("scope invalidation affects only the rejected token and service", async () => {
  const state = setup();
  state.seed();
  await state.call({action: "invalidate", rejectedAccessToken: "older-access"});
  assert.equal(state.records.get(path).data.requiresConsent, undefined);
  await state.call({action: "invalidate", rejectedAccessToken: "old-access"});
  assert.equal(await state.call(), null);
  assert.equal(state.requests.length, 0);
});

test("anonymous requests, untrusted origins, and invalid services are rejected before token exchange", async () => {
  const state = setup();
  await assert.rejects(state.call({}, null), (error) => error.code === "unauthenticated");
  await assert.rejects(state.call({}, "alice", "https://evil.test"), (error) => error.code === "permission-denied");
  await assert.rejects(state.call({service: "../data/settings"}), (error) => error.code === "invalid-argument");
  assert.equal(state.requests.length, 0);
});

test("the server supplies service scopes and the OAuth client ID", async () => {
  const state = setup();
  const config = await state.call({action: "configure"});
  assert.deepEqual(config, {clientId: "client", scopes: ["openid", ...calendarScopes]});
  assert.equal(state.requests.length, 0);
});

test("Tasks tokens are stored separately without changing Calendar access", async () => {
  const state = setup();
  state.seed();
  state.fetch = (url) => url.endsWith("/userinfo") ? response({sub: "google-alice"}) :
        response({access_token: "tasks-access", refresh_token: "tasks-refresh", expires_in: 3600, scope: "openid https://www.googleapis.com/auth/tasks"});
  assert.equal((await state.call({action: "exchange", service: "tasks", code: "code"})).accessToken, "tasks-access");
  assert.equal(state.records.get("users/alice/googleConnections/tasks").data.refreshToken, "tasks-refresh");
  assert.equal(state.records.get(path).data.refreshToken, "old-refresh");
  state.seed({googleSubject: "google-other"});
  assert.equal(await state.call(), null);
});

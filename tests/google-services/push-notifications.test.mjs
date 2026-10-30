import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, SourceTextModule, SyntheticModule } from "node:vm";

const source = await readFile(new URL("../../MyTransportAppWASM/wwwroot/js/push-notifications.js", import.meta.url), "utf8");

async function setup({ oldToken = null, oldUid = null, incomingToken = "new-fcm-token", permission = "granted", hasSubscription = true } = {}) {
  const storage = new Map();
  if (oldToken) storage.set("push-token", oldToken);
  if (oldUid) storage.set("push-uid", oldUid);

  const state = {
    functionsRegion: null,
    registrationCalls: [],
    registeredWorkerUrls: [],
    listenedInForeground: false,
  };

  const app = {
    options: {
      apiKey: "test-api-key",
      appId: "test-app-id",
      authDomain: "test.firebaseapp.com",
      messagingSenderId: "123456789",
      projectId: "test-project",
      webPushVapidKey: "test-vapid-key",
    },
  };

  const windowObj = {
    Notification: {
      permission,
      requestPermission: async () => permission,
    },
  };

  const navigatorObj = {
    serviceWorker: {
      getRegistration: async () => hasSubscription ? ({
        pushManager: {
          getSubscription: async () => ({ endpoint: "https://fcm.googleapis.com/fcm/send/123" }),
        },
      }) : null,
      register: async (href, options) => {
        state.registeredWorkerUrls.push({ href, options });
        return { scope: options?.scope };
      },
    },
  };

  const context = createContext({
    window: windowObj,
    navigator: navigatorObj,
    Notification: windowObj.Notification,
    isSecureContext: true,
    localStorage: {
      getItem: key => storage.get(key) ?? null,
      setItem: (key, val) => storage.set(key, val),
      removeItem: key => storage.delete(key),
    },
    URL,
    console,
  });

  const functionsSdk = new SyntheticModule(["getFunctions", "httpsCallable"], function () {
    this.setExport("getFunctions", (_app, region) => {
      state.functionsRegion = region;
      return _app;
    });
    this.setExport("httpsCallable", (_functions, name) => {
      assert.equal(name, "pushRegistration");
      return async data => {
        state.registrationCalls.push(data);
        return { data: { success: true } };
      };
    });
  }, { context });
  await functionsSdk.link(() => {});
  await functionsSdk.evaluate();

  const messagingSdk = new SyntheticModule(["isSupported", "getMessaging", "getToken", "deleteToken", "onMessage"], function () {
    this.setExport("isSupported", async () => true);
    this.setExport("getMessaging", () => ({}));
    this.setExport("getToken", async () => incomingToken);
    this.setExport("deleteToken", async () => true);
    this.setExport("onMessage", () => {
      state.listenedInForeground = true;
      return () => {};
    });
  }, { context });
  await messagingSdk.link(() => {});
  await messagingSdk.evaluate();

  const configModule = new SyntheticModule(["getFirebaseApp", "firebaseSdkBase"], function () {
    this.setExport("getFirebaseApp", async () => app);
    this.setExport("firebaseSdkBase", "https://cdn.example.com/firebase/10.14.1");
  }, { context });
  await configModule.link(() => {});
  await configModule.evaluate();

  const authModule = new SyntheticModule(["getAuthContext", "requireSignedInUser"], function () {
    this.setExport("getAuthContext", async () => ({ auth: { currentUser: { uid: "user-123" } } }));
    this.setExport("requireSignedInUser", async () => {});
  }, { context });
  await authModule.link(() => {});
  await authModule.evaluate();

  const module = new SourceTextModule(source, {
    context,
    initializeImportMeta(meta) {
      meta.url = "https://example.com/MyTransportProject/js/push-notifications.js";
    },
    importModuleDynamically: async specifier => {
      if (specifier.includes("firebase-functions.js")) return functionsSdk;
      if (specifier.includes("firebase-messaging.js")) return messagingSdk;
      throw new Error(`Unexpected dynamic import: ${specifier}`);
    },
  });

  await module.link(specifier => {
    if (specifier === "./firebase-config.js") return configModule;
    if (specifier === "./firebase-auth.js") return authModule;
    throw new Error(`Unexpected static import: ${specifier}`);
  });
  await module.evaluate();

  return { state, storage, pushNotifications: context.window.pushNotifications };
}

const plain = value => JSON.parse(JSON.stringify(value));

test("status(uid) calls getFunctions with asia-southeast1 and updates registration when token is new", async () => {
  const { state, storage, pushNotifications } = await setup({ oldToken: null, incomingToken: "fcm-token-abc" });
  const status = await pushNotifications.status("user-123");

  assert.equal(status, "enabled");
  assert.equal(state.functionsRegion, "asia-southeast1");
  assert.deepEqual(plain(state.registrationCalls), [
    { action: "add", token: "fcm-token-abc" },
  ]);
  assert.equal(storage.get("push-token"), "fcm-token-abc");
  assert.equal(storage.get("push-uid"), "user-123");
  assert.equal(state.registeredWorkerUrls.length, 1);
  const swUrl = new URL(state.registeredWorkerUrls[0].href);
  assert.equal(swUrl.searchParams.get("sdkBase"), "https://cdn.example.com/firebase/10.14.1");
});

test("status(uid) removes old token and adds new token when token changes", async () => {
  const { state, storage, pushNotifications } = await setup({ oldToken: "old-token-xyz", oldUid: "user-123", incomingToken: "new-token-123" });
  const status = await pushNotifications.status("user-123");

  assert.equal(status, "enabled");
  assert.deepEqual(plain(state.registrationCalls), [
    { action: "add", token: "new-token-123" },
    { action: "remove", token: "old-token-xyz" },
  ]);
  assert.equal(storage.get("push-token"), "new-token-123");
  assert.equal(storage.get("push-uid"), "user-123");
});

test("status(uid) skips updateRegistration when push-token and push-uid match current session", async () => {
  const { state, storage, pushNotifications } = await setup({ oldToken: "existing-token", oldUid: "user-123", incomingToken: "existing-token" });
  const status = await pushNotifications.status("user-123");

  assert.equal(status, "enabled");
  assert.equal(state.registrationCalls.length, 0);
  assert.equal(storage.get("push-token"), "existing-token");
  assert.equal(storage.get("push-uid"), "user-123");
});

test("status(uid) registers token for new user when switching accounts on the same device", async () => {
  const { state, storage, pushNotifications } = await setup({ oldToken: "existing-token", oldUid: "user-old", incomingToken: "existing-token" });
  const status = await pushNotifications.status("user-new");

  assert.equal(status, "enabled");
  assert.deepEqual(plain(state.registrationCalls), [
    { action: "add", token: "existing-token" },
  ]);
  assert.equal(storage.get("push-token"), "existing-token");
  assert.equal(storage.get("push-uid"), "user-new");
});

test("enable(uid) stores push-uid and disable(uid) clears push-uid", async () => {
  const { state, storage, pushNotifications } = await setup({ incomingToken: "enabled-token" });
  await pushNotifications.enable("user-123");
  assert.equal(storage.get("push-token"), "enabled-token");
  assert.equal(storage.get("push-uid"), "user-123");

  await pushNotifications.disable("user-123");
  assert.equal(storage.get("push-token"), undefined);
  assert.equal(storage.get("push-uid"), undefined);
});

test("disable(uid) cleans up orphaned push-uid when no active token or subscription exists", async () => {
  const { storage, pushNotifications } = await setup({ oldToken: null, oldUid: "orphaned-user", hasSubscription: false });
  assert.equal(storage.get("push-uid"), "orphaned-user");
  const result = await pushNotifications.disable("user-123");
  assert.equal(result, true);
  assert.equal(storage.get("push-uid"), undefined);
});

test("status() without signed-in user skips updateRegistration", async () => {
  const { state, storage, pushNotifications } = await setup({ oldToken: "existing-token", oldUid: "user-123", incomingToken: "existing-token" });
  const status = await pushNotifications.status(null);
  assert.equal(status, "enabled");
  assert.equal(state.registrationCalls.length, 0);
  assert.equal(storage.get("push-token"), "existing-token");
  assert.equal(storage.get("push-uid"), "user-123");
});

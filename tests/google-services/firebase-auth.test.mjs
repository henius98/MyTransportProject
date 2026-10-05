import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, SourceTextModule, SyntheticModule } from "node:vm";

const authSource = await readFile(new URL("../../MyTransportAppWASM/wwwroot/js/firebase-auth.js", import.meta.url), "utf8");
const storeSource = await readFile(new URL("../../MyTransportAppWASM/wwwroot/js/google-token-store.js", import.meta.url), "utf8");

async function setup({ restoredUid = null, restoration = Promise.resolve(), scriptFailure = false, deferScript = false, credentialFailure = false } = {}) {
    const storage = new Map();
    const callbacks = [];
    const state = { scripts: [], prompts: 0, cancellations: 0, autoSelectDisabled: 0, credentials: [], popups: 0, warnings: [] };
    const auth = { currentUser: null, authStateReady: async () => {
        await restoration;
        state.notifyUser(restoredUid);
    } };
    const window = {};
    const googleIdentity = {
        initialize: options => { state.oneTapOptions = options; },
        prompt: () => { state.prompts++; },
        cancel: () => { state.cancellations++; },
        disableAutoSelect: () => { state.autoSelectDisabled++; }
    };
    state.loadScript = () => {
        window.google = { accounts: { id: googleIdentity } };
        state.scripts[0].onload();
    };
    const context = createContext({
        window, console: { ...console, warn: (...args) => state.warnings.push(args) },
        document: {
            createElement: tag => { assert.equal(tag, "script"); return {}; },
            head: { appendChild: script => {
                state.scripts.push(script);
                if (scriptFailure) script.onerror();
                else if (!deferScript) state.loadScript();
            } }
        },
        localStorage: {
            getItem: key => storage.get(key) ?? null,
            setItem: (key, value) => storage.set(key, value),
            removeItem: key => storage.delete(key)
        }
    });
    const sdk = new SyntheticModule(["getAuth", "GoogleAuthProvider", "onAuthStateChanged", "signOut", "signInWithCredential", "signInWithPopup"], function () {
        this.setExport("getAuth", () => auth);
        this.setExport("GoogleAuthProvider", class {
            setCustomParameters() {}
            static credential(idToken) { return { idToken }; }
        });
        this.setExport("onAuthStateChanged", (_auth, callback) => { callbacks.push(callback); return () => {}; });
        // Keep the notification deferred so explicit sign-out must clear tokens before returning.
        this.setExport("signOut", async () => { auth.currentUser = null; });
        this.setExport("signInWithCredential", async (target, credential) => {
            assert.equal(target, auth);
            state.credentials.push(credential.idToken);
            if (credentialFailure) throw Object.assign(new Error("Rejected"), { code: "auth/invalid-credential" });
            state.notifyUser("one-tap-user");
            return { user: auth.currentUser };
        });
        this.setExport("signInWithPopup", async () => {
            state.popups++;
            state.notifyUser("popup-user");
            return { user: auth.currentUser };
        });
    }, { context });
    await sdk.link(() => assert.fail("Unexpected SDK dependency"));
    await sdk.evaluate();
    const config = new SyntheticModule(["getFirebaseApp", "firebaseSdkBase"], function () {
        this.setExport("getFirebaseApp", async () => ({}));
        this.setExport("firebaseSdkBase", "https://firebase.test");
    }, { context });
    const store = new SourceTextModule(storeSource, { context });
    const module = new SourceTextModule(authSource, {
        context,
        importModuleDynamically(specifier) {
            assert.equal(specifier, "https://firebase.test/firebase-auth.js");
            return sdk;
        }
    });
    await module.link(specifier => {
        if (specifier === "./google-token-store.js") return store;
        assert.equal(specifier, "./firebase-config.js");
        return config;
    });
    await module.evaluate();
    for (const service of ["calendar", "tasks"]) {
        store.namespace.setConnection(service, { uid: "user-a", accessToken: `saved-${service}`, expiresAt: 9999999 });
    }
    return Object.assign(state, {
        api: module.namespace, window, storage, callbacks,
        notifyUser(uid) {
            auth.currentUser = uid ? { uid } : null;
            callbacks.forEach(callback => callback(auth.currentUser));
        }
    });
}

test("Firebase restoration preserves saved access while waiting for the first signed-in account", async () => {
    const state = await setup();
    await state.api.getAuthContext();
    assert.equal(state.storage.size, 1);
    state.notifyUser("user-a");
    assert.equal(state.storage.size, 1);
    await state.api.getAuthContext();
    assert.equal(state.callbacks.length, 1);
});

test("an initially signed-out Firebase session clears saved access without loading Google services", async () => {
    const state = await setup();
    await state.api.getAuthContext();
    state.notifyUser(null);
    assert.equal(state.storage.size, 0);
});

test("signing out from any page clears saved access before returning", async () => {
    const state = await setup();
    await state.api.getAuthContext();
    state.notifyUser("user-a");
    assert.equal(await state.window.firebaseAuthInterop.signOut(), true);
    assert.equal(state.storage.size, 0);
});

test("account changes and sign-out notifications clear saved access without loading Google services", async () => {
    for (const uid of [null, "user-b"]) {
        const state = await setup();
        await state.api.getAuthContext();
        state.notifyUser("user-a");
        state.notifyUser(uid);
        state.notifyUser("user-a");
        assert.equal(state.storage.size, 0);
    }
});

const clientId = "test-client.apps.googleusercontent.com";
const nextTurn = () => new Promise(resolve => setImmediate(resolve));

test("One Tap waits for session restoration and prompts only once for a signed-out visitor", async () => {
    let restore;
    const state = await setup({ restoration: new Promise(resolve => { restore = resolve; }) });
    const starting = state.window.firebaseAuthInterop.initializeOneTap(clientId);
    await nextTurn();
    assert.equal(state.scripts.length, 0);
    restore();
    await starting;
    await state.window.firebaseAuthInterop.initializeOneTap(clientId);
    assert.equal(state.scripts.length, 1);
    assert.equal(state.scripts[0].src, "https://accounts.google.com/gsi/client");
    assert.equal(state.prompts, 1);
    assert.equal(state.oneTapOptions.client_id, clientId);
    assert.equal(state.oneTapOptions.auto_select, false);
});

test("One Tap skips unconfigured apps and restored signed-in sessions", async () => {
    for (const id of [null, "", "   "]) {
        const state = await setup();
        await state.window.firebaseAuthInterop.initializeOneTap(id);
        assert.equal(state.scripts.length, 0);
    }
    const state = await setup({ restoredUid: "user-a" });
    await state.window.firebaseAuthInterop.initializeOneTap(clientId);
    assert.equal(state.scripts.length, 0);
    assert.equal(state.prompts, 0);
    assert.equal(state.storage.size, 1);
});

test("One Tap exchanges the Google credential through Firebase and notifies Blazor", async () => {
    const state = await setup();
    const profiles = [];
    await state.window.firebaseAuthInterop.onAuthStateChanged({ invokeMethodAsync: async (method, profile) => {
        assert.equal(method, "OnAuthStateChanged");
        profiles.push(profile);
    } });
    await state.window.firebaseAuthInterop.initializeOneTap(clientId);
    await state.oneTapOptions.callback({ credential: "google-id-token" });
    assert.deepEqual(state.credentials, ["google-id-token"]);
    assert.equal(profiles.at(-1).uid, "one-tap-user");
    assert.equal(state.cancellations, 1);
    await state.oneTapOptions.callback({ credential: "duplicate" });
    assert.deepEqual(state.credentials, ["google-id-token"]);
});

test("manual login cancels One Tap and ignores a delayed credential", async () => {
    const state = await setup();
    await state.window.firebaseAuthInterop.initializeOneTap(clientId);
    state.window.firebaseAuthInterop.cancelOneTap();
    await state.oneTapOptions.callback({ credential: "stale-token" });
    const user = await state.window.firebaseAuthInterop.signInWithGoogle();
    assert.equal(user.uid, "popup-user");
    assert.deepEqual(state.credentials, []);
    assert.equal(state.popups, 1);
});

test("sign-out disables automatic selection and prevents One Tap reopening", async () => {
    const state = await setup();
    await state.window.firebaseAuthInterop.initializeOneTap(clientId);
    await state.window.firebaseAuthInterop.signOut();
    await state.oneTapOptions.callback({ credential: "stale-token" });
    await state.window.firebaseAuthInterop.initializeOneTap(clientId);
    assert.equal(state.autoSelectDisabled, 1);
    assert.equal(state.prompts, 1);
    assert.deepEqual(state.credentials, []);
});

test("cancellation or signing in while Google's script loads prevents a late prompt", async () => {
    for (const cancel of [state => state.window.firebaseAuthInterop.cancelOneTap(), state => state.notifyUser("user-a")]) {
        const state = await setup({ deferScript: true });
        const starting = state.window.firebaseAuthInterop.initializeOneTap(clientId);
        await nextTurn();
        assert.equal(state.scripts.length, 1);
        cancel(state);
        state.loadScript();
        await starting;
        assert.equal(state.prompts, 0);
    }
});

test("a blocked One Tap script does not break manual sign-in", async () => {
    const state = await setup({ scriptFailure: true });
    await state.window.firebaseAuthInterop.initializeOneTap(clientId);
    assert.equal(state.warnings.length, 1);
    assert.equal(state.prompts, 0);
    assert.equal((await state.window.firebaseAuthInterop.signInWithGoogle()).uid, "popup-user");
});

test("One Tap and Google authorization share one pending identity script", async () => {
    const state = await setup({ deferScript: true });
    const loading = state.api.loadGoogleIdentity();
    const oneTap = state.window.firebaseAuthInterop.initializeOneTap(clientId);
    await nextTurn();
    assert.equal(state.scripts.length, 1);
    state.loadScript();
    await Promise.all([loading, oneTap]);
    assert.equal(state.prompts, 1);
    await state.api.loadGoogleIdentity();
    assert.equal(state.scripts.length, 1);
});

test("a rejected One Tap credential leaves manual sign-in available", async () => {
    const state = await setup({ credentialFailure: true });
    await state.window.firebaseAuthInterop.initializeOneTap(clientId);
    await state.oneTapOptions.callback({ credential: "rejected-token" });
    assert.equal(state.warnings.length, 1);
    assert.equal(state.warnings[0][1], "auth/invalid-credential");
    assert.equal((await state.window.firebaseAuthInterop.signInWithGoogle()).uid, "popup-user");
});

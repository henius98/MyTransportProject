import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, SourceTextModule, SyntheticModule } from "node:vm";

const source = await readFile(new URL("../../MyTransportAppWASM/wwwroot/js/google-authorization.js", import.meta.url), "utf8");
const plain = value => JSON.parse(JSON.stringify(value));

async function setup() {
    const state = { uid: "alice", calls: [], popups: 0, imports: 0, loads: 0 };
    const context = createContext({});
    const sdk = new SyntheticModule(["getFunctions", "httpsCallable"], function () {
        this.setExport("getFunctions", app => app);
        this.setExport("httpsCallable", (_functions, name) => {
            assert.equal(name, "googleConnection");
            return async data => {
                state.calls.push(plain(data));
                if (state.call) return { data: await state.call(data) };
                return { data: data.action === "configure"
                    ? { clientId: "client.apps.googleusercontent.com", scopes: ["openid", "calendar-scope"] }
                    : { accessToken: "access", expiresAt: 3600000 } };
            };
        });
    }, { context });
    await sdk.link(() => assert.fail("Unexpected dependency"));
    await sdk.evaluate();
    const config = new SyntheticModule(["getFirebaseApp", "firebaseSdkBase"], function () {
        this.setExport("getFirebaseApp", async () => ({}));
        this.setExport("firebaseSdkBase", "https://firebase.test");
    }, { context });
    const firebase = new SyntheticModule(["loadGoogleIdentity", "requireSignedInUser"], function () {
        this.setExport("requireSignedInUser", async uid => {
            if (state.uid !== uid) throw new Error("The signed-in account has changed");
        });
        this.setExport("loadGoogleIdentity", async () => {
            state.loads++;
            return { oauth2: { initCodeClient: options => {
                state.options = options;
                return { requestCode: () => {
                    state.popups++;
                    if (state.popup) state.popup(options);
                    else options.callback({ code: "authorization-code" });
                } };
            } } };
        });
    }, { context });
    const module = new SourceTextModule(source, { context, importModuleDynamically: specifier => {
        assert.equal(specifier, "https://firebase.test/firebase-functions.js");
        state.imports++;
        return sdk;
    } });
    await module.link(specifier => specifier === "./firebase-config.js" ? config : firebase);
    await module.evaluate();
    state.api = module.namespace;
    state.authorize = (requireSession = () => {}) => state.api.authorizeConnection("alice", "calendar", "alice@example.com", requireSession);
    return state;
}

test("code authorization uses backend configuration, an account hint, and exchanges only the code", async () => {
    const state = await setup();
    await state.api.prepareAuthorization("alice", "calendar");
    const result = await state.authorize();
    assert.deepEqual(plain(result), { accessToken: "access", expiresAt: 3600000 });
    assert.equal(state.options.scope, "openid calendar-scope");
    assert.equal(state.options.login_hint, "alice@example.com");
    assert.equal(state.options.client_id, "client.apps.googleusercontent.com");
    assert.equal(state.options.ux_mode, "popup");
    assert.deepEqual(state.calls, [
        { action: "configure", service: "calendar" },
        { action: "exchange", service: "calendar", code: "authorization-code" }
    ]);
    assert.equal(state.imports, 1);
    assert.equal(state.loads, 1);
});

test("restore and invalidation use authenticated callable requests without sending a user-supplied UID", async () => {
    const state = await setup();
    await state.api.restoreConnection("alice", "tasks", "rejected");
    await state.api.invalidateConnection("alice", "tasks", "rejected");
    assert.deepEqual(state.calls, [
        { action: "get", service: "tasks", rejectedAccessToken: "rejected" },
        { action: "invalidate", service: "tasks", rejectedAccessToken: "rejected" }
    ]);
    assert.equal(state.popups, 0);
});

test("closed, blocked, and denied permission windows never send an exchange request", async () => {
    for (const [popup, expected] of [
        [options => options.error_callback({ type: "popup_failed_to_open" }), /Allow pop-ups/],
        [options => options.error_callback({ type: "popup_closed" }), /closed/],
        [options => options.callback({ error: "access_denied" }), /did not grant/]
    ]) {
        const state = await setup();
        state.popup = popup;
        await assert.rejects(state.authorize(), expected);
        assert.equal(state.calls.length, 1);
    }
});

test("account changes and sign-out-and-back-in during consent prevent code exchange", async () => {
    for (const sameAccount of [false, true]) {
        const state = await setup();
        let sessionChanged = false;
        state.popup = options => {
            state.uid = sameAccount ? "alice" : "bob";
            sessionChanged = true;
            options.callback({ code: "stale-code" });
        };
        await assert.rejects(state.authorize(() => {
            if (sameAccount && sessionChanged) throw new Error("The signed-in account has changed");
        }), /account has changed/);
        assert.equal(state.calls.length, 1);
    }
});

test("late cloud responses cannot be returned to another signed-in account", async () => {
    const state = await setup();
    state.call = () => { state.uid = "bob"; return { accessToken: "private" }; };
    await assert.rejects(state.api.restoreConnection("alice", "tasks"), /account has changed/);
});

test("a failed preparation can be retried", async () => {
    const state = await setup();
    state.call = () => { throw new Error("Offline"); };
    await assert.rejects(state.api.prepareAuthorization("alice", "calendar"), /Offline/);
    state.call = null;
    await state.authorize();
    assert.equal(state.popups, 1);
});

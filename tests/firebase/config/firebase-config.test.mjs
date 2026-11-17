import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, SourceTextModule, SyntheticModule } from "node:vm";

const source = await readFile(new URL("../../../MyTransportAppWASM/wwwroot/js/firebase-config.js", import.meta.url), "utf8");
const firebaseConfig = {
    apiKey: "base-api-key", authDomain: "base.firebaseapp.com", projectId: "base-project", appId: "base-app",
    storageBucket: "base.appspot.com"
};
const plain = value => JSON.parse(JSON.stringify(value));

async function setup() {
    const state = { initialized: [], imports: [] };
    const window = {};
    const context = createContext({ window });
    const firebase = new SyntheticModule(["initializeApp"], function () {
        this.setExport("initializeApp", config => {
            state.initialized.push(plain(config));
            return { name: "[DEFAULT]", options: config };
        });
    }, { context });
    await firebase.link(() => assert.fail("Unexpected Firebase dependency"));
    await firebase.evaluate();
    const module = new SourceTextModule(source, {
        context,
        importModuleDynamically(specifier) {
            state.imports.push(specifier);
            assert.match(specifier, /^https:\/\/www\.gstatic\.com\/firebasejs\/[^/]+\/firebase-app\.js$/);
            return firebase;
        }
    });
    await module.link(() => assert.fail("Unexpected static dependency"));
    await module.evaluate();
    state.api = module.namespace;
    state.window = window;
    return state;
}

test("initializes Firebase from configured options without fetching appsettings or setting globals", async () => {
    const state = await setup();
    state.api.configureFirebase(firebaseConfig);
    const app = await state.api.getFirebaseApp();
    assert.equal(app.name, "[DEFAULT]");
    assert.deepEqual(state.initialized, [firebaseConfig]);
    assert.equal(state.window.firebaseApp, undefined);
});

test("rejects missing Firebase settings and invalid required values before loading the SDK", async () => {
    const invalidConfigs = [null, undefined, {}];
    for (const key of ["apiKey", "authDomain", "projectId", "appId"]) {
        for (const value of [undefined, "", 123, "YOUR_VALUE"]) {
            invalidConfigs.push({ ...firebaseConfig, [key]: value });
        }
    }
    for (const config of invalidConfigs) {
        const state = await setup();
        state.api.configureFirebase(config);
        await assert.rejects(state.api.getFirebaseApp(), /sign-in has not been configured/);
        assert.equal(state.imports.length, 0);
        assert.equal(state.window.firebaseApp, undefined);
    }
});

test("retries cleanly once valid Firebase configuration is pushed after an earlier failure", async () => {
    const state = await setup();
    await assert.rejects(state.api.getFirebaseApp(), /sign-in has not been configured/);
    assert.equal(state.initialized.length, 0);

    state.api.configureFirebase(firebaseConfig);
    await state.api.getFirebaseApp();
    assert.deepEqual(state.initialized, [firebaseConfig]);
});

test("concurrent and subsequent callers share one initialized Firebase app", async () => {
    const state = await setup();
    state.api.configureFirebase(firebaseConfig);
    const apps = await Promise.all(Array.from({ length: 5 }, () => state.api.getFirebaseApp()));
    assert.ok(apps.every(app => app === apps[0]));
    assert.equal(await state.api.getFirebaseApp(), apps[0]);
    assert.equal(state.initialized.length, 1);
    assert.equal(state.imports.length, 1);
});

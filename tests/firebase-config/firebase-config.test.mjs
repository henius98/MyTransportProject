import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, SourceTextModule, SyntheticModule } from "node:vm";

const source = await readFile(new URL("../../MyTransportAppWASM/wwwroot/js/firebase-config.js", import.meta.url), "utf8");
const firebaseConfig = {
    apiKey: "base-api-key", authDomain: "base.firebaseapp.com", projectId: "base-project", appId: "base-app",
    storageBucket: "base.appspot.com"
};
const response = (payload, status = 200) => ({ ok: status >= 200 && status < 300, status, json: async () => payload });
const plain = value => JSON.parse(JSON.stringify(value));

async function setup({
    environment = "Production", appsettings = ["../appsettings.json"],
    moduleUrl = "https://example.com/js/firebase-config.js", base = { Firebase: firebaseConfig }, overrides = {}
} = {}) {
    const state = { requests: [], initialized: [], imports: [], files: { "appsettings.json": base, ...overrides } };
    const window = { Blazor: { runtime: { getConfig: () => ({ applicationEnvironment: environment, appsettings }) } } };
    const context = createContext({
        URL, window,
        fetch: async (url, options) => {
            state.requests.push({ url: String(url), ...options });
            if (state.fetch) return state.fetch(url, options);
            const filename = new URL(url).pathname.split("/").at(-1);
            return Object.hasOwn(state.files, filename) ? response(state.files[filename]) : response({}, 404);
        }
    });
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
        initializeImportMeta(meta) { meta.url = moduleUrl; },
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

test("uses base Firebase settings without requesting an absent environment file", async () => {
    const state = await setup({ appsettings: ["../appsettings.json", "../appsettings.Development.json"] });
    const app = await state.api.getFirebaseApp();
    assert.deepEqual(state.initialized, [firebaseConfig]);
    assert.deepEqual(state.requests, [{ url: "https://example.com/appsettings.json", cache: "no-cache" }]);
    assert.equal(state.window.firebaseApp, app);
});

test("merges the active Development or Staging Firebase section over inherited base values", async () => {
    for (const environment of ["Development", "Staging"]) {
        const filename = `appsettings.${environment}.json`;
        const state = await setup({
            environment,
            appsettings: ["../appsettings.json", `../${filename}`, "../appsettings.Production.json"],
            base: { Firebase: firebaseConfig, Logging: { level: "Information" } },
            overrides: {
                [filename]: { Firebase: { apiKey: `${environment}-key`, projectId: `${environment}-project` }, Logging: { level: "Debug" } },
                "appsettings.Production.json": { Firebase: { apiKey: "inactive-key" } }
            }
        });
        await state.api.getFirebaseApp();
        assert.deepEqual(state.initialized, [{ ...firebaseConfig, apiKey: `${environment}-key`, projectId: `${environment}-project` }]);
        assert.deepEqual(state.requests, [
            { url: "https://example.com/appsettings.json", cache: "no-cache" },
            { url: `https://example.com/${filename}`, cache: "no-cache" }
        ]);
    }
});

test("resolves configuration beneath the deployed project path and accepts an override without Firebase", async () => {
    const state = await setup({
        environment: "Development", appsettings: ["appsettings.json", "appsettings.Development.json"],
        moduleUrl: "https://example.com/MyTransportAppWASM/js/firebase-config.js",
        overrides: { "appsettings.Development.json": { Logging: { level: "Debug" } } }
    });
    await state.api.getFirebaseApp();
    assert.deepEqual(state.initialized, [firebaseConfig]);
    assert.deepEqual(state.requests.map(request => request.url), [
        "https://example.com/MyTransportAppWASM/appsettings.json",
        "https://example.com/MyTransportAppWASM/appsettings.Development.json"
    ]);
});

test("rejects missing Firebase settings and invalid required values before loading the SDK", async () => {
    const invalidSettings = [{}, { Firebase: null }];
    for (const key of ["apiKey", "authDomain", "projectId", "appId"]) {
        for (const value of [undefined, "", 123, "YOUR_VALUE"]) {
            invalidSettings.push({ Firebase: { ...firebaseConfig, [key]: value } });
        }
    }
    for (const base of invalidSettings) {
        const state = await setup({ base });
        await assert.rejects(state.api.getFirebaseApp(), /sign-in has not been configured/);
        assert.equal(state.imports.length, 0);
        assert.equal(state.window.firebaseApp, undefined);
    }
});

test("validates the merged settings so invalid environment overrides cannot use the base value", async () => {
    const state = await setup({
        environment: "Development", appsettings: ["../appsettings.json", "../appsettings.Development.json"],
        overrides: { "appsettings.Development.json": { Firebase: { apiKey: "" } } }
    });
    await assert.rejects(state.api.getFirebaseApp(), /sign-in has not been configured/);
    assert.equal(state.imports.length, 0);
});

test("retries after base or active environment configuration cannot be fetched", async () => {
    for (const failedFile of ["appsettings.json", "appsettings.Staging.json"]) {
        for (const networkFailure of [false, true]) {
            const state = await setup({
                environment: "Staging", appsettings: ["../appsettings.json", "../appsettings.Staging.json"],
                overrides: { "appsettings.Staging.json": { Firebase: { projectId: "staging-project" } } }
            });
            state.fetch = url => {
                const filename = new URL(url).pathname.split("/").at(-1);
                if (filename !== failedFile) return response(state.files[filename]);
                if (networkFailure) throw new Error("Network unavailable");
                return response({}, 404);
            };
            await assert.rejects(state.api.getFirebaseApp(), networkFailure ? /Network unavailable/ : /Could not load sign-in configuration/);
            assert.equal(state.initialized.length, 0);
            state.fetch = null;
            await state.api.getFirebaseApp();
            assert.deepEqual(state.initialized, [{ ...firebaseConfig, projectId: "staging-project" }]);
        }
    }
});

test("concurrent and subsequent callers share one initialized Firebase app", async () => {
    const state = await setup();
    const apps = await Promise.all(Array.from({ length: 5 }, () => state.api.getFirebaseApp()));
    assert.ok(apps.every(app => app === apps[0]));
    assert.equal(await state.api.getFirebaseApp(), apps[0]);
    assert.equal(state.window.firebaseApp, apps[0]);
    assert.equal(state.requests.length, 1);
    assert.equal(state.initialized.length, 1);
    assert.equal(state.imports.length, 1);
});

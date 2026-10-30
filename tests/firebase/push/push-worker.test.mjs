import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, runInContext } from "node:vm";

const source = await readFile(new URL("../../../MyTransportAppWASM/wwwroot/firebase-messaging-sw.js", import.meta.url), "utf8");

function worker() {
    const config = { apiKey: "public-key", projectId: "test" };
    const url = new URL("https://example.com/MyTransportProject/firebase-messaging-sw.js");
    url.searchParams.set("config", JSON.stringify(config));
    const handlers = {};
    const shown = [];
    const opened = [];
    let backgroundMessage;
    let initialized;
    const self = {
        location: url,
        registration: {
            scope: "https://example.com/MyTransportProject/",
            showNotification: async (title, options) => { shown.push({ title, options }); }
        },
        addEventListener: (name, handler) => { handlers[name] = handler; }
    };
    const firebase = {
        initializeApp: value => { initialized = value; },
        messaging: () => ({ onBackgroundMessage: handler => { backgroundMessage = handler; } })
    };
    const clients = {
        matchAll: async () => [],
        openWindow: async target => { opened.push(target); }
    };
    const imported = [];
    runInContext(source, createContext({ URL, self, firebase, clients, importScripts: script => { imported.push(script); } }));
    return { config, initialized, handlers, shown, opened, imported, onBackgroundMessage: payload => backgroundMessage(payload) };
}

test("the worker displays a data-only push and opens its app path when tapped", async () => {
    const instance = worker();
    assert.deepEqual(JSON.parse(JSON.stringify(instance.initialized)), instance.config);
    await instance.onBackgroundMessage({ data: { title: "Bus update", body: "Arriving", url: "map" } });
    assert.equal(instance.shown.length, 1);
    assert.equal(instance.shown[0].title, "Bus update");
    assert.equal(instance.shown[0].options.data.url, "https://example.com/MyTransportProject/map");
    let completion;
    instance.handlers.notificationclick({
        notification: { data: instance.shown[0].options.data, close() {} },
        waitUntil: promise => { completion = promise; }
    });
    await completion;
    assert.deepEqual(instance.opened, ["https://example.com/MyTransportProject/map"]);
});

test("the worker avoids duplicate notification payloads and confines links to the app", async () => {
    const instance = worker();
    await instance.onBackgroundMessage({ notification: { title: "FCM displays this" } });
    assert.equal(instance.shown.length, 0);
    await instance.onBackgroundMessage({ data: { title: "Alert", url: "https://outside.example/" } });
    assert.equal(instance.shown[0].options.data.url, "https://example.com/MyTransportProject/");
});

test("the worker loads SDK scripts from configured sdkBase query parameter", () => {
    const config = { apiKey: "public-key", projectId: "test" };
    const url = new URL("https://example.com/MyTransportProject/firebase-messaging-sw.js");
    url.searchParams.set("config", JSON.stringify(config));
    url.searchParams.set("sdkBase", "https://cdn.example.com/firebase/10.14.1");
    const imported = [];
    const self = {
        location: url,
        registration: { scope: "https://example.com/MyTransportProject/" },
        addEventListener: () => {}
    };
    const firebase = {
        initializeApp: () => {},
        messaging: () => ({ onBackgroundMessage: () => {} })
    };
    runInContext(source, createContext({ URL, self, firebase, clients: {}, importScripts: s => imported.push(s) }));
    assert.deepEqual(imported, [
        "https://cdn.example.com/firebase/10.14.1/firebase-app-compat.js",
        "https://cdn.example.com/firebase/10.14.1/firebase-messaging-compat.js"
    ]);
});

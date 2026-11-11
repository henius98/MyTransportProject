import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { configureAppSettings } from "../../../scripts/configure-firebase.mjs";

const appsettings = await readFile(new URL("../../../MyTransportAppWASM/wwwroot/appsettings.json", import.meta.url), "utf8");
const devSettings = JSON.parse(await readFile(new URL("../../../MyTransportAppWASM/wwwroot/appsettings.Development.example.json", import.meta.url), "utf8"));
const firebase = { apiKey: "key", authDomain: "app.firebaseapp.com", projectId: "app", appId: "1:2:web:3" };
const parse = text => JSON.parse(text.replace(/\s\/\/.*$/gm, ""));

test("injects public Firebase keys only and keeps inline comments", () => {
    const updated = configureAppSettings(appsettings, {
        FIREBASE_CONFIG_JSON: JSON.stringify({ ...firebase, webPushVapidKey: "vapid", privateKey: "secret" })
    });
    assert.deepEqual(parse(updated).Firebase, { ...firebase, webPushVapidKey: "vapid" });
    assert.ok(updated.includes("// Get route IDs and names without fetching every trip."));
});

test("strips trailing shell backslashes from the Firebase JSON", () => {
    const raw = `{"apiKey":"key",\\\n"authDomain":"app.firebaseapp.com",\\\n"projectId":"app","appId":"1:2:web:3"}`;
    assert.deepEqual(parse(configureAppSettings(appsettings, { FIREBASE_CONFIG_JSON: raw })).Firebase, firebase);
});

test("rejects missing or placeholder required Firebase keys", () => {
    for (const key of Object.keys(firebase)) {
        for (const value of [undefined, "", "YOUR_VALUE"]) {
            const config = JSON.stringify({ ...firebase, [key]: value });
            assert.throws(() => configureAppSettings(appsettings, { FIREBASE_CONFIG_JSON: config }), new RegExp(`Firebase ${key} is missing`));
        }
    }
    assert.throws(() => configureAppSettings("{}", {}), /Firebase section is missing/);
});

test("maps static data and BaziFlow base URLs by provider name", () => {
    const updated = configureAppSettings(appsettings, {
        FIREBASE_CONFIG_JSON: JSON.stringify(firebase),
        STATIC_DATA_BASE_URL: "https://worker.example/",
        BAZIFLOW_BASE_URL: "https://bazi.example/"
    }, devSettings);
    const settings = parse(updated);
    const byName = Object.fromEntries(settings.TransportProviders.map(p => [p.Name, p.StaticData?.BaseUrl]));
    assert.equal(byName["Rapid Bus KL"], "https://worker.example/rapid-bus-kl");
    assert.equal(byName["BAS.MY Seremban (A)"], "https://worker.example/mybas-seremban-a");
    assert.equal(settings.BaziFlow.BaseUrl, "https://bazi.example");
    assert.ok(updated.includes("// Without a filter, return all GeoJSON; ?route_id= returns one route."));
});

test("leaves endpoints untouched when no base URLs are supplied", () => {
    const updated = configureAppSettings(appsettings, { FIREBASE_CONFIG_JSON: JSON.stringify(firebase) }, devSettings);
    const settings = parse(updated);
    assert.ok(settings.TransportProviders.every(p => !p.StaticData || p.StaticData.BaseUrl === ""));
    assert.equal(settings.BaziFlow.BaseUrl, "");
});

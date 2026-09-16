import { readFile, writeFile } from "node:fs/promises";

const path = new URL("../MyTransportAppWASM/wwwroot/appsettings.json", import.meta.url);
const settings = JSON.parse(await readFile(path, "utf8"));
const config = process.env.FIREBASE_CONFIG_JSON
    ? JSON.parse(process.env.FIREBASE_CONFIG_JSON)
    : settings.Firebase ?? {};
for (const key of ["apiKey", "authDomain", "projectId", "appId"]) {
    if (typeof config[key] !== "string" || !config[key].trim() || config[key].startsWith("YOUR_")) {
        throw new Error(`Firebase ${key} is missing. Set the FIREBASE_CONFIG_JSON repository variable or configure the Firebase section in wwwroot/appsettings.json.`);
    }
}
const publicConfig = Object.fromEntries(
    ["apiKey", "authDomain", "projectId", "appId", "storageBucket", "messagingSenderId", "measurementId"]
        .filter(key => typeof config[key] === "string")
        .map(key => [key, config[key]])
);
settings.Firebase = publicConfig;
await writeFile(path, JSON.stringify(settings, null, 2) + "\n");

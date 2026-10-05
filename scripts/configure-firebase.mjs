import { readFile, writeFile } from "node:fs/promises";

const path = new URL(
  "../MyTransportAppWASM/wwwroot/appsettings.json",
  import.meta.url,
);
const source = await readFile(path, "utf8");
const firebaseSection = source.match(/("Firebase"\s*:\s*)(\{[^{}]*\})/);
if (!firebaseSection)
  throw new Error("Firebase section is missing from wwwroot/appsettings.json.");
const raw = process.env.FIREBASE_CONFIG_JSON ?? firebaseSection[2];
// Strip trailing backslashes that may appear from shell escaping or multi-line pasting.
const sanitized = raw.replace(/\\$/gm, "");
const config = JSON.parse(sanitized);
for (const key of ["apiKey", "authDomain", "projectId", "appId"]) {
  if (
    typeof config[key] !== "string" ||
    !config[key].trim() ||
    config[key].startsWith("YOUR_")
  ) {
    throw new Error(
      `Firebase ${key} is missing. Set the FIREBASE_CONFIG_JSON repository variable or configure the Firebase section in wwwroot/appsettings.json.`,
    );
  }
}
const publicConfig = Object.fromEntries(
  [
    "apiKey",
    "authDomain",
    "projectId",
    "appId",
    "storageBucket",
    "messagingSenderId",
    "measurementId",
    "googleClientId",
    "webPushVapidKey",
  ]
    .filter((key) => typeof config[key] === "string")
    .map((key) => [key, config[key]]),
);
const replacement = JSON.stringify(publicConfig, null, 2).replaceAll(
  "\n",
  "\n  ",
);
await writeFile(
  path,
  source.replace(firebaseSection[0], firebaseSection[1] + replacement),
);

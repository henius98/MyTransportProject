import { readFile, writeFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";

const PUBLIC_KEYS = [
  "apiKey",
  "authDomain",
  "projectId",
  "appId",
  "storageBucket",
  "messagingSenderId",
  "measurementId",
  "googleClientId",
  "webPushVapidKey",
];

const escapeRegExp = (value) => value.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
const trimBase = (value) => (value || "").trim().replace(/\/+$/, "");

// Edits appsettings.json text in place so inline // comments survive.
export function configureAppSettings(source, env = {}, devSettings = null) {
  const firebaseSection = source.match(/("Firebase"\s*:\s*)(\{[^{}]*\})/);
  if (!firebaseSection)
    throw new Error("Firebase section is missing from wwwroot/appsettings.json.");
  const raw = env.FIREBASE_CONFIG_JSON ?? firebaseSection[2];
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
    PUBLIC_KEYS.filter((key) => typeof config[key] === "string").map((key) => [key, config[key]]),
  );
  const replacement = JSON.stringify(publicConfig, null, 2).replaceAll("\n", "\n  ");
  let updated = source.replace(firebaseSection[0], () => firebaseSection[1] + replacement);

  // Static data slugs come from the Development template's local worker URLs.
  const staticBase = trimBase(env.STATIC_DATA_BASE_URL);
  if (staticBase && Array.isArray(devSettings?.TransportProviders)) {
    for (const provider of devSettings.TransportProviders) {
      const slug = (provider?.StaticData?.BaseUrl || "").split("/").filter(Boolean).pop();
      if (!slug || !provider.Name) continue;
      const pattern = new RegExp(
        `("Name"\\s*:\\s*${escapeRegExp(JSON.stringify(provider.Name))}(?:(?!"Name"\\s*:)[\\s\\S])*?"StaticData"\\s*:\\s*\\{[^{}]*?"BaseUrl"\\s*:\\s*)"[^"]*"`,
      );
      updated = updated.replace(pattern, (_, prefix) => `${prefix}${JSON.stringify(`${staticBase}/${slug}`)}`);
    }
  }

  const baziBase = trimBase(env.BAZIFLOW_BASE_URL);
  if (baziBase) {
    updated = updated.replace(
      /("BaziFlow"\s*:\s*\{[^{}]*?"BaseUrl"\s*:\s*)"[^"]*"/,
      (_, prefix) => `${prefix}${JSON.stringify(baziBase)}`,
    );
  }

  return updated;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const path = new URL("../MyTransportAppWASM/wwwroot/appsettings.json", import.meta.url);
  const devPath = new URL("../MyTransportAppWASM/wwwroot/appsettings.Development.example.json", import.meta.url);
  const source = await readFile(path, "utf8");
  const devSettings = process.env.STATIC_DATA_BASE_URL
    ? JSON.parse(await readFile(devPath, "utf8"))
    : null;
  await writeFile(path, configureAppSettings(source, process.env, devSettings));
}

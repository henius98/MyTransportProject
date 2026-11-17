import { getFirebaseApp, firebaseSdkBase } from "./firebase-config.js";
import { loadGoogleIdentity, requireSignedInUser } from "./firebase-auth.js";

let callablePromise = null;
const preparations = new Map();

export const serviceScopes = {
    calendar: [
        "https://www.googleapis.com/auth/calendar.calendarlist.readonly",
        "https://www.googleapis.com/auth/calendar.events"
    ],
    tasks: [
        "https://www.googleapis.com/auth/tasks"
    ]
};

async function invoke(uid, data) {
    await requireSignedInUser(uid);
    if (!callablePromise) {
        callablePromise = Promise.all([getFirebaseApp(), import(`${firebaseSdkBase}/firebase-functions.js`)])
            .then(([app, sdk]) => sdk.httpsCallable(sdk.getFunctions(app, "asia-southeast1"), "googleConnection"));
        void callablePromise.catch(() => { callablePromise = null; });
    }
    const callable = await callablePromise;
    await requireSignedInUser(uid);
    let result;
    try {
        result = await callable(data);
    } catch (err) {
        await requireSignedInUser(uid);
        if (err?.code === "functions/not-found" || err?.code === "functions/internal") {
            if (data.action === "get" || data.action === "invalidate") {
                return null;
            }
            throw new Error("The Google connection service is not deployed on Firebase. Set 'googleClientId' in appsettings or deploy the 'googleConnection' Cloud Function.");
        }
        if (err?.code === "functions/unavailable") {
            throw new Error("Google connection service is currently unavailable. Please try again later.");
        }
        throw err;
    }
    await requireSignedInUser(uid);
    return result?.data ?? null;
}

export function restoreConnection(uid, service, rejectedAccessToken = null) {
    return invoke(uid, { action: "get", service, rejectedAccessToken });
}

export function invalidateConnection(uid, service, rejectedAccessToken) {
    return invoke(uid, { action: "invalidate", service, rejectedAccessToken });
}

export function prepareAuthorization(uid, service) {
    const key = `${uid}/${service}`;
    if (!preparations.has(key)) {
        const pending = Promise.all([
            loadGoogleIdentity(),
            invoke(uid, { action: "configure", service }).catch(async (backendErr) => {
                const app = await getFirebaseApp();
                const clientId = app.options?.googleClientId?.trim();
                if (clientId && serviceScopes[service]) {
                    return { clientId, scopes: serviceScopes[service], clientSide: true };
                }
                throw backendErr;
            })
        ]);
        preparations.set(key, pending);
        void pending.catch(() => preparations.delete(key));
    }
    return preparations.get(key);
}

function requestGisConsent(init, extractValue, trigger) {
    return new Promise((resolve, reject) => {
        const client = init({
            callback: response => {
                const value = extractValue(response);
                if (value && !response?.error) {
                    resolve(value);
                } else {
                    reject(new Error("Google did not grant access. Connect again and allow the requested permissions."));
                }
            },
            error_callback: error => reject(new Error(error.type === "popup_failed_to_open"
                ? "Allow pop-ups for this site, then connect again."
                : "The Google permission window was closed. Connect again when you are ready."))
        });
        trigger(client);
    });
}

export async function authorizeConnection(uid, service, email, requireSession) {
    const [identity, configuration] = await prepareAuthorization(uid, service);
    requireSession();
    await requireSignedInUser(uid);
    const baseClientOptions = {
        client_id: configuration.clientId,
        scope: configuration.scopes.join(" "),
        login_hint: email,
        include_granted_scopes: true,
    };
    if (configuration?.clientSide) {
        const tokenResponse = await requestGisConsent(
            callbacks => identity.oauth2.initTokenClient({
                ...baseClientOptions,
                hint: email,
                prompt: "",
                ...callbacks,
            }),
            response => response.access_token ? response : null,
            client => client.requestAccessToken({ prompt: "" }),
        );
        requireSession();
        await requireSignedInUser(uid);
        const expiresIn = Number(tokenResponse.expires_in) || 3600;
        const grantedScopes = typeof tokenResponse.scope === "string"
            ? tokenResponse.scope.split(" ").filter(Boolean)
            : null;
        return {
            accessToken: tokenResponse.access_token,
            expiresAt: Date.now() + expiresIn * 1000,
            ...(grantedScopes?.length ? { scopes: grantedScopes } : {})
        };
    }

    const code = await requestGisConsent(
        callbacks => identity.oauth2.initCodeClient({
            ...baseClientOptions,
            ux_mode: "popup",
            ...callbacks,
        }),
        response => response.code,
        client => client.requestCode(),
    );
    requireSession();
    return invoke(uid, { action: "exchange", service, code });
}

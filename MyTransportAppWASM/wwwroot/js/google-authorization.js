import { getFirebaseApp, firebaseSdkBase } from "./firebase-config.js";
import { loadGoogleIdentity, requireSignedInUser } from "./firebase-auth.js";

let callablePromise = null;
const preparations = new Map();

async function invoke(uid, data) {
    await requireSignedInUser(uid);
    if (!callablePromise) {
        callablePromise = Promise.all([getFirebaseApp(), import(`${firebaseSdkBase}/firebase-functions.js`)])
            .then(([app, sdk]) => sdk.httpsCallable(sdk.getFunctions(app), "googleConnection"));
        void callablePromise.catch(() => { callablePromise = null; });
    }
    const callable = await callablePromise;
    await requireSignedInUser(uid);
    const result = await callable(data);
    await requireSignedInUser(uid);
    return result.data;
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
        const pending = Promise.all([loadGoogleIdentity(), invoke(uid, { action: "configure", service })]);
        preparations.set(key, pending);
        void pending.catch(() => preparations.delete(key));
    }
    return preparations.get(key);
}

export async function authorizeConnection(uid, service, email, requireSession) {
    const [identity, configuration] = await prepareAuthorization(uid, service);
    requireSession();
    await requireSignedInUser(uid);
    const code = await new Promise((resolve, reject) => {
        identity.oauth2.initCodeClient({
            client_id: configuration.clientId,
            scope: configuration.scopes.join(" "),
            login_hint: email,
            include_granted_scopes: true,
            ux_mode: "popup",
            callback: response => {
                if (response.code && !response.error) resolve(response.code);
                else reject(new Error("Google did not grant access. Connect again and allow the requested permissions."));
            },
            error_callback: error => reject(new Error(error.type === "popup_failed_to_open"
                ? "Allow pop-ups for this site, then connect again."
                : "The Google permission window was closed. Connect again when you are ready."))
        }).requestCode();
    });
    requireSession();
    return invoke(uid, { action: "exchange", service, code });
}

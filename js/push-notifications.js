import { getFirebaseApp, firebaseSdkBase } from "./firebase-config.js";
import { getAuthContext, requireSignedInUser } from "./firebase-auth.js";

let contextPromise;
let callablePromise;
let foregroundListener;

function supported() {
  return (
    isSecureContext && "serviceWorker" in navigator && "Notification" in window
  );
}

async function getContext() {
  if (!contextPromise) {
    const pending = Promise.all([
      getFirebaseApp(),
      import(`${firebaseSdkBase}/firebase-messaging.js`),
    ]).then(async ([app, sdk]) => {
      if (!(await sdk.isSupported()))
        throw new Error("Push notifications are unavailable in this browser.");
      const vapidKey = app.options.webPushVapidKey;
      if (!vapidKey || !app.options.messagingSenderId) {
        throw new Error("Web Push is not configured for this app yet.");
      }
      return { app, sdk, messaging: sdk.getMessaging(app), vapidKey };
    });
    contextPromise = pending;
    void pending.catch(() => {
      if (contextPromise === pending) contextPromise = null;
    });
  }
  return contextPromise;
}

async function updateRegistration(uid, action, token) {
  await requireSignedInUser(uid);
  if (!callablePromise) {
    const pending = Promise.all([
      getFirebaseApp(),
      import(`${firebaseSdkBase}/firebase-functions.js`),
    ]).then(([app, sdk]) =>
      sdk.httpsCallable(sdk.getFunctions(app), "pushRegistration"),
    );
    callablePromise = pending;
    void pending.catch(() => {
      if (callablePromise === pending) callablePromise = null;
    });
  }
  const callable = await callablePromise;
  await requireSignedInUser(uid);
  await callable({ action, token });
}

async function workerRegistration(app) {
  const { apiKey, appId, authDomain, messagingSenderId, projectId } =
    app.options;
  const config = { apiKey, appId, authDomain, messagingSenderId, projectId };
  const url = new URL("../firebase-messaging-sw.js", import.meta.url);
  url.searchParams.set("config", JSON.stringify(config));
  return navigator.serviceWorker.register(url.href, {
    scope: new URL("../", import.meta.url).pathname,
  });
}

async function listenInForeground(registration) {
  if (foregroundListener) return;
  const { sdk, messaging } = await getContext();
  foregroundListener = sdk.onMessage(messaging, (payload) => {
    const data = payload.data || payload.notification;
    if (!data?.title || !localStorage.getItem("push-token")) return;
    const scope = registration.scope;
    const requested = new URL(data.url || scope, scope);
    const url =
      requested.origin === location.origin && requested.href.startsWith(scope)
        ? requested.href
        : scope;
    void registration.showNotification(data.title, {
      body: data.body || "",
      icon: new URL("myTransport.svg", scope).href,
      data: { url },
    });
  });
}

window.pushNotifications = {
  status: async (uid) => {
    if (!supported()) return "unsupported";
    if (Notification.permission === "denied") return "denied";
    const registration = await navigator.serviceWorker.getRegistration(
      new URL("../", import.meta.url).href,
    );
    if (!registration || !(await registration.pushManager.getSubscription()))
      return "disabled";
    if (uid) {
      try {
        const { app, sdk, messaging, vapidKey } = await getContext();
        const current = await workerRegistration(app);
        const token = await sdk.getToken(messaging, {
          vapidKey,
          serviceWorkerRegistration: current,
        });
        if (token) {
          await updateRegistration(uid, "add", token);
          const old = localStorage.getItem("push-token");
          if (old && old !== token)
            await updateRegistration(uid, "remove", old);
          localStorage.setItem("push-token", token);
          await listenInForeground(current);
        }
      } catch (error) {
        console.warn("Could not refresh push registration:", error.message);
      }
    }
    return "enabled";
  },
  enable: async (uid) => {
    if (!supported())
      throw new Error(
        "Push notifications require HTTPS and a supported browser.",
      );
    // Start the permission prompt while the user click still has browser activation.
    const permissionPromise = Notification.requestPermission();
    const { auth } = await getAuthContext();
    if (auth.currentUser?.uid !== uid)
      throw new Error("Sign in to enable notifications.");
    const permission = await permissionPromise;
    if (permission !== "granted")
      throw new Error(
        "Allow notifications in your browser settings, then try again.",
      );
    const { app, sdk, messaging, vapidKey } = await getContext();
    const registration = await workerRegistration(app);
    const token = await sdk.getToken(messaging, {
      vapidKey,
      serviceWorkerRegistration: registration,
    });
    if (!token)
      throw new Error("This device could not subscribe to push notifications.");
    await updateRegistration(uid, "add", token);
    localStorage.setItem("push-token", token);
    await listenInForeground(registration);
    return true;
  },
  disable: async (uid) => {
    const token = localStorage.getItem("push-token");
    const registration = supported()
      ? await navigator.serviceWorker.getRegistration(
          new URL("../", import.meta.url).href,
        )
      : null;
    const subscription = registration
      ? await registration.pushManager.getSubscription()
      : null;
    if (!token && !subscription) return true;
    let unsubscribed = false;
    let removed = false;
    let failure;
    if (supported()) {
      try {
        const { sdk, messaging } = await getContext();
        unsubscribed = await sdk.deleteToken(messaging);
      } catch (error) {
        failure = error;
      }
    }
    if (!unsubscribed && subscription) {
      try {
        unsubscribed = await subscription.unsubscribe();
      } catch (error) {
        failure = error;
      }
    }
    if (token && uid) {
      try {
        await updateRegistration(uid, "remove", token);
        removed = true;
      } catch (error) {
        failure = error;
      }
    }
    if (!unsubscribed && !removed)
      throw failure || new Error("Could not turn off notifications.");
    localStorage.removeItem("push-token");
    return true;
  },
};

if (
  supported() &&
  Notification.permission === "granted" &&
  localStorage.getItem("push-token")
) {
  void (async () => {
    const registration = await navigator.serviceWorker.getRegistration(
      new URL("../", import.meta.url).href,
    );
    if (registration) await listenInForeground(registration);
  })().catch((error) =>
    console.warn("Foreground notifications are unavailable:", error.message),
  );
}

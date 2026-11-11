// Firebase configuration is passed from the same appsettings used by the page.
// Register this worker only after the user enables notifications.
function resolveTargetUrl(rawUrl) {
  const scope = self.registration.scope;
  const requested = new URL(rawUrl || scope, scope);
  return requested.origin === self.location.origin && requested.href.startsWith(scope)
    ? requested.href : scope;
}

self.addEventListener("notificationclick", event => {
  event.notification.close();
  const target = resolveTargetUrl(event.notification.data?.url);

  event.waitUntil((async () => {
    const windows = await clients.matchAll({ type: "window", includeUncontrolled: true });
    const existing = windows.find(client => client.url === target);
    if (existing) return existing.focus();
    return clients.openWindow(target);
  })());
});

const params = new URL(self.location.href).searchParams;
const config = JSON.parse(params.get("config"));
const sdkBase = params.get("sdkBase") || "https://www.gstatic.com/firebasejs/11.4.0";
importScripts(`${sdkBase}/firebase-app-compat.js`);
importScripts(`${sdkBase}/firebase-messaging-compat.js`);
firebase.initializeApp(config);

firebase.messaging().onBackgroundMessage(payload => {
  // FCM displays notification payloads itself. Data-only messages need this display.
  if (payload.notification || !payload.data?.title) return;
  const url = resolveTargetUrl(payload.data.url);
  return self.registration.showNotification(payload.data.title, {
    body: payload.data.body || "",
    icon: new URL("myTransport.svg", self.registration.scope).href,
    data: { url }
  });
});

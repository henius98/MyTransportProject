// Firebase configuration is passed from the same appsettings used by the page.
// Register this worker only after the user enables notifications.
self.addEventListener("notificationclick", event => {
  event.notification.close();
  const scope = self.registration.scope;
  const requested = new URL(event.notification.data?.url || scope, scope);
  const target = requested.origin === self.location.origin && requested.href.startsWith(scope)
    ? requested.href : scope;

  event.waitUntil((async () => {
    const windows = await clients.matchAll({ type: "window", includeUncontrolled: true });
    const existing = windows.find(client => client.url === target);
    if (existing) return existing.focus();
    return clients.openWindow(target);
  })());
});

const config = JSON.parse(new URL(self.location.href).searchParams.get("config"));
importScripts("https://www.gstatic.com/firebasejs/10.14.1/firebase-app-compat.js");
importScripts("https://www.gstatic.com/firebasejs/10.14.1/firebase-messaging-compat.js");
firebase.initializeApp(config);

firebase.messaging().onBackgroundMessage(payload => {
  // FCM displays notification payloads itself. Data-only messages need this display.
  if (payload.notification || !payload.data?.title) return;
  const scope = self.registration.scope;
  const requested = new URL(payload.data.url || scope, scope);
  const url = requested.origin === self.location.origin && requested.href.startsWith(scope)
    ? requested.href : scope;
  return self.registration.showNotification(payload.data.title, {
    body: payload.data.body || "",
    icon: new URL("myTransport.svg", scope).href,
    data: { url }
  });
});

// Firebase configuration is pushed from Blazor IConfiguration via configureFirebase(config).
export const firebaseSdkBase = "https://www.gstatic.com/firebasejs/11.4.0";
let configuredConfig = null;
let appPromise = null;

export function configureFirebase(config) {
  configuredConfig = config && typeof config === "object" ? { ...config } : null;
}

export async function getFirebaseApp() {
  if (!appPromise) {
    const pending = (async () => {
      const config = configuredConfig || {};
      if (!["apiKey", "authDomain", "projectId", "appId"].every(
        key => typeof config[key] === "string" && config[key] && !config[key].startsWith("YOUR_"))) {
        throw new Error("Google sign-in has not been configured for this app yet.");
      }
      const { initializeApp } = await import(`${firebaseSdkBase}/firebase-app.js`);
      return initializeApp(config);
    })();
    appPromise = pending;
    void pending.catch(() => {
      if (appPromise === pending) appPromise = null;
    });
  }

  return appPromise;
}

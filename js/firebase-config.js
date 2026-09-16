// Set Firebase in appsettings.json (also used by deployment), with overrides in appsettings.{Environment}.json.
export const firebaseSdkBase = "https://www.gstatic.com/firebasejs/10.14.1";
let appPromise = null;

export async function getFirebaseApp() {
  if (!appPromise) {
    const pending = (async () => {
      const { applicationEnvironment, appsettings } = window.Blazor.runtime.getConfig();
      const environmentFile = `appsettings.${applicationEnvironment}.json`;
      const files = ["appsettings.json"];
      if (appsettings?.some(path => path.split("/").pop() === environmentFile)) {
        files.push(environmentFile);
      }
      const settings = await Promise.all(files.map(async file => {
        const response = await fetch(new URL(`../${file}`, import.meta.url), { cache: "no-cache" });
        if (!response.ok) throw new Error("Could not load sign-in configuration.");
        return (await response.json()).Firebase;
      }));
      const config = Object.assign({}, ...settings);
      if (!["apiKey", "authDomain", "projectId", "appId"].every(
        key => typeof config[key] === "string" && config[key] && !config[key].startsWith("YOUR_"))) {
        throw new Error("Google sign-in has not been configured for this app yet.");
      }
      const { initializeApp } = await import(`${firebaseSdkBase}/firebase-app.js`);
      const app = initializeApp(config);
      window.firebaseApp = app;
      return app;
    })();
    appPromise = pending;
    void pending.catch(() => {
      if (appPromise === pending) appPromise = null;
    });
  }

  return appPromise;
}

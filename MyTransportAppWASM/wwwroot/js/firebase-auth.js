import { getFirebaseApp, firebaseSdkBase } from "./firebase-config.js";
import { clearConnections } from "./google-token-store.js";

let unsubscribeAuthStateChanged = null;
let authContextPromise = null;
let oneTapStarted = false;
let oneTapCancelled = false;
let googleIdentity = null;
let googleIdentityPromise = null;

export async function loadGoogleIdentity() {
  if (window.google?.accounts) return window.google.accounts;
  if (!googleIdentityPromise) {
    googleIdentityPromise = new Promise((resolve, reject) => {
      const script = document.createElement("script");
      script.src = "https://accounts.google.com/gsi/client";
      script.async = true;
      script.onload = () => resolve(window.google.accounts);
      script.onerror = () =>
        reject(
          new Error("Google authorization could not load. Please try again."),
        );
      document.head.appendChild(script);
    });
    void googleIdentityPromise.catch(() => {
      googleIdentityPromise = null;
    });
  }
  return googleIdentityPromise;
}

function cancelOneTap() {
  oneTapCancelled = true;
  googleIdentity?.cancel();
}

async function initializeOneTap(clientId) {
  if (oneTapStarted || oneTapCancelled || !clientId?.trim()) return;
  oneTapStarted = true;

  try {
    const { auth, sdk } = await getAuthContext();
    await auth.authStateReady();
    if (auth.currentUser || oneTapCancelled) return;

    // Load Google's UI only after Firebase has restored the visitor's session.
    const identity = await loadGoogleIdentity();
    if (auth.currentUser || oneTapCancelled) return;

    googleIdentity = identity.id;
    googleIdentity.initialize({
      client_id: clientId.trim(),
      auto_select: false,
      callback: async ({ credential }) => {
        if (auth.currentUser || oneTapCancelled) return;
        oneTapCancelled = true;
        try {
          await sdk.signInWithCredential(
            auth,
            sdk.GoogleAuthProvider.credential(credential),
          );
        } catch (error) {
          console.warn(
            "Google One Tap sign-in failed. Use the sign-in button to try again.",
            error.code,
          );
        }
      },
    });
    googleIdentity.prompt();
  } catch (error) {
    console.warn(
      "Google One Tap is unavailable. The sign-in button is still available.",
      error.message,
    );
  }
}

export async function getAuthContext() {
  if (!authContextPromise) {
    const pending = Promise.all([
      getFirebaseApp(),
      import(`${firebaseSdkBase}/firebase-auth.js`),
    ]).then(([app, sdk]) => {
      const auth = sdk.getAuth(app);
      let previousUid;
      // Clear saved Google access even when no Calendar or Tasks page has been opened.
      sdk.onAuthStateChanged(auth, (user) => {
        const uid = user?.uid ?? null;
        if (uid) cancelOneTap();
        if (!uid || (previousUid !== undefined && previousUid !== uid))
          clearConnections();
        previousUid = uid;
      });
      const provider = new sdk.GoogleAuthProvider();
      provider.setCustomParameters({ prompt: "select_account" });
      return { auth, provider, sdk };
    });
    authContextPromise = pending;
    void pending.catch(() => {
      if (authContextPromise === pending) authContextPromise = null;
    });
  }

  return authContextPromise;
}

function userProfile(user) {
  return user
    ? {
        uid: user.uid,
        email: user.email,
        displayName: user.displayName,
        photoURL: user.photoURL,
      }
    : null;
}

export async function requireSignedInUser(uid) {
  const { auth } = await getAuthContext();
  if (!uid || auth.currentUser?.uid !== uid) {
    throw new Error("The signed-in account has changed. Please retry.");
  }
}

window.firebaseAuthInterop = {
  initializeOneTap,
  cancelOneTap,
  signInWithGoogle: async function () {
    try {
      cancelOneTap();
      const { auth, provider, sdk } = await getAuthContext();
      const result = await sdk.signInWithPopup(auth, provider);
      // Result includes credential and user info; only send the profile to Blazor.
      return userProfile(result.user);
    } catch (error) {
      console.error("Firebase Auth Error:", error);
      throw error;
    }
  },
  signOut: async function () {
    try {
      cancelOneTap();
      googleIdentity?.disableAutoSelect();
      const { auth, sdk } = await getAuthContext();
      if (auth.currentUser && window.pushNotifications) {
        await window.pushNotifications.disable(auth.currentUser.uid);
      }
      await sdk.signOut(auth);
      clearConnections();
      return true;
    } catch (error) {
      console.error("Firebase SignOut Error:", error);
      throw error;
    }
  },
  // Allows Blazor to listen to auth state changes
  onAuthStateChanged: async function (dotNetHelper) {
    const { auth, sdk } = await getAuthContext();

    if (unsubscribeAuthStateChanged) {
      unsubscribeAuthStateChanged();
    }

    unsubscribeAuthStateChanged = sdk.onAuthStateChanged(auth, (user) => {
      // Pass basic user info to Blazor
      void dotNetHelper
        .invokeMethodAsync("OnAuthStateChanged", userProfile(user))
        .catch((error) =>
          console.error("Could not update the sign-in display:", error),
        );
    });
  },
  disposeAuthStateChanged: function () {
    cancelOneTap();
    if (unsubscribeAuthStateChanged) {
      unsubscribeAuthStateChanged();
      unsubscribeAuthStateChanged = null;
    }
  },
};

import { getFirebaseApp, firebaseSdkBase } from "./firebase-config.js";

let unsubscribeAuthStateChanged = null;
let authContextPromise = null;

export async function getAuthContext() {
    if (!authContextPromise) {
        const pending = Promise.all([
            getFirebaseApp(),
            import(`${firebaseSdkBase}/firebase-auth.js`)
        ]).then(([app, sdk]) => {
            const provider = new sdk.GoogleAuthProvider();
            provider.setCustomParameters({ prompt: "select_account" });
            return { auth: sdk.getAuth(app), provider, sdk };
        });
        authContextPromise = pending;
        void pending.catch(() => {
            if (authContextPromise === pending) authContextPromise = null;
        });
    }

    return authContextPromise;
}

function userProfile(user) {
    return user ? {
        uid: user.uid,
        email: user.email,
        displayName: user.displayName,
        photoURL: user.photoURL
    } : null;
}

export async function requireSignedInUser(uid) {
    const { auth } = await getAuthContext();
    if (!uid || auth.currentUser?.uid !== uid) {
        throw new Error("The signed-in account has changed. Please retry.");
    }
}

window.firebaseAuthInterop = {
    signInWithGoogle: async function () {
        try {
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
            const { auth, sdk } = await getAuthContext();
            await sdk.signOut(auth);
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
            void dotNetHelper.invokeMethodAsync('OnAuthStateChanged', userProfile(user))
                .catch(error => console.error("Could not update the sign-in display:", error));
        });
    },
    disposeAuthStateChanged: function () {
        if (unsubscribeAuthStateChanged) {
            unsubscribeAuthStateChanged();
            unsubscribeAuthStateChanged = null;
        }
    }
};

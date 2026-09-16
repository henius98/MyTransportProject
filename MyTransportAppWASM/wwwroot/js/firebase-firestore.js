import { getFirebaseApp, firebaseSdkBase } from "./firebase-config.js";
import { requireSignedInUser } from "./firebase-auth.js";

let firestoreContextPromise = null;

async function getFirestoreContext() {
    if (!firestoreContextPromise) {
        const pending = Promise.all([
            getFirebaseApp(),
            import(`${firebaseSdkBase}/firebase-firestore.js`)
        ]).then(([app, sdk]) => ({ db: sdk.getFirestore(app), sdk }));
        firestoreContextPromise = pending;
        void pending.catch(() => {
            if (firestoreContextPromise === pending) firestoreContextPromise = null;
        });
    }

    return firestoreContextPromise;
}

window.firebaseFirestoreInterop = {
    getUserSettings: async function (uid) {
        try {
            const { db, sdk } = await getFirestoreContext();
            await requireSignedInUser(uid);
            const docRef = sdk.doc(db, "users", uid, "data", "settings");
            const docSnap = await sdk.getDoc(docRef);

            if (docSnap.exists()) {
                return docSnap.data();
            } else {
                return null;
            }
        } catch (error) {
            console.error("Firestore get error:", error);
            throw error;
        }
    },
    setUserSettings: async function (uid, settings) {
        try {
            const { db, sdk } = await getFirestoreContext();
            await requireSignedInUser(uid);
            const docRef = sdk.doc(db, "users", uid, "data", "settings");
            await sdk.setDoc(docRef, settings, { merge: true });
            return true;
        } catch (error) {
            console.error("Firestore set error:", error);
            throw error;
        }
    }
};

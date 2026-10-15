import { getFirebaseApp, firebaseSdkBase } from "./firebase-config.js";
import { requireSignedInUser } from "./firebase-auth.js";

let contextPromise;

async function context(uid) {
    if (!contextPromise) {
        const pending = Promise.all([getFirebaseApp(), import(`${firebaseSdkBase}/firebase-firestore.js`)])
            .then(([app, sdk]) => ({ db: sdk.getFirestore(app), sdk }));
        contextPromise = pending;
        void pending.catch(() => { if (contextPromise === pending) contextPromise = null; });
    }
    const result = await contextPromise;
    await requireSignedInUser(uid);
    return result;
}

function documentId({ taskListId, taskId }) {
    const bytes = new TextEncoder().encode(JSON.stringify([taskListId, taskId]));
    return btoa(String.fromCharCode(...bytes)).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", "");
}

function extensionCollection({ db, sdk }, uid) {
    return sdk.collection(db, "users", uid, "taskExtensions");
}

export async function listTaskExtensions(uid) {
    const ctx = await context(uid);
    const snapshot = await ctx.sdk.getDocs(extensionCollection(ctx, uid));
    await requireSignedInUser(uid);
    return snapshot.docs.map(item => item.data());
}

export async function saveTaskExtension(uid, extension) {
    const ctx = await context(uid);
    const ref = ctx.sdk.doc(extensionCollection(ctx, uid), documentId(extension));
    const data = {
        taskListId: extension.taskListId,
        taskId: extension.taskId,
        estimatedMinutes: extension.estimatedMinutes,
        dependsOn: extension.dependsOn,
        startedAt: extension.startedAt
    };
    if (data.estimatedMinutes == null && data.dependsOn.length === 0 && data.startedAt == null)
        await ctx.sdk.deleteDoc(ref);
    else
        await ctx.sdk.setDoc(ref, data);
    await requireSignedInUser(uid);
}

export async function deleteTaskExtensions(uid, deleted) {
    const ctx = await context(uid);
    const snapshot = await ctx.sdk.getDocs(extensionCollection(ctx, uid));
    await requireSignedInUser(uid);
    const changes = planTaskExtensionDeletion(snapshot.docs, deleted);
    for (let offset = 0; offset < changes.length; offset += 500) {
        const batch = ctx.sdk.writeBatch(ctx.db);
        for (const change of changes.slice(offset, offset + 500)) {
            if (change.remove) batch.delete(change.ref);
            else batch.update(change.ref, { dependsOn: change.dependsOn });
        }
        await requireSignedInUser(uid);
        await batch.commit();
    }
    await requireSignedInUser(uid);
}

export function planTaskExtensionDeletion(documents, deleted) {
    const deletedIds = new Set(deleted.map(documentId));
    const changes = [];
    for (const item of documents) {
        if (deletedIds.has(documentId(item.data()))) {
            changes.push({ ref: item.ref, remove: true });
            continue;
        }
        const data = item.data();
        const dependsOn = (data.dependsOn ?? []).filter(reference => !deletedIds.has(documentId(reference)));
        if (dependsOn.length !== (data.dependsOn ?? []).length) {
            if (dependsOn.length === 0 && data.estimatedMinutes == null && data.startedAt == null)
                changes.push({ ref: item.ref, remove: true });
            else changes.push({ ref: item.ref, dependsOn });
        }
    }
    return changes;
}

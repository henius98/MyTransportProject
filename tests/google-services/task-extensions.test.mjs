import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, SourceTextModule, SyntheticModule } from "node:vm";

const source = await readFile(new URL("../../MyTransportAppWASM/wwwroot/js/firebase-task-extensions.js", import.meta.url), "utf8");
const context = createContext({ TextEncoder, btoa });
const writes = [];
const batches = [];
let documents = [];
const db = {};
const sdk = {
    getFirestore: () => db,
    collection: (_db, ...path) => path,
    doc: (path, id) => ({ path, id }),
    getDocs: async () => ({ docs: documents }),
    writeBatch: () => {
        const operations = [];
        return {
            delete: ref => operations.push({ operation: "delete", ref }),
            update: (ref, data) => operations.push({ operation: "update", ref, data }),
            commit: async () => { batches.push(operations); }
        };
    },
    setDoc: async (ref, data) => { writes.push({ operation: "set", ref, data }); },
    deleteDoc: async ref => { writes.push({ operation: "delete", ref }); }
};
const firestore = new SyntheticModule(Object.keys(sdk), function () {
    for (const [key, value] of Object.entries(sdk)) this.setExport(key, value);
}, { context });
await firestore.link(() => {});
await firestore.evaluate();
const module = new SourceTextModule(source, { context, importModuleDynamically: async () => firestore });
await module.link(specifier => new SyntheticModule(
    specifier === "./firebase-config.js" ? ["getFirebaseApp", "firebaseSdkBase"] : ["requireSignedInUser"],
    function () {
        if (specifier === "./firebase-config.js") {
            this.setExport("getFirebaseApp", async () => ({}));
            this.setExport("firebaseSdkBase", "unused");
        } else this.setExport("requireSignedInUser", async () => {});
    }, { context }
));
await module.evaluate();

test("saves only extension fields under the signed-in account and deletes empty extensions", async () => {
    const extension = {
        taskListId: "list", taskId: "task", estimatedMinutes: 90,
        dependsOn: [{ taskListId: "list", taskId: "before" }], startedAt: "2026-09-23T10:00:00Z"
    };
    await module.namespace.saveTaskExtension("alice", extension);
    assert.equal(writes[0].operation, "set");
    assert.deepEqual(JSON.parse(JSON.stringify(writes[0].ref.path)), ["users", "alice", "taskExtensions"]);
    assert.deepEqual(JSON.parse(JSON.stringify(writes[0].data)), extension);
    await module.namespace.saveTaskExtension("alice", { ...extension, estimatedMinutes: null, dependsOn: [], startedAt: null });
    assert.equal(writes[1].operation, "delete");
    assert.equal(writes[1].ref.id, writes[0].ref.id);
});

test("deletion removes the task and subtasks and clears dependencies in every list", () => {
    const ref = (taskListId, taskId) => ({ taskListId, taskId });
    const documents = [
        { ref: "a", data: () => ({ ...ref("list", "a"), dependsOn: [] }) },
        { ref: "child", data: () => ({ ...ref("list", "child"), dependsOn: [ref("list", "a")] }) },
        { ref: "c", data: () => ({ ...ref("list", "c"), dependsOn: [ref("list", "a"), ref("other", "b")] }) },
        { ref: "d", data: () => ({ ...ref("other", "d"), dependsOn: [ref("list", "child")] }) },
        { ref: "unrelated", data: () => ({ ...ref("other", "b"), dependsOn: [] }) }
    ];
    const changes = module.namespace.planTaskExtensionDeletion(documents, [ref("list", "a"), ref("list", "child")]);
    assert.deepEqual(JSON.parse(JSON.stringify(changes)), [
        { ref: "a", remove: true },
        { ref: "child", remove: true },
        { ref: "c", dependsOn: [ref("other", "b")] },
        { ref: "d", remove: true }
    ]);
});

test("deletion commits dependent updates and removes empty extension documents", async () => {
    const ref = (taskListId, taskId) => ({ taskListId, taskId });
    documents = [
        { ref: "deleted", data: () => ({ ...ref("list", "a"), dependsOn: [] }) },
        { ref: "dependent", data: () => ({ ...ref("other", "b"), estimatedMinutes: 45, startedAt: null, dependsOn: [ref("list", "a")] }) },
        { ref: "empty", data: () => ({ ...ref("list", "c"), estimatedMinutes: null, startedAt: null, dependsOn: [ref("list", "a")] }) }
    ];
    await module.namespace.deleteTaskExtensions("alice", [ref("list", "a")]);
    assert.deepEqual(JSON.parse(JSON.stringify(batches.at(-1))), [
        { operation: "delete", ref: "deleted" },
        { operation: "update", ref: "dependent", data: { dependsOn: [] } },
        { operation: "delete", ref: "empty" }
    ]);
});

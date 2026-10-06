import { readFile } from "node:fs/promises";
import { before, after, beforeEach, test } from "node:test";
import {
  initializeTestEnvironment,
  assertSucceeds,
  assertFails,
} from "@firebase/rules-unit-testing";
import { doc, getDoc, setDoc, deleteDoc } from "firebase/firestore";

let environment;
const settings = (db) => doc(db, "users/alice/data/settings");

before(async () => {
  environment = await initializeTestEnvironment({
    projectId: "demo-mytransport",
    firestore: {
      rules: await readFile(
        new URL("../../firestore.rules", import.meta.url),
        "utf8",
      ),
    },
  });
});
beforeEach(async () => environment.clearFirestore());
after(async () => environment?.cleanup());

test("owner can create, read, merge, and delete preferences", async () => {
  const ref = settings(environment.authenticatedContext("alice").firestore());
  await assertSucceeds(
    setDoc(ref, {
      theme: "light",
      language: "ms-MY",
      hasSeenWelcome: true,
      defaultLocation: { latitude: 3.139, longitude: 101.6869 },
    }),
  );
  await assertSucceeds(getDoc(ref));
  await assertSucceeds(setDoc(ref, { theme: "dark" }, { merge: true }));
  await assertSucceeds(deleteDoc(ref));
});

test("anonymous users and other accounts cannot access preferences", async () => {
  for (const context of [
    environment.unauthenticatedContext(),
    environment.authenticatedContext("bob"),
  ]) {
    const ref = settings(context.firestore());
    await assertFails(getDoc(ref));
    await assertFails(setDoc(ref, { theme: "light" }));
    await assertFails(deleteDoc(ref));
  }
});

test("owners cannot grant themselves roles or write outside preferences", async () => {
  const db = environment.authenticatedContext("alice").firestore();
  await assertFails(setDoc(settings(db), { theme: "dark", role: "admin" }));
  await assertFails(setDoc(doc(db, "users/alice"), { role: "admin" }));
  await assertFails(
    setDoc(doc(db, "users/alice/data/private"), { value: "anything" }),
  );
});

test("invalid preference types and coordinates are rejected", async () => {
  const ref = settings(environment.authenticatedContext("alice").firestore());
  for (const invalid of [
    { theme: "rainbow" },
    { language: "unknown" },
    { hasSeenWelcome: "yes" },
    { defaultLocation: { latitude: 91, longitude: 0 } },
    { defaultLocation: { latitude: 0, longitude: 181 } },
    { defaultLocation: { latitude: "3", longitude: 100 } },
    { defaultLocation: { latitude: 3 } },
    { defaultLocation: { latitude: 3, longitude: 100, extra: true } },
  ])
    await assertFails(setDoc(ref, invalid));
});

test("optional preference fields can be omitted or null", async () => {
  const ref = settings(environment.authenticatedContext("alice").firestore());
  await assertSucceeds(setDoc(ref, { language: "en-US" }));
  await assertSucceeds(
    setDoc(ref, {
      theme: null,
      language: null,
      hasSeenWelcome: null,
      defaultLocation: null,
    }),
  );
});

import {createHash, timingSafeEqual} from "node:crypto";
import {HttpsError} from "firebase-functions/https";

const devices = (db) => db.collection("pushDevices");
const deviceId = (token) => createHash("sha256").update(token).digest("hex");

export async function handlePushRegistration(request, {db}) {
  if (!request.auth?.uid) {
    throw new HttpsError("unauthenticated", "Sign in to manage notifications.");
  }
  const {action, token} = request.data ?? {};
  if (
    !["add", "remove"].includes(action) ||
    typeof token !== "string" ||
    token.length < 20 ||
    token.length > 4096
  ) {
    throw new HttpsError("invalid-argument", "Invalid push registration.");
  }

  const ref = devices(db).doc(deviceId(token));
  if (action === "add") {
    await ref.set({uid: request.auth.uid, token, updatedAt: Date.now()});
  } else {
    const saved = await ref.get();
    if (saved.exists && saved.data().uid === request.auth.uid) {
      await ref.delete();
    }
  }
  return {ok: true};
}

export async function handlePushWebhook(
    request,
    response,
    {db, messaging, secret},
) {
  if (request.method !== "POST") {
    return response.status(405).json({error: "Method not allowed"});
  }
  const authorization = request.get("authorization");
  const actual = Buffer.from(
    authorization?.startsWith("Bearer ") ? authorization.slice(7) : "",
  );
  const expected = Buffer.from(secret);
  if (
    !expected.length ||
    actual.length !== expected.length ||
    !timingSafeEqual(actual, expected)
  ) {
    return response.status(401).json({error: "Unauthorized"});
  }

  const {uid, title, body, url = ""} = request.body ?? {};
  if (
    typeof uid !== "string" ||
    !/^[A-Za-z0-9_-]{1,128}$/.test(uid) ||
    typeof title !== "string" ||
    !title.trim() ||
    title.length > 120 ||
    typeof body !== "string" ||
    body.length > 500 ||
    typeof url !== "string" ||
    url.length > 512 ||
    url.startsWith("//") ||
    /^[a-z]+:/i.test(url)
  ) {
    return response.status(400).json({error: "Invalid notification"});
  }

  const snapshot = await devices(db).where("uid", "==", uid).limit(500).get();
  if (snapshot.empty) return response.status(200).json({sent: 0, failed: 0});
  const registrations = snapshot.docs;
  const result = await messaging.sendEachForMulticast({
    tokens: registrations.map((doc) => doc.data().token),
    data: {title: title.trim(), body, url},
  });
  await Promise.all(
      result.responses.map((item, index) => {
        if (
          [
            "messaging/registration-token-not-registered",
            "messaging/invalid-registration-token",
          ].includes(item.error?.code)
        ) {
          return registrations[index].ref.delete();
        }
      }),
  );
  return response
      .status(200)
      .json({sent: result.successCount, failed: result.failureCount});
}

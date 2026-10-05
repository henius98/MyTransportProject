import {HttpsError} from "firebase-functions/https";

const scopes = {
  calendar: [
    "https://www.googleapis.com/auth/calendar.calendarlist.readonly",
    "https://www.googleapis.com/auth/calendar.events",
  ],
  tasks: ["https://www.googleapis.com/auth/tasks"],
};

function publicConnection(record) {
  return record && !record.requiresConsent ?
    {accessToken: record.accessToken, expiresAt: record.expiresAt} :
    null;
}

async function tokenRequest(parameters, dependencies) {
  let response;
  let tokens;
  try {
    response = await dependencies.fetch("https://oauth2.googleapis.com/token", {
      method: "POST",
      headers: {"Content-Type": "application/x-www-form-urlencoded"},
      body: new URLSearchParams({
        client_id: dependencies.clientId,
        client_secret: dependencies.clientSecret,
        ...parameters,
      }).toString(),
      signal: AbortSignal.timeout(15000),
    });
    tokens = await response.json();
  } catch {
    throw new HttpsError(
        "unavailable",
        "Google could not be reached. Please try again.",
    );
  }
  if (!response.ok) {
    if (response.status === 400 && tokens.error === "invalid_grant") {
      return null;
    }
    throw new HttpsError(
        "unavailable",
        "Google could not renew access. Please try again later.",
    );
  }
  if (
    typeof tokens.access_token !== "string" ||
    !tokens.access_token ||
    !Number.isFinite(tokens.expires_in) ||
    tokens.expires_in <= 60
  ) {
    throw new HttpsError(
        "unavailable",
        "Google returned an incomplete token response. Please try again.",
    );
  }
  return tokens;
}

async function replaceIfUnchanged(db, ref, snapshot, replacement) {
  return db.runTransaction(async (transaction) => {
    const latest = await transaction.get(ref);
    // A delayed refresh or rejection must not overwrite a newer connection from another tab or device.
    if (
      latest.exists !== snapshot.exists ||
      (latest.exists && !latest.updateTime.isEqual(snapshot.updateTime))
    ) {
      return latest.data() ?? null;
    }
    if (replacement) transaction.set(ref, replacement);
    else transaction.delete(ref);
    return replacement;
  });
}

export async function handleGoogleConnection(request, dependencies) {
  const {db, auth, clientId, allowedOrigins, now} = dependencies;
  if (!request.auth) {
    throw new HttpsError(
        "unauthenticated",
        "Sign in to connect Google services.",
    );
  }
  const origin = request.rawRequest.get("origin");
  if (!allowedOrigins.includes(origin)) {
    throw new HttpsError(
        "permission-denied",
        "This site's origin is not allowed to connect Google services.",
    );
  }
  const {action, service, code, rejectedAccessToken} = request.data ?? {};
  if (
    !Object.hasOwn(scopes, service) ||
    !["configure", "exchange", "get", "invalidate"].includes(action)
  ) {
    throw new HttpsError(
        "invalid-argument",
        "This Google connection request is not supported.",
    );
  }
  const user = await auth.getUser(request.auth.uid);
  const googleSubject = user.providerData.find(
      (provider) => provider.providerId === "google.com",
  )?.uid;
  if (user.disabled || !googleSubject) {
    throw new HttpsError(
        "permission-denied",
        "Sign in with a Google account to connect this service.",
    );
  }
  if (action === "configure") {
    return {clientId, scopes: ["openid", ...scopes[service]]};
  }

  // The verified Firebase identity determines the document path; never accept a UID supplied by the browser.
  const ref = db.doc(`users/${request.auth.uid}/googleConnections/${service}`);
  const snapshot = await ref.get();
  const saved = snapshot.data();
  if (action === "exchange") {
    if (typeof code !== "string" || !code || code.length > 4096) {
      throw new HttpsError(
          "invalid-argument",
          "A Google authorization code is required.",
      );
    }
    const issuedAt = now();
    const tokens = await tokenRequest(
        {grant_type: "authorization_code", code, redirect_uri: origin},
        dependencies,
    );
    if (!tokens) {
      throw new HttpsError(
          "failed-precondition",
          "The Google permission window expired. Connect again.",
      );
    }
    const grantedScopes = (tokens.scope ?? "").split(" ");
    if (!scopes[service].every((scope) => grantedScopes.includes(scope))) {
      throw new HttpsError(
          "permission-denied",
          "Google did not grant all requested permissions. Connect again and allow access.",
      );
    }
    let profile;
    try {
      const response = await dependencies.fetch(
          "https://openidconnect.googleapis.com/v1/userinfo",
          {
            headers: {Authorization: `Bearer ${tokens.access_token}`},
            signal: AbortSignal.timeout(15000),
          },
      );
      if (!response.ok) throw new Error("Google profile unavailable");
      profile = await response.json();
    } catch {
      throw new HttpsError(
          "unavailable",
          "Could not verify the connected Google account. Please try again.",
      );
    }
    if (profile.sub !== googleSubject) {
      throw new HttpsError(
          "permission-denied",
          "Choose the same Google account you used to sign in to this app.",
      );
    }
    const refreshToken =
      tokens.refresh_token ||
      (saved?.googleSubject === googleSubject ? saved.refreshToken : null);
    if (!refreshToken) {
      throw new HttpsError(
          "failed-precondition",
          "Google did not return offline access. Remove this app's access in your Google Account permissions, then connect again.",
      );
    }
    const record = {
      googleSubject,
      accessToken: tokens.access_token,
      refreshToken,
      expiresAt: issuedAt + tokens.expires_in * 1000,
      scopes: grantedScopes,
    };
    return publicConnection(
        await replaceIfUnchanged(db, ref, snapshot, record),
    );
  }

  if (!saved || saved.googleSubject !== googleSubject) return null;
  if (action === "invalidate") {
    if (saved.accessToken === rejectedAccessToken) {
      await replaceIfUnchanged(db, ref, snapshot, {
        ...saved,
        requiresConsent: true,
      });
    }
    return null;
  }
  if (saved.requiresConsent) return null;
  if (
    saved.expiresAt > now() + 60000 &&
    saved.accessToken !== rejectedAccessToken
  ) {
    return publicConnection(saved);
  }
  const issuedAt = now();
  const tokens = await tokenRequest(
      {grant_type: "refresh_token", refresh_token: saved.refreshToken},
      dependencies,
  );
  if (!tokens) {
    return publicConnection(await replaceIfUnchanged(db, ref, snapshot, null));
  }
  const grantedScopes = tokens.scope ? tokens.scope.split(" ") : saved.scopes;
  const record = {
    googleSubject,
    accessToken: tokens.access_token,
    refreshToken: tokens.refresh_token || saved.refreshToken,
    expiresAt: issuedAt + tokens.expires_in * 1000,
    scopes: grantedScopes,
    requiresConsent: !scopes[service].every((scope) =>
      grantedScopes.includes(scope),
    ),
  };
  return publicConnection(await replaceIfUnchanged(db, ref, snapshot, record));
}

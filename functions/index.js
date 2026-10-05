/**
 * Import function triggers from their respective submodules:
 *
 * const {onCall} = require("firebase-functions/v2/https");
 * const {onDocumentWritten} = require("firebase-functions/v2/firestore");
 *
 * See a full list of supported triggers at https://firebase.google.com/docs/functions
 */

import {initializeApp} from "firebase-admin/app";
import {getAuth} from "firebase-admin/auth";
import {getFirestore} from "firebase-admin/firestore";
import {getMessaging} from "firebase-admin/messaging";
import {setGlobalOptions} from "firebase-functions";
import {HttpsError, onCall, onRequest} from "firebase-functions/https";
import * as logger from "firebase-functions/logger";
import {defineSecret, defineString} from "firebase-functions/params";
import {handleGoogleConnection} from "./google-connections.js";
import {
  handlePushRegistration,
  handlePushWebhook,
} from "./push-notifications.js";

// For cost control, you can set the maximum number of containers that can be
// running at the same time. This helps mitigate the impact of unexpected
// traffic spikes by instead downgrading performance. This limit is a
// per-function limit. You can override the limit for each function using the
// `maxInstances` option in the function's options, e.g.
// `onRequest({ maxInstances: 5 }, (req, res) => { ... })`.
// NOTE: setGlobalOptions does not apply to functions using the v1 API. V1
// functions should each use functions.runWith({ maxInstances: 10 }) instead.
// In the v1 API, each function can only serve one request per container, so
// this will be the maximum concurrent request count.
setGlobalOptions({maxInstances: 10});

initializeApp();
const clientId = defineString("GOOGLE_OAUTH_CLIENT_ID");
const clientSecret = defineSecret("GOOGLE_OAUTH_CLIENT_SECRET");
const allowedOrigins = defineString("GOOGLE_OAUTH_ALLOWED_ORIGINS");
const pushWebhookSecret = defineSecret("PUSH_WEBHOOK_SECRET");

export const googleConnection = onCall(
    {secrets: [clientSecret]},
    async (request) => {
      try {
        return await handleGoogleConnection(request, {
          db: getFirestore(),
          auth: getAuth(),
          clientId: clientId.value(),
          clientSecret: clientSecret.value(),
          allowedOrigins: allowedOrigins
              .value()
              .split(",")
              .map((value) => value.trim()),
          fetch,
          now: Date.now,
        });
      } catch (error) {
      // OAuth errors can contain tokens and client secrets; never log or return their raw payloads.
        if (error instanceof HttpsError) throw error;
        logger.error("Google access could not be restored.");
        throw new HttpsError(
            "unavailable",
            "Google access could not be restored. Please try again.",
        );
      }
    },
);

export const pushRegistration = onCall((request) =>
  handlePushRegistration(request, {db: getFirestore()}),
);

export const pushWebhook = onRequest(
    {secrets: [pushWebhookSecret]},
    (request, response) =>
      handlePushWebhook(request, response, {
        db: getFirestore(),
        messaging: getMessaging(),
        secret: pushWebhookSecret.value(),
      }),
);

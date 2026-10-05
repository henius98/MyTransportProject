import assert from "node:assert/strict";
import {test} from "node:test";
import {
  handlePushRegistration,
  handlePushWebhook,
} from "../push-notifications.js";

function fixture() {
  const records = new Map();
  const db = {
    collection: (name) => {
      assert.equal(name, "pushDevices");
      return {
        doc: (id) => ({
          set: async (data) => {
            records.set(id, data);
          },
          get: async () => ({
            exists: records.has(id),
            data: () => records.get(id),
          }),
          delete: async () => {
            records.delete(id);
          },
        }),
        where: (field, operator, uid) => {
          assert.deepEqual([field, operator], ["uid", "=="]);
          return {
            limit: (count) => {
              assert.equal(count, 500);
              return {
                get: async () => {
                  const docs = [...records.entries()]
                      .filter(([, data]) => data.uid === uid)
                      .map(([id, data]) => ({
                        data: () => data,
                        ref: db.collection(name).doc(id),
                      }));
                  return {empty: docs.length === 0, docs};
                },
              };
            },
          };
        },
      };
    },
  };
  const response = {
    status(code) {
      this.code = code;
      return this;
    },
    json(body) {
      this.body = body;
      return this;
    },
  };
  return {db, records, response};
}

const token = "an-example-fcm-registration-token-123456";

test("only an authenticated user can register or remove their device", async () => {
  const {db, records} = fixture();
  await assert.rejects(
      handlePushRegistration({data: {action: "add", token}}, {db}),
      (error) => error.code === "unauthenticated",
  );
  await handlePushRegistration(
      {auth: {uid: "alice"}, data: {action: "add", token}},
      {db},
  );
  assert.equal([...records.values()][0].uid, "alice");
  await handlePushRegistration(
      {auth: {uid: "bob"}, data: {action: "remove", token}},
      {db},
  );
  assert.equal(records.size, 1);
  await handlePushRegistration(
      {auth: {uid: "alice"}, data: {action: "remove", token}},
      {db},
  );
  assert.equal(records.size, 0);
});

test("the webhook authenticates the Cloudflare sender and targets one user's devices", async () => {
  const {db, records, response} = fixture();
  await handlePushRegistration(
      {auth: {uid: "alice"}, data: {action: "add", token}},
      {db},
  );
  await handlePushRegistration(
      {auth: {uid: "bob"}, data: {action: "add", token: token + "-bob"}},
      {db},
  );
  const requests = [];
  const messaging = {
    sendEachForMulticast: async (message) => {
      requests.push(message);
      return {
        successCount: 1,
        failureCount: 0,
        responses: [{success: true}],
      };
    },
  };
  const request = {
    method: "POST",
    get: () => "Bearer shared-secret",
    body: {
      uid: "alice",
      title: "Transit update",
      body: "Bus arriving",
      url: "map",
    },
  };
  await handlePushWebhook({...request, get: () => "Bearer wrong"}, response, {
    db,
    messaging,
    secret: "shared-secret",
  });
  assert.equal(response.code, 401);
  await handlePushWebhook(request, response, {
    db,
    messaging,
    secret: "shared-secret",
  });
  assert.equal(response.code, 200);
  assert.equal(response.body.sent, 1);
  assert.deepEqual(requests[0], {
    tokens: [token],
    data: {title: "Transit update", body: "Bus arriving", url: "map"},
  });
  assert.equal(records.size, 2);
});

test("invalid URLs are rejected and expired device tokens are removed", async () => {
  const {db, records, response} = fixture();
  await handlePushRegistration(
      {auth: {uid: "alice"}, data: {action: "add", token}},
      {db},
  );
  const request = {
    method: "POST",
    get: () => "Bearer secret",
    body: {uid: "alice", title: "Alert", body: "Text", url: "map"},
  };
  const messaging = {
    sendEachForMulticast: async () => ({
      successCount: 0,
      failureCount: 1,
      responses: [
        {error: {code: "messaging/registration-token-not-registered"}},
      ],
    }),
  };
  await handlePushWebhook(
      {...request, body: {...request.body, url: "https://other.example"}},
      response,
      {db, messaging, secret: "secret"},
  );
  assert.equal(response.code, 400);
  await handlePushWebhook(request, response, {
    db,
    messaging,
    secret: "secret",
  });
  assert.equal(records.size, 0);
});

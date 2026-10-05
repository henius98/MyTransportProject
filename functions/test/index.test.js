import assert from "node:assert/strict";
import {test} from "node:test";
import {googleConnection, pushRegistration, pushWebhook} from "../index.js";

test("exported functions are defined and configured with v2 global options", () => {
  assert.equal(typeof googleConnection, "function");
  assert.equal(typeof pushRegistration, "function");
  assert.equal(typeof pushWebhook, "function");

  assert.equal(googleConnection.__endpoint?.platform, "gcfv2");
  assert.equal(pushRegistration.__endpoint?.platform, "gcfv2");
  assert.equal(pushWebhook.__endpoint?.platform, "gcfv2");

  assert.equal(googleConnection.__endpoint?.maxInstances, 10);
  assert.equal(pushRegistration.__endpoint?.maxInstances, 10);
  assert.equal(pushWebhook.__endpoint?.maxInstances, 10);
});

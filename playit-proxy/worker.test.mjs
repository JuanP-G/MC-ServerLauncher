// Tests of the Playit partner proxy (worker.js), run with `node --test` (Node 18 or later, no
// dependencies). The .NET test suite runs this file too (PlayitProxyWorkerTests), so CI covers it.
import { test, beforeEach, afterEach } from "node:test";
import assert from "node:assert/strict";
import worker from "./worker.js";

const URL_OK = "https://proxy.example/v1/partner/create_agent";
const VARIANT = "308943e8-faef-4835-a2ba-270351f72aa3";

const validBody = () => ({
  agent_name: "MC Server Launcher",
  self_managed: true,
  agent_details: { variant_id: VARIANT, version_major: 1, version_minor: 0, version_patch: 10 },
  platform: "windows",
  account_setup_code: "abc123DEF",
});

let upstreamCalls;
let realFetch;

beforeEach(() => {
  upstreamCalls = [];
  realFetch = globalThis.fetch;
  globalThis.fetch = async (url, init) => {
    upstreamCalls.push({ url, init });
    return new Response(JSON.stringify({ status: "success", data: { agent_secret_key: "k" } }), { status: 200 });
  };
});
afterEach(() => { globalThis.fetch = realFetch; });

const limiter = (success = true) => ({ limit: async () => ({ success }) });
const env = (extra = {}) => ({ PLAYIT_API_KEY: "partner-key", SETUP_LIMITER: limiter(), ...extra });

function post(body, headers = {}, url = URL_OK) {
  return new Request(url, {
    method: "POST",
    headers: { "Content-Type": "application/json", ...headers },
    body: typeof body === "string" ? body : JSON.stringify(body),
  });
}

const message = async (response) => (await response.json()).data.message;

test("a valid request is forwarded with the partner key and the answer comes back", async () => {
  const response = await worker.fetch(post(validBody()), env());

  assert.equal(response.status, 200);
  assert.equal(upstreamCalls.length, 1);
  assert.equal(upstreamCalls[0].url, "https://api.playit.gg/v1/partner/create_agent");
  assert.equal(upstreamCalls[0].init.headers["Authorization"], "Api-Key partner-key");
  assert.equal((await response.json()).data.agent_secret_key, "k");
});

test("without the rate limiter binding it refuses instead of forwarding", async () => {
  const response = await worker.fetch(post(validBody()), { PLAYIT_API_KEY: "partner-key" });

  assert.equal(response.status, 500);
  assert.equal(await message(response), "proxy not configured");
  assert.equal(upstreamCalls.length, 0);
});

test("a limiter that throws refuses instead of forwarding", async () => {
  const broken = { limit: async () => { throw new Error("binding down"); } };
  const response = await worker.fetch(post(validBody()), env({ SETUP_LIMITER: broken }));

  assert.equal(response.status, 503);
  assert.equal(upstreamCalls.length, 0);
});

test("a client over the limit gets 429 and nothing is forwarded", async () => {
  const response = await worker.fetch(post(validBody()), env({ SETUP_LIMITER: limiter(false) }));

  assert.equal(response.status, 429);
  assert.equal(upstreamCalls.length, 0);
});

test("the limiter runs before the body is read", async () => {
  // An invalid body from a client that is already over the limit is a 429, not a 400: the junk
  // request cost no more than the limiter check.
  const response = await worker.fetch(post("not json at all"), env({ SETUP_LIMITER: limiter(false) }));

  assert.equal(response.status, 429);
});

test("only the validated fields are forwarded: unknown fields are dropped", async () => {
  const body = validBody();
  body.api_key = "attacker";
  body.extra = { nested: true };
  body.agent_details.admin = true;

  await worker.fetch(post(body), env());

  const sent = JSON.parse(upstreamCalls[0].init.body);
  assert.deepEqual(sent, validBody());
});

test("the body is capped in bytes, not in characters", async () => {
  // 1,500 characters of 3 bytes each: under 4,096 characters, over 4,096 bytes.
  const body = validBody();
  body.account_setup_code = "x";
  body.padding = "€".repeat(1500);

  const response = await worker.fetch(post(body), env());

  assert.equal(response.status, 413);
  assert.equal(upstreamCalls.length, 0);
});

test("a declared Content-Length over the cap is refused", async () => {
  const response = await worker.fetch(post(validBody(), { "Content-Length": "999999" }), env());

  assert.equal(response.status, 413);
});

for (const [name, change, expected] of [
  ["a different agent name", (b) => { b.agent_name = "Other"; }, "unexpected agent_name"],
  ["self_managed false", (b) => { b.self_managed = false; }, "unexpected self_managed"],
  ["an unknown platform", (b) => { b.platform = "solaris"; }, "unexpected platform"],
  ["a different variant", (b) => { b.agent_details.variant_id = "other"; }, "unexpected agent_details"],
  ["a non-integer version", (b) => { b.agent_details.version_major = 1.5; }, "unexpected agent_details"],
  ["a negative version", (b) => { b.agent_details.version_patch = -1; }, "unexpected agent_details"],
  ["a version out of range", (b) => { b.agent_details.version_minor = 100000; }, "unexpected agent_details"],
  ["missing details", (b) => { delete b.agent_details; }, "unexpected agent_details"],
  ["an empty setup code", (b) => { b.account_setup_code = ""; }, "missing or invalid account_setup_code"],
  ["a setup code with a space", (b) => { b.account_setup_code = "ab cd"; }, "missing or invalid account_setup_code"],
  ["a setup code with a newline", (b) => { b.account_setup_code = "ab\ncd"; }, "missing or invalid account_setup_code"],
  ["a numeric setup code", (b) => { b.account_setup_code = 123; }, "missing or invalid account_setup_code"],
  ["a setup code that is too long", (b) => { b.account_setup_code = "a".repeat(257); }, "missing or invalid account_setup_code"],
]) {
  test(`${name} is rejected and not forwarded`, async () => {
    const body = validBody();
    change(body);

    const response = await worker.fetch(post(body), env());

    assert.equal(response.status, 400);
    assert.equal(await message(response), expected);
    assert.equal(upstreamCalls.length, 0);
  });
}

test("a body that is not a JSON object is rejected", async () => {
  for (const raw of ["not json", "null", "[]", "42", '"text"']) {
    const response = await worker.fetch(post(raw), env());
    assert.equal(response.status, 400, raw);
  }
  assert.equal(upstreamCalls.length, 0);
});

test("anything but POST to the one path is refused", async () => {
  const get = await worker.fetch(new Request(URL_OK, { method: "GET" }), env());
  assert.equal(get.status, 405);

  const other = await worker.fetch(post(validBody(), {}, "https://proxy.example/v1/partner/other"), env());
  assert.equal(other.status, 404);
  assert.equal(upstreamCalls.length, 0);
});

test("a browser request (Origin header) is refused and no CORS headers are sent", async () => {
  const response = await worker.fetch(post(validBody(), { Origin: "https://evil.example" }), env());

  assert.equal(response.status, 403);
  assert.equal(response.headers.get("Access-Control-Allow-Origin"), null);
});

test("APP_TOKEN is enforced only when it is set", async () => {
  const withToken = env({ APP_TOKEN: "marker" });

  assert.equal((await worker.fetch(post(validBody()), withToken)).status, 403);
  assert.equal((await worker.fetch(post(validBody(), { "X-MCSL-Client": "marker" }), withToken)).status, 200);
  assert.equal((await worker.fetch(post(validBody()), env())).status, 200);
});

test("a missing partner key is a configuration error", async () => {
  const response = await worker.fetch(post(validBody()), { SETUP_LIMITER: limiter() });

  assert.equal(response.status, 500);
  assert.equal(upstreamCalls.length, 0);
});

test("a failing upstream gives a generic 502 that does not echo the reason", async () => {
  globalThis.fetch = async () => { throw new Error("connect ECONNREFUSED 10.0.0.1:443"); };

  const response = await worker.fetch(post(validBody()), env());

  assert.equal(response.status, 502);
  assert.doesNotMatch(await response.text(), /ECONNREFUSED|10\.0\.0\.1/);
});

test("an upstream answer that is far too large is not passed on", async () => {
  globalThis.fetch = async () => new Response("x".repeat(20000), { status: 200 });

  const response = await worker.fetch(post(validBody()), env());

  assert.equal(response.status, 502);
});

test("Playit's own error status and body pass through", async () => {
  globalThis.fetch = async () => new Response(JSON.stringify({ status: "fail", data: "InvalidSetupCode" }), { status: 400 });

  const response = await worker.fetch(post(validBody()), env());

  assert.equal(response.status, 400);
  assert.equal((await response.json()).data, "InvalidSetupCode");
});

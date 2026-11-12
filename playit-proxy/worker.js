// MC Server Launcher — Playit partner proxy (Cloudflare Worker).
//
// Purpose: the partner Api-Key must NEVER ship inside the desktop app (it's public + open-source,
// so anything embedded is extractable). Instead, the app calls THIS proxy, and the proxy adds the
// Api-Key server-side and forwards the request to Playit. The key lives only here, as a Worker
// secret (PLAYIT_API_KEY) — set it with the dashboard or `wrangler secret put PLAYIT_API_KEY`.
//
// The proxy only forwards the single create_agent endpoint (nothing else), and requires that Playit
// call to already contain a user-authorized account_setup_code, so a leaked proxy URL can't be
// abused to do anything a setup code wouldn't already allow.
//
// Because the proxy URL is baked into a public app, it's known — and anyone with a free Playit
// account has a setup code of their own — so this Worker also hardens against abuse (minting agents
// with the partner key in bulk, burning the free quota, hammering Playit's partner endpoint):
//   - rejects anything but POST to the one path;
//   - rejects browser/cross-origin requests (an Origin header) and never emits CORS headers;
//   - rate-limits per client IP, and FAILS CLOSED: without the SETUP_LIMITER binding it refuses to
//     forward anything, so a Worker deployed without the limiter is not an open door;
//   - caps the body size IN BYTES and validates every field of a real create_agent request;
//   - forwards a body REBUILT from the validated fields only, never the caller's bytes, so nothing
//     the app does not send can reach Playit with the partner key;
//   - filters obvious noise with a known client header (optional, see APP_TOKEN).

const PLAYIT_ENDPOINT = "https://api.playit.gg/v1/partner/create_agent";
const MAX_BODY_BYTES = 4096;             // create_agent payloads are tiny; anything bigger is junk.
const MAX_RESPONSE_CHARS = 16384;        // Playit's answer is a few hundred bytes; more is not Playit.
const MAX_SETUP_CODE_LEN = 256;
const EXPECTED_AGENT_NAME = "MC Server Launcher";
// The agent variant the app registers with (PlayitPartnerService.VariantId): a public value from
// Playit's open-source agent. Any other variant is not this app.
const EXPECTED_VARIANT_ID = "308943e8-faef-4835-a2ba-270351f72aa3";
const PLATFORMS = ["windows", "linux", "macos"];
const SETUP_CODE_PATTERN = /^[\x21-\x7e]+$/;   // printable ASCII, no spaces or control characters
const MAX_VERSION_PART = 999;

export default {
  async fetch(request, env) {
    if (request.method !== "POST")
      return fail(405, "method not allowed");

    if (new URL(request.url).pathname !== "/v1/partner/create_agent")
      return fail(404, "not found");

    // This is a desktop-app → proxy call, never a browser one. A cross-origin browser request always
    // carries an Origin header and has no business here: reject it, and never send CORS headers, so
    // the proxy can't be driven from a web page.
    if (request.headers.get("Origin") !== null)
      return fail(403, "forbidden");

    // Optional shared client marker: only enforced when APP_TOKEN is set (kept off by default so we
    // don't break already-released app builds). Not a real secret if the app is open source, but it
    // turns away random scanners; set APP_TOKEN once your users are on a build that sends the header.
    if (env.APP_TOKEN && request.headers.get("X-MCSL-Client") !== env.APP_TOKEN)
      return fail(403, "forbidden");

    if (!env.PLAYIT_API_KEY)
      return fail(500, "proxy not configured");

    // The limiter is not optional. It used to be skipped when the binding was missing, which turned
    // a forgotten step of the deployment into an unlimited way to mint agents with the partner key;
    // a Worker without it now refuses to work, the same as one without the key.
    if (!env.SETUP_LIMITER)
      return fail(500, "proxy not configured");

    // Before reading the body: a junk request must not cost more than the rate-limit check.
    const ip = request.headers.get("CF-Connecting-IP") || "unknown";
    let allowed;
    try { allowed = (await env.SETUP_LIMITER.limit({ key: ip })).success; }
    catch (e) {
      console.error("rate limiter failed:", e);
      return fail(503, "try again later");
    }
    if (!allowed)
      return fail(429, "rate limited, try again later");

    // Reject oversized bodies before reading them.
    if (Number(request.headers.get("Content-Length") || "0") > MAX_BODY_BYTES)
      return fail(413, "payload too large");

    // BYTES, not characters: a string's length counts UTF-16 units, so a body of multi-byte
    // characters was several times larger than the cap that "checked" it.
    const bytes = await request.arrayBuffer();
    if (bytes.byteLength > MAX_BODY_BYTES)
      return fail(413, "payload too large");

    let parsed;
    try { parsed = JSON.parse(new TextDecoder("utf-8", { fatal: true }).decode(bytes)); }
    catch { return fail(400, "invalid JSON"); }

    const payload = validate(parsed);
    if (typeof payload === "string")
      return fail(400, payload);

    let upstream;
    try {
      upstream = await fetch(PLAYIT_ENDPOINT, {
        method: "POST",
        headers: {
          "Authorization": `Api-Key ${env.PLAYIT_API_KEY}`,
          "Content-Type": "application/json",
        },
        // The rebuilt body, never what the caller sent.
        body: JSON.stringify(payload),
      });
    } catch (e) {
      // The reason stays in the Worker's own log: echoing it to the client told anyone calling
      // the proxy how the upstream request failed, which is none of their business.
      console.error("playit upstream request failed:", e);
      return fail(502, "upstream unavailable, try again later");
    }

    // Pass Playit's response (status + JSON body) through, but only a response that looks like one.
    const text = await upstream.text();
    if (text.length > MAX_RESPONSE_CHARS)
      return fail(502, "upstream unavailable, try again later");

    return new Response(text, {
      status: upstream.status,
      headers: { "Content-Type": "application/json" },
    });
  },
};

/**
 * Checks a parsed body field by field and returns the request to forward, built only from what was
 * checked (unknown fields are dropped, not passed on), or a message saying what is wrong with it.
 */
function validate(body) {
  if (body === null || typeof body !== "object" || Array.isArray(body))
    return "invalid JSON";

  const code = body.account_setup_code;
  if (typeof code !== "string" || code.length < 1 || code.length > MAX_SETUP_CODE_LEN
      || !SETUP_CODE_PATTERN.test(code))
    return "missing or invalid account_setup_code";

  if (body.agent_name !== EXPECTED_AGENT_NAME)
    return "unexpected agent_name";
  if (body.self_managed !== true)
    return "unexpected self_managed";
  if (!PLATFORMS.includes(body.platform))
    return "unexpected platform";

  const details = body.agent_details;
  if (details === null || typeof details !== "object" || Array.isArray(details)
      || details.variant_id !== EXPECTED_VARIANT_ID)
    return "unexpected agent_details";
  for (const part of ["version_major", "version_minor", "version_patch"])
    if (!Number.isInteger(details[part]) || details[part] < 0 || details[part] > MAX_VERSION_PART)
      return "unexpected agent_details";

  return {
    agent_name: EXPECTED_AGENT_NAME,
    self_managed: true,
    agent_details: {
      variant_id: EXPECTED_VARIANT_ID,
      version_major: details.version_major,
      version_minor: details.version_minor,
      version_patch: details.version_patch,
    },
    platform: body.platform,
    account_setup_code: code,
  };
}

function fail(status, message) {
  return json({ status: "error", data: { type: "proxy", message } }, status);
}

function json(obj, status) {
  return new Response(JSON.stringify(obj), { status, headers: { "Content-Type": "application/json" } });
}

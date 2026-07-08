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
// Because the proxy URL is baked into a public app, it's known — so this Worker also hardens against
// plain abuse (burning the free quota, hammering Playit's partner endpoint with junk):
//   - rejects anything but POST to the one path;
//   - rejects browser/cross-origin requests (an Origin header) and never emits CORS headers;
//   - caps the body size and validates the JSON shape (a real create_agent request, not noise);
//   - filters obvious noise with a known client header (optional, see APP_TOKEN);
//   - rate-limits per client IP (optional binding SETUP_LIMITER, see wrangler.toml + README).

const PLAYIT_ENDPOINT = "https://api.playit.gg/v1/partner/create_agent";
const MAX_BODY_BYTES = 4096;             // create_agent payloads are tiny; anything bigger is junk.
const MAX_SETUP_CODE_LEN = 256;
const EXPECTED_AGENT_NAME = "MC Server Launcher";

export default {
  async fetch(request, env) {
    if (request.method !== "POST")
      return json({ status: "error", data: { type: "proxy", message: "method not allowed" } }, 405);

    if (new URL(request.url).pathname !== "/v1/partner/create_agent")
      return json({ status: "error", data: { type: "proxy", message: "not found" } }, 404);

    // This is a desktop-app → proxy call, never a browser one. A cross-origin browser request always
    // carries an Origin header and has no business here: reject it, and never send CORS headers, so
    // the proxy can't be driven from a web page.
    if (request.headers.get("Origin") !== null)
      return json({ status: "error", data: { type: "proxy", message: "forbidden" } }, 403);

    // Optional shared client marker: only enforced when APP_TOKEN is set (kept off by default so we
    // don't break already-released app builds). Not a real secret if the app is open source, but it
    // turns away random scanners; set APP_TOKEN once your users are on a build that sends the header.
    if (env.APP_TOKEN && request.headers.get("X-MCSL-Client") !== env.APP_TOKEN)
      return json({ status: "error", data: { type: "proxy", message: "forbidden" } }, 403);

    if (!env.PLAYIT_API_KEY)
      return json({ status: "error", data: { type: "proxy", message: "proxy not configured" } }, 500);

    // Reject oversized bodies before reading them.
    if (Number(request.headers.get("Content-Length") || "0") > MAX_BODY_BYTES)
      return json({ status: "error", data: { type: "proxy", message: "payload too large" } }, 413);

    const body = await request.text();
    if (body.length > MAX_BODY_BYTES)
      return json({ status: "error", data: { type: "proxy", message: "payload too large" } }, 413);

    // Minimal body validation: it must look like the create_agent request we actually make.
    let parsed;
    try { parsed = JSON.parse(body); }
    catch { return json({ status: "error", data: { type: "proxy", message: "invalid JSON" } }, 400); }

    const code = parsed && parsed.account_setup_code;
    if (typeof code !== "string" || code.length < 1 || code.length > MAX_SETUP_CODE_LEN)
      return json({ status: "error", data: { type: "proxy", message: "missing or invalid account_setup_code" } }, 400);
    if (!parsed || parsed.agent_name !== EXPECTED_AGENT_NAME)
      return json({ status: "error", data: { type: "proxy", message: "unexpected agent_name" } }, 400);

    // Per-IP rate limit (optional binding; configure it to actually cap abuse — see wrangler.toml).
    if (env.SETUP_LIMITER) {
      const ip = request.headers.get("CF-Connecting-IP") || "unknown";
      const { success } = await env.SETUP_LIMITER.limit({ key: ip });
      if (!success)
        return json({ status: "error", data: { type: "proxy", message: "rate limited, try again later" } }, 429);
    }

    let upstream;
    try {
      upstream = await fetch(PLAYIT_ENDPOINT, {
        method: "POST",
        headers: {
          "Authorization": `Api-Key ${env.PLAYIT_API_KEY}`,
          "Content-Type": "application/json",
        },
        body,
      });
    } catch (e) {
      return json({ status: "error", data: { type: "proxy", message: `upstream: ${e}` } }, 502);
    }

    // Pass Playit's response (status + JSON body) straight through.
    return new Response(await upstream.text(), {
      status: upstream.status,
      headers: { "Content-Type": "application/json" },
    });
  },
};

function json(obj, status) {
  return new Response(JSON.stringify(obj), { status, headers: { "Content-Type": "application/json" } });
}

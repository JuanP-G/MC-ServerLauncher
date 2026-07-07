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

const PLAYIT_ENDPOINT = "https://api.playit.gg/v1/partner/create_agent";

export default {
  async fetch(request, env) {
    if (request.method !== "POST") {
      return json({ status: "error", data: { type: "proxy", message: "method not allowed" } }, 405);
    }
    if (new URL(request.url).pathname !== "/v1/partner/create_agent") {
      return json({ status: "error", data: { type: "proxy", message: "not found" } }, 404);
    }
    if (!env.PLAYIT_API_KEY) {
      return json({ status: "error", data: { type: "proxy", message: "proxy not configured" } }, 500);
    }

    const body = await request.text();

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

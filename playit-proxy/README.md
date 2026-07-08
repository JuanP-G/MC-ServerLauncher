# Playit partner proxy

A tiny [Cloudflare Worker](https://workers.cloudflare.com/) that holds the Playit **partner
Api-Key** server-side, so it never ships inside the desktop app. The app calls this proxy; the
proxy adds the `Authorization: Api-Key …` header and forwards the request to Playit's
`/v1/partner/create_agent`.

Why: the app is public and open-source, so any key embedded in it (even encoded) is extractable.
A proxy is the only way to actually keep the key secret. The Worker free tier (100k requests/day)
is far more than this needs.

## Deploy (dashboard, no CLI — easiest)

1. Create a free account at https://dash.cloudflare.com.
2. **Workers & Pages → Create → Workers → Create Worker**. Give it a name, e.g. `mcsl-playit`.
   Click **Deploy** (the default hello-world is fine for now).
3. **Edit code** → replace everything with the contents of [`worker.js`](worker.js) →
   **Deploy**.
4. **Settings → Variables and Secrets → Add → type: Secret**:
   - Name: `PLAYIT_API_KEY`
   - Value: your Playit partner Api-Key
   - **Save / Deploy**.
5. Copy the Worker URL shown at the top — it looks like
   `https://mcsl-playit.<your-subdomain>.workers.dev`.

That URL (public, not secret) is what gets baked into the app. The Api-Key stays only in the
Worker secret.

6. **Add the rate-limit binding** (recommended — see *Abuse protection* below). Dashboard:
   **Settings → Bindings → Add → Rate limiting**, name `SETUP_LIMITER`, e.g. **20 requests / 60 s**.
   With the CLI, it's already in [`wrangler.toml`](wrangler.toml), so `wrangler deploy` sets it up.

## Abuse protection

The proxy URL is baked into a public app, so it's known. Even though a leaked URL can't do anything a
user-authorized setup code wouldn't already allow (and never exposes the Api-Key), someone could still
spam junk requests to burn the Worker's free quota or hammer Playit's partner endpoint. The Worker
guards against that:

- **Only** `POST /v1/partner/create_agent` is accepted; everything else is 404/405.
- **No browser access**: any request with an `Origin` header is rejected and no CORS headers are ever
  returned, so the proxy can't be driven from a web page.
- **Body limits + validation**: bodies over 4 KB are refused, and the JSON must actually look like a
  create_agent request (a non-empty `account_setup_code` and the expected `agent_name`).
- **Per-IP rate limit**: `SETUP_LIMITER` caps create_agent attempts per client IP (default 20/min).
  If the binding isn't configured the Worker still runs but the limit is skipped — so configure it.
- **Optional client header**: set an `APP_TOKEN` var to require the app's `X-MCSL-Client` header to
  match it (extra noise filter). Leave it unset to stay compatible with older app builds.

And the Api-Key never leaves the Worker, so none of the above can leak it. Keep the key rotation below
handy in case Playit ever rate-limits or flags the partner account.

## Deploy (CLI, alternative)

```bash
npm i -g wrangler
wrangler login
# in this folder:
wrangler deploy                       # deploys worker.js (see wrangler.toml)
wrangler secret put PLAYIT_API_KEY    # paste the key when prompted
```

## Test it

```bash
curl -sS -X POST "https://mcsl-playit.<your-subdomain>.workers.dev/v1/partner/create_agent" \
  -H "Content-Type: application/json" \
  --data '{"agent_name":"MC Server Launcher","self_managed":true,"platform":"windows","account_setup_code":"<A FRESH CODE>","agent_details":{"variant_id":"308943e8-faef-4835-a2ba-270351f72aa3","version_major":1,"version_minor":0,"version_patch":10}}'
```

A valid, fresh setup code should return `{"status":"success", "data":{ "agent_secret_key": … }}`.
Note there is **no** `Authorization` header here — the Worker adds it.

## Rotating the key

If the key is ever compromised, rotate it in Playit and update the `PLAYIT_API_KEY` secret in the
Worker. Nothing in the app changes (the app never sees the key).

## Trust assumption: the Playit agent binary

To actually forward tunnel traffic, the app downloads Playit's official **`playitd`** agent and runs
it as a child process with the user's per-user agent key (`PlayitAgentRunner`). Because that native
binary is the highest-privilege code the app fetches, it is **pinned and checksum-verified** before
it ever runs, exactly like every other download (Mojang/Adoptium/Paper/Modrinth and the app's own
installer):

- The agent version is pinned (`AgentVersion`, currently `v1.0.10`), and the **SHA-256 of each
  per-OS asset is hard-coded** in `PlayitAgentRunner` (`AssetSha256`).
- After download — and also when reusing a cached copy on disk — the file is verified against that
  pinned hash via `DownloadVerifier`. On mismatch it is deleted and the start fails; a tampered or
  corrupted binary is never executed. This closes the "someone serves an altered binary at the
  GitHub Releases URL" vector (repo/account compromise, a re-uploaded release, a broken-TLS MITM, or
  a hostile DNS/proxy).
- **Upgrading the agent:** when you bump `AgentVersion`, you must also update the pinned hashes in
  `AssetSha256` (download the new assets and record their SHA-256). A version bump without matching
  hashes will (correctly) refuse to run.

The pins are the exact bytes of Playit's signed `v1.0.10` release assets; the SHA-256 pin is
stricter than an Authenticode publisher check (it fixes the precise binary, not just "signed by
someone"), so it's the primary integrity control here.

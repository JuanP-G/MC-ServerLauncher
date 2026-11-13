# Playit partner proxy

A tiny [Cloudflare Worker](https://workers.cloudflare.com/) that holds the Playit **partner
Api-Key** server-side, so it never ships inside the desktop app. The app calls this proxy; the
proxy adds the `Authorization: Api-Key …` header and forwards the request to Playit's
`/v1/partner/create_agent`.

Why: the app is public and open-source, so any key embedded in it (even encoded) is extractable.
A proxy is the only way to actually keep the key secret. The Worker free tier (100k requests/day)
is far more than this needs.

## What it sees

The setup code the user pastes (one use, minted by Playit) and, in the answer, the per-user agent
key that Playit returns. Both pass through the Worker in memory: it keeps no storage and logs only
the reason of its own failures (`console.error`), never a body. The dialog that asks for the code
says so, and the README of the app lists it among what the app sends to others. The Worker runs on
the maintainer's Cloudflare account, so a user is trusting the project with those two values for the
moment they are exchanged, the same as with the signed updates.

Its address today is a `workers.dev` subdomain of that account. A domain owned by the project would
be better (it would not name a person, and it could move to another account without a release), but
every installed app calls the current address: whoever moves it has to keep this Worker answering
there until the last release that names it is gone, and put the new address in
`PlayitPartnerService.DefaultProxyUrl`.

## Deploy (dashboard, no CLI — easiest)

1. Create a free account at https://dash.cloudflare.com.
2. **Workers & Pages → Create → Workers → Create Worker**. Give it a name, e.g. `mcsl-playit`.
   Click **Deploy** (the default hello-world is fine for now). The name decides the address, and the
   app calls one address (`PlayitPartnerService.DefaultProxyUrl`): a Worker you create under another
   name is a different Worker that the app will never reach until you change that URL too.
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

6. **Add the rate-limit binding — required** (see *Abuse protection* below). Dashboard:
   **Settings → Bindings → Add → Rate limiting**, name `SETUP_LIMITER`, e.g. **20 requests / 60 s**.
   With the CLI, it's already in [`wrangler.toml`](wrangler.toml), so `wrangler deploy` sets it up
   (Wrangler 4.36.0 or later). **Without it the Worker refuses every request** (HTTP 500
   `proxy not configured`), the same as without the key: it will not run unlimited.

> [!IMPORTANT]
> **Updating a Worker that is already deployed** (an older version of `worker.js` has no limiter
> requirement): add the `SETUP_LIMITER` binding **first or in the same deploy**, then replace the
> code. If you replace the code first, the app gets 500s until the binding exists. Afterwards, check
> that the app still connects (see *Test it*).

## Abuse protection

The proxy URL is baked into a public app, so it's known. Even though a leaked URL can't do anything a
user-authorized setup code wouldn't already allow (and never exposes the Api-Key), someone could still
spam junk requests to burn the Worker's free quota or hammer Playit's partner endpoint. The Worker
guards against that:

- **Only** `POST /v1/partner/create_agent` is accepted; everything else is 404/405.
- **No browser access**: any request with an `Origin` header is rejected and no CORS headers are ever
  returned, so the proxy can't be driven from a web page.
- **Body limits + validation**: bodies over 4 KB (**bytes**, not characters) are refused, and every
  field of the JSON is checked: the setup code (printable ASCII, up to 256 characters), the exact
  `agent_name`, `self_managed: true`, a known `platform`, the exact `variant_id` and three small
  integer version numbers.
- **Only what was checked is forwarded**: the request sent to Playit is rebuilt from the validated
  fields. Anything else the caller added is dropped, so nothing the app doesn't send can travel to
  Playit carrying the partner key.
- **Per-IP rate limit, mandatory**: `SETUP_LIMITER` caps create_agent attempts per client IP (default
  20/min) and is checked **before** the body is read. If the binding is missing the Worker fails
  closed (500) instead of running without a limit; if the limiter itself errors, the answer is 503.
- **Playit's answer is capped** too: a response far larger than a create_agent reply is not passed on.
- **No internal errors echoed**: if the request to Playit fails, the client gets a generic 502 and
  the reason goes only to the Worker's log.
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

## Tests

`worker.test.mjs` runs the Worker's code against a fake `fetch` and a fake limiter, with Node's own
test runner and no dependencies:

```bash
cd playit-proxy
node --test worker.test.mjs
```

The .NET suite runs it too (`PlayitProxyWorkerTests`), so CI covers it.

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

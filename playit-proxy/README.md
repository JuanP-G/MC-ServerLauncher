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

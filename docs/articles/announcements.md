# Announcements

> ¿Prefieres español? Lee la versión en [español](announcements.es.md).

What the **What's new** section shows besides the notes of each version: notices that the project can
publish to every installed app (an outage of a service the app relies on, a security matter, a tip), how
they are signed and read, what the app asks for to get them, and how to publish one.

The reason it exists: when Playit.gg fails, the person using the app has no way to know it is not the app
or their server. A notice says so, in the app, without anyone having to update.

## What the user sees

- **Notices in What's new.** Above the versions: what is going on first, then what ended (in green, for a
  week). Each is a card with a ring and an icon in the colour of its kind (amber for an *incident*, red for
  *security*, blue for a *tip*) and, when there is one, a **More information** button.
- **A strip under the title bar** for an incident or a security notice that is still going on and that the
  user has neither read nor closed. **View notice** opens What's new, and opening it counts as reading, so
  the strip goes away; the cross closes it for that notice only without opening anything (it stays listed).
  It also goes away by itself when the notice is resolved or runs out.
- **A notification**, once, for a new incident or security notice, and again when an incident the user was
  told about is resolved. It is not raised when the strip is already in front of the user with the window
  open, and it follows the master switch of notifications.
- **A mark on the rail** while something is unread, the same one the versions use.
- **A card when the file could not be checked**: *Could not check for notices. Last copy: 3 h ago.* A failed
  look is never shown as "no notices", because that is exactly when an incident could be going on unseen.
- **A card when the file has expired**: *The list of notices has expired and may be out of date. Last
  checked: 3 h ago.* A different message, for a different thing: the look worked, and what it found (or
  what the app holds) is out of date (see *The file expires*).

Tips never use the strip or notifications.

**Settings → Announcements** has: *Show announcements* (off: no request at all, no mark, no strip, no
notification, and What's new lists only versions), how the rail marks what is unread, where the strip shows
(the whole app, only in Tunnels, or never), whether an urgent notice also raises a notification, and
whether incidents and tips are wanted. Security notices have no switch of their own: only *Show
announcements* silences them.

## What the app asks for, and what it shows of you

Two addresses serve the same file, tried in order:

1. `https://mc-server-launcher.vercel.app/news.json`
2. `https://juanp-g.github.io/MC-ServerLauncher/news.json`

Each look is a request for the file and one for its signature (`news.json.sig`), or a single conditional
request that answers *not modified* when nothing changed. The app looks once shortly after it starts, every
5 minutes while it stays open, and when the window comes back to the front or What's new is opened if the
copy it holds was last confirmed more than 2 minutes ago. A look that finds nothing new is a *not modified*
answer with no body, a few hundred bytes, so an urgent notice reaches an open app within about five
minutes and costs next to nothing.

The request carries **no version, no language and no identifier**: the server sees an address and the app's
User-Agent, as for any download. Which notices are for this machine is decided **in the app**, from the
file (see *Who a notice is for*); nothing about what the user does leaves the computer.

## The file

`web/news.json`, UTF-8 with **LF** line ends and no byte-order mark, next to `web/news.json.sig`. The
website and the Pages workflow publish the `web/` folder as it is.

```json
{
  "schema": 1,
  "seq": 42,
  "issued": "2026-11-07T14:20:00Z",
  "expires": "2027-01-06T14:20:00Z",
  "items": [
    {
      "id": "playit-2026-11-07",
      "kind": "incident",
      "status": "active",
      "from": "2026-11-07T14:00:00Z",
      "until": "2026-11-09T14:00:00Z",
      "only": { "usesTunnels": true },
      "link": "https://playit.gg/status",
      "text": {
        "es": { "title": "Playit.gg tiene problemas", "body": "Los túneles pueden no conectar." },
        "en": { "title": "Playit.gg is having problems", "body": "Tunnels may fail to connect." }
      }
    }
  ]
}
```

| Field | Meaning |
| --- | --- |
| `schema` | Always `1`. A file with another number is refused whole. |
| `seq` | Whole number, higher with every version of the file. The app keeps the highest it has accepted and **refuses a lower one**; an equal one is accepted. |
| `issued` | When this version was written. |
| `expires` | **Mandatory.** When the file stops vouching that it is the latest one (see *The file expires*). It must be later than `issued`; a file without it, or with another kind of value, is refused whole. `sign-news.ps1` writes it. |
| `id` | Names the notice for ever: what is read, closed and announced is remembered by it. Up to 64 characters from letters, digits, `.`, `_` and `-`. |
| `kind` | `incident`, `security` or `tip`. |
| `status` | `active` (the default) or `resolved`. A tip has nothing to resolve: it is listed while it is in the file. |
| `from` / `until` | The notice is shown from `from` (by default, from `issued`) until `until`. **`until` is mandatory for an incident and a security notice**, and it is capped at 30 days after `from`, so a forgotten notice cannot become permanent. A tip may have none. |
| `resolvedAt` | Mandatory when `status` is `resolved`. The notice is retired seven days after it, or at `until` if that comes first. |
| `only` | Who it is for (below). Left out, it is for everybody. |
| `link` | Where *More information* goes: `https`, and only `github.com`, `playit.gg`, `mc-server-launcher.vercel.app` or `juanp-g.github.io`. Anything else is dropped. |
| `text` | `es`, `en`, `de`, `fr` and `pt`, each with a `title` (80 characters at most) and a `body` (400 at most). |

At most 64 KiB per file, and the app reads the first 50 items. An item that is not valid is skipped on its
own and the rest of the file is used; when two items share an `id`, the first one counts.

The language is the one the app is shown in; failing that English, failing that whichever the notice has.

### Who a notice is for

`only` narrows a notice; every condition left out narrows nothing:

| Condition | Meaning |
| --- | --- |
| `usesTunnels` | `true`: only whoever has a Playit account or a server set to use a tunnel. `false`: only whoever has neither. |
| `os` | A list of `windows`, `linux`, `macos`. An empty list on purpose is a mistake and the notice is dropped. |
| `minVersion` / `maxVersion` | The app versions it applies to, both inclusive. |

The check for tunnels does not need the agent to be connected: an outage matters to whoever depends on the
service, up or down.

## How it is checked

Everything below happens before the content is looked at, and nothing is written to disk until it passes.

1. The file must arrive whole and within the size limit.
2. Its signature must verify, over the exact bytes received, with one of the keys built into the app (the
   same ones as for updates; see [Signed updates](signing.md)).
3. It must parse, and its `seq` must not be lower than the highest ever accepted.
4. Its `expires` must not have passed. A file past it is refused as *expired*, which is not the same as a
   failed look: see the next section.
5. Only then are the file and its signature replaced, and the ETag is stored last of all: an ETag kept for
   a body that was then refused would make every later request answer *not modified* and a corrected
   signature would never be picked up.

The copy kept in the app's `cache` folder (`news.json`, `news.json.sig`, `news.stamp`) is **verified again
every time it is read**: the folder is writable by anything on the machine. What has been read, closed and
announced is in `news-state.json`, apart from the settings.

If any step fails the whole file is refused and the previous copy stays; the user sees the card above. Both
addresses answering *not found* is not an error: nothing has been published, and there are no notices.

### The file expires

A signature stays valid for ever, so on its own it cannot tell the latest file from an old one that was
signed correctly in its day. `expires` is signed with the rest, so nobody who serves the file can stretch
it, and the app judges it **every time the file is read**, not once when it was saved:

- A file that arrives already expired is refused and replaces nothing.
- A *not modified* answer for a copy that has expired is not a confirmation: the server says nothing newer
  exists, which is no comfort when what exists is out of date.
- A copy that was good when saved is reported stale as soon as its date passes, with no network involved,
  also after a restart.

In every case the user sees a card in What's new saying that **the list of notices has expired and may be
out of date**, with when it was last confirmed. It is a different message from "could not check for
notices": the look worked, the file is what is old. The notices of the expired copy are **not hidden**
(an incident ends by its own `until`, at most 30 days after it started), they are listed with the warning,
so an expired file never makes a live incident disappear. The strip and the notifications behave as with
any copy. Renewing is signing the file again with a later date and a higher `seq`: the same one is accepted,
but then the file from before the renewal could be served again until its own date. The card goes away at
the next look.

If two addresses disagree (one serves an expired file, the other a current one) the current one wins; if
one cannot be reached the result is a failed look rather than an expired one, because not knowing outranks
knowing what is wrong.

## Publishing a notice

1. Edit `web/news.json` and raise `seq`. Keep **LF** line ends and no BOM (`.gitattributes` marks the file
   and its signature `-text` so git does not convert them).
2. Run `.\tools\signing\sign-news.ps1`. It asks for the key's password, writes `expires` (60 days from now;
   `-ExpiresInDays` takes 1 to 90, because more would be never expiring in practice) and writes
   `web/news.json.sig`. It does not touch `issued`, which is the default `from` of the notices, or `seq`.
   It refuses a file with CR or a BOM, over 64 KiB, with an incident or security item without `until`, or a
   resolved one without `resolvedAt`: better an error here than a notice nobody sees. The date it printed
   at the end is the one to renew by.
3. Commit both files in a pull request. Once merged, check that both addresses serve the new file and that
   the signature verifies by hand ([Signed updates](signing.md#verifying-a-release-by-hand)).
4. To end an incident, do not delete it: set `status` to `resolved` with `resolvedAt`, raise `seq` and sign
   again. The app says it was resolved (strip gone, one notification, green card for a week). A notice that
   is simply removed just disappears, without the good news.
5. **Renew before it expires**, even with nothing to say: raise `seq`, run the script again and publish the
   file and its signature (a pull request, as in step 3). A good moment is every release. A file left to expire does
   nothing worse than show the *out of date* card to everyone, but it does show it to everyone.

## What this does not cover

- **Freshness, in full.** A signature proves who wrote the file, not that it is the latest. `seq` stops an
  older file being served after a newer one was seen, and `expires` bounds the rest: whoever can block the
  network can keep an app on the last file it saw, but only until that file expires, after which the app
  says so instead of passing it for current. Nothing stops it in the days before.
- **A leaked key.** It signs updates and notices alike, so whoever has it could show false notices until a
  release removes it from the trusted keys. What a notice can do is limited: text, and a link to four hosts.
  It never runs or downloads anything.
- **Targeting people.** `only` is coarse on purpose and is decided on the user's machine; there is no way
  to address one person.

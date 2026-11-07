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
  user has not closed. **View notice** opens What's new; the cross closes the strip for that notice only (it
  stays listed). It goes away by itself when the notice is resolved or runs out.
- **A notification**, once, for a new incident or security notice, and again when an incident the user was
  told about is resolved. It is not raised when the strip is already in front of the user with the window
  open, and it follows the master switch of notifications.
- **A mark on the rail** while something is unread, the same one the versions use.
- **A card when the file could not be checked**: *Could not check for notices. Last copy: 3 h ago.* A failed
  look is never shown as "no notices", because that is exactly when an incident could be going on unseen.

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
30 minutes while it stays open, and when What's new is opened if the copy it holds was last confirmed more
than 10 minutes ago.

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
| `id` | Names the notice for ever: what is read, closed and announced is remembered by it. Up to 64 characters from letters, digits, `.`, `_` and `-`. |
| `kind` | `incident`, `security` or `tip`. |
| `status` | `active` (the default) or `resolved`. A tip has nothing to resolve: it is listed while it is in the file. |
| `from` / `until` | The notice is shown from `from` (by default, from `issued`) until `until`. **`until` is mandatory for an incident and a security notice**, and it is capped at 30 days after `from`, so a forgotten notice cannot become permanent and the file needs no expiry of its own. A tip may have none. |
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
4. Only then are the file and its signature replaced, and the ETag is stored last of all: an ETag kept for
   a body that was then refused would make every later request answer *not modified* and a corrected
   signature would never be picked up.

The copy kept in the app's `cache` folder (`news.json`, `news.json.sig`, `news.stamp`) is **verified again
every time it is read**: the folder is writable by anything on the machine. What has been read, closed and
announced is in `news-state.json`, apart from the settings.

If any step fails the whole file is refused and the previous copy stays; the user sees the card above. Both
addresses answering *not found* is not an error: nothing has been published, and there are no notices.

## Publishing a notice

1. Edit `web/news.json` and raise `seq`. Keep **LF** line ends and no BOM (`.gitattributes` marks the file
   and its signature `-text` so git does not convert them).
2. Run `.\tools\signing\sign-news.ps1`. It asks for the key's password and writes `web/news.json.sig`. It
   refuses a file with CR or a BOM, over 64 KiB, with an incident or security item without `until`, or a
   resolved one without `resolvedAt`: better an error here than a notice nobody sees.
3. Commit both files in a pull request. Once merged, check that both addresses serve the new file and that
   the signature verifies by hand ([Signed updates](signing.md#verifying-a-release-by-hand)).
4. To end an incident, do not delete it: set `status` to `resolved` with `resolvedAt`, raise `seq` and sign
   again. The app says it was resolved (strip gone, one notification, green card for a week). A notice that
   is simply removed just disappears, without the good news.

## What this does not cover

- **Freshness.** A signature proves who wrote the file, not that it is the latest. `seq` stops an older
  file being served after a newer one was seen, but whoever can block the network can keep an app on the
  last file it saw.
- **A leaked key.** It signs updates and notices alike, so whoever has it could show false notices until a
  release removes it from the trusted keys. What a notice can do is limited: text, and a link to four hosts.
  It never runs or downloads anything.
- **Targeting people.** `only` is coarse on purpose and is decided on the user's machine; there is no way
  to address one person.

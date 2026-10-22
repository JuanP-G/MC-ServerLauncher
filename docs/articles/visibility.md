# Visibility and statistics

> ¿Prefieres español? Lee la versión en [español](visibility.es.md).

How we measure who reaches the website and who downloads the app, and where it is worth being listed so
more people find it. The app has no telemetry and none will be added unless it is optional and explicit:
only the website and what GitHub already publishes are measured here.

## What is measured, and with what

| What | Tool | What it stores |
| --- | --- | --- |
| Website visits, country, language, device, where visitors come from | [GoatCounter](https://www.goatcounter.com) | No cookies, no banner, no IP stored. Free for non-commercial projects. |
| Clicks on Download (per OS), GitHub, Issues, language switch | GoatCounter events (`data-goatcounter-click` in `web/index.html`) | One counter per button, no personal data. |
| Which guide leads to a download | Events `guia-<id>-cta`, `guia-<id>-descarga-win/linux/mac`, `guia-<id>-mas-<other>` and `guia-<id>-landing` | Same: one counter per button and per guide. |
| Real downloads of each installer | GitHub Releases counters | Cumulative total per file. |
| Repository views, clones and referrers | GitHub traffic API | Only the last 14 days. |

A click on "Download" is not a download: the person can cancel on GitHub. **Real downloads** come from the
release counters; clicks are for comparing buttons and systems.

## Setup (once)

1. **GoatCounter.** Create the account at goatcounter.com with the code `mc-serverlauncher` (the site
   already points to `https://mc-serverlauncher.goatcounter.com`). If that code is taken, pick another one,
   change it in `web/index.html` (one line with `data-goatcounter`) and run `python web/_i18n/build.py`.
   Optional: under *Settings → Site* enable "Allow adding visitors to this site..." for a public dashboard,
   a nice transparency gesture.
2. **Daily GitHub snapshot.** The `stats.yml` workflow saves downloads and stars every day on the `stats`
   branch. To save traffic as well it needs a secret: create a personal access token with write access to
   this repository and store it under *Settings → Secrets and variables → Actions* as `TRAFFIC_TOKEN`.
   The traffic API refuses the token GitHub gives to workflows.
   To bring the website visits into the same snapshot, create a GoatCounter API token (*Settings → API*,
   only "Read statistics", only this site) and store it as the secret `GOATCOUNTER_TOKEN`.
3. **Run it now** instead of waiting until tomorrow: *Actions → Daily statistics snapshot → Run workflow*.

## Reading the data

```bash
git fetch origin stats
git worktree add ../stats-data origin/stats
python tools/stats_report.py ../stats-data
```

It shows downloads per system and version, growth over 7 and 30 days (`n/d` until snapshots that old
exist), repository views and where they come from. The CSV files (`downloads.csv`, `repo.csv`,
`views.csv`, `clones.csv`, `referrers.csv`, `paths.csv`) open directly in a spreadsheet.

## Where to be listed

- **Google Search Console**: already verified. Check *Performance* to see which searches show the site.
- **Bing Webmaster Tools** (what Edge uses): sign in with the same Google account and **import** the
  property from Search Console. `web/_i18n/ping-indexnow.py` already notifies Bing of changes via IndexNow.
- **AlternativeTo**: the listing still shows screenshots of the old design. Replace them with the ones in
  `docs/screenshots/` and refresh the description with what is new. Changes need a signed-in account
  (*Suggest changes*), so they cannot be automated.
- **Package managers**: being installable with `winget install` is free, trusted discovery. The manifest for
  the current version lives in `installer/winget/` (validated with `winget validate`). To publish it, copy
  `manifests/` into a fork of [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) and open a PR;
  after the first one is accepted, each release is one command:
  `wingetcreate update JuanP-G.MC-ServerLauncher --version X --urls <installer-url> --submit`. Scoop and
  Chocolatey work along the same lines.
- **Listings with their own traffic**: Softpedia, MajorGeeks, SourceForge, Flathub (for the AppImage).
- **Communities**: r/admincraft, r/Minecraft (mind each one's self-promotion rules), Modrinth.

## Guides for specific searches

The landing competes for "Minecraft server", a very crowded search. The guides target long, specific
searches, where a dedicated page can win. There are five, each in all five languages (25 pages):

| Guide (`id`) | Search it answers | English |
| --- | --- | --- |
| `mods` | make a Minecraft server with mods | `/en/guides/make-minecraft-server-with-mods/` |
| `gratis` | host a free server without port forwarding | `/en/guides/host-free-minecraft-server-no-port-forwarding/` |
| `dormir` | server that stops itself when empty | `/en/guides/minecraft-server-stops-itself-when-empty/` |
| `descargar` | download the Minecraft server (official jar) | `/en/guides/download-minecraft-server-jar-official/` |
| `comparativa` | best Minecraft server manager for Windows | `/en/guides/best-minecraft-server-manager-windows/` |

Spanish lives at `/guias/<slug>/` and the other languages at `/de/guides/`, `/fr/guides/` and
`/pt/guides/`. Each page has its own title, description, `canonical`, `hreflang` (all five versions plus
`x-default`) and structured data (`Article`, `HowTo`, `FAQPage` and `BreadcrumbList`; `comparativa` has no `HowTo` because it is not a procedure). Google no longer
shows the `HowTo` and `FAQPage` rich results for most sites, but the data still helps it understand the page.

**Where the text lives.** Each guide is a single file, `web/_i18n/guides/<id>.json`, with all five
languages inside; Spanish sets the structure (blocks, steps and questions). The pages are not edited by
hand. `python web/_i18n/build.py` generates the landing, the translations and the guides in one go, and the
template cuts the stylesheet, scene, bar, downloads and footer out of the already-translated landing, so the
guides inherit its changes and its interface translation without repeating them.

**What is checked while generating.** Generation fails, writing nothing, if a language is missing a string,
if its structure differs from the Spanish one, if a long text is identical to the Spanish (untranslated), if
a `title` is over 78 characters or a `description` is outside 70–175, or if an internal `{guide:id}` link
does not exist. It also rewrites the guides' entries in `sitemap.xml` (between the `guides:start` and
`guides:end` markers) and the cards of the landing's "Guides" section.

**What people searched for (October 22, 2026).** Search Console, last 28 days: 12 clicks, 177 impressions
and an average position of 16.8. The queries with clicks are brand ones (`mc server launcher`, `server
launcher`); long-tail queries already appear but without clicks yet (`minecraft server mods download`,
`minecraft server descargar`, `how to add mods to minecraft server`). As a result:

- `mods`: the title, H1 and description carry "how to add mods to a Minecraft server", there is a "Where to
  download Minecraft server mods" section and a "How do I add mods...?" FAQ entry. The URL is unchanged so
  what is already indexed is not lost.
- `descargar`: new, for "minecraft server descargar" and "download minecraft server jar".
- `comparativa`: new, for "best Minecraft server manager / launcher for Windows". Every fact in the table
  comes from each project's official website, read on October 22, 2026 (Crafty Controller, Pterodactyl, AMP
  and MCSManager). What could not be checked is not claimed: for AMP it only says it is paid and that prices
  are on its website. When updating it, re-read their sites and change the date in the text.
- The three older guides carry "MC Server Launcher" (the name the app window shows) in the description, and
  link to `descargar` and `comparativa` from their last section.

**Adding a guide.** Create `web/_i18n/guides/<id>.json` by copying an existing one (slugs, dates, `icon` and
the five languages), add its Spanish card to the `#guias` section of `web/index.html` (the card text is
`card_title` / `card_desc` and the link is the Spanish slug's path) and run `python web/_i18n/build.py`.
`ping-indexnow.py` picks it up by itself. The available blocks are `h2`, `p`, `ul`, `tip`, `steps` and `table`
(four columns at most; it stacks on mobile). With `"howto": false` the guide generates no `HowTo`; otherwise it
uses its last `steps` block. Change `modified` only when you really edit it: the sitemap shows it.

**After publishing.** In Search Console, *URL Inspection → Request indexing* with the five Spanish
addresses (it finds the rest through the sitemap and `hreflang`), and `python web/_i18n/ping-indexnow.py` for
Bing, **once deployed** (`--dry-run` only lists the addresses, sending nothing). Wait a few weeks and look under *Performance* at which queries bring impressions to each guide; in
GoatCounter, which guide ends in a download click.

## When a number does not add up

- GoatCounter usually shows fewer visits than other tools because it ignores bots and cannot see people
  who block third-party scripts; that is expected from privacy-respecting measurement.
- Download counters include the ones you make yourself when testing a release.

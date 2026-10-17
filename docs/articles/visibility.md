# Visibility and statistics

> 🇪🇸 ¿Prefieres español? Lee la versión en [español](visibility.es.md).

How we measure who reaches the website and who downloads the app, and where it is worth being listed so
more people find it. The app has no telemetry and none will be added unless it is optional and explicit:
only the website and what GitHub already publishes are measured here.

## What is measured, and with what

| What | Tool | What it stores |
| --- | --- | --- |
| Website visits, country, language, device, where visitors come from | [GoatCounter](https://www.goatcounter.com) | No cookies, no banner, no IP stored. Free for non-commercial projects. |
| Clicks on Download (per OS), GitHub, Issues, language switch | GoatCounter events (`data-goatcounter-click` in `web/index.html`) | One counter per button, no personal data. |
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
- **AlternativeTo**: the listing has no screenshots at all. Upload the ones in `docs/screenshots/` (or new
  ones) and refresh the description with what is new.
- **Package managers**: being installable with `winget install` is free, trusted discovery. The manifest for
  the current version lives in `installer/winget/` (validated with `winget validate`). To publish it, copy
  `manifests/` into a fork of [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) and open a PR;
  after the first one is accepted, each release is one command:
  `wingetcreate update JuanP-G.MC-ServerLauncher --version X --urls <installer-url> --submit`. Scoop and
  Chocolatey work along the same lines.
- **Listings with their own traffic**: Softpedia, MajorGeeks, SourceForge, Flathub (for the AppImage).
- **Communities**: r/admincraft, r/Minecraft (mind each one's self-promotion rules), Modrinth.

## When a number does not add up

- GoatCounter usually shows fewer visits than other tools because it ignores bots and cannot see people
  who block third-party scripts; that is expected from privacy-respecting measurement.
- Download counters include the ones you make yourself when testing a release.

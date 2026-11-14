#!/usr/bin/env bash
# Opens an issue when Playit publishes a stable agent newer than the one the app pins.
#
# The pinned version and the asset names come from PlayitAgentRunner.cs, so this never needs editing
# when the agent is bumped. Prereleases and drafts are ignored: /releases/latest only returns stable
# releases. One issue per version: if an issue with the same title exists (open or closed), nothing
# is opened again.
#
# Needs: gh (authenticated through GH_TOKEN), jq, sort -V.
# Env:   DRY_RUN=1 prints the issue instead of opening it.
#        ASSIGNEE  login to assign the issue to, so its owner is notified (optional).
set -euo pipefail

runner="McServerLauncher/Services/PlayitAgentRunner.cs"
upstream="playit-cloud/playit-agent"

part() { grep -oP "public const int Version$1 = \K[0-9]+" "$runner"; }
pinned="$(part Major).$(part Minor).$(part Patch)"
mapfile -t assets < <(grep -oP '^\s*\["\K[^"]+(?="\]\s*=)' "$runner")
if [[ ! "$pinned" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ || ${#assets[@]} -eq 0 ]]; then
  echo "::error::Could not read the pinned version or the asset names from $runner"
  exit 1
fi

release="$(gh api "repos/$upstream/releases/latest")"
tag="$(jq -r '.tag_name' <<<"$release")"
latest="${tag#v}"
if [[ ! "$latest" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "::warning::Latest Playit release tag '$tag' is not a plain version; skipping."
  exit 0
fi

echo "Pinned: $pinned  Latest stable: $latest"
if [[ "$latest" == "$pinned" || "$(printf '%s\n%s\n' "$pinned" "$latest" | sort -V | tail -1)" == "$pinned" ]]; then
  echo "Up to date."
  exit 0
fi

title="Playit agent $tag is out (the app pins v$pinned)"
existing="$(gh issue list --state all --search "\"$title\" in:title" --json title --jq "map(select(.title == \"$title\")) | length")"
if [[ "$existing" != "0" ]]; then
  echo "An issue for $tag already exists."
  exit 0
fi

url="$(jq -r '.html_url' <<<"$release")"
published="$(jq -r '.published_at[0:10]' <<<"$release")"
notes="$(jq -r '.body // ""' <<<"$release")"
[[ -z "${notes//[[:space:]]/}" ]] && notes="_No release notes._"

rows=""
missing=0
for a in "${assets[@]}"; do
  digest="$(jq -r --arg n "$a" '.assets[] | select(.name == $n) | .digest // "no digest published"' <<<"$release")"
  if [[ -z "$digest" ]]; then
    rows+="| \`$a\` | **missing from this release** |"$'\n'
    missing=1
  else
    rows+="| \`$a\` | \`${digest#sha256:}\` |"$'\n'
  fi
done

# 2.0 kept the name playit-windows-*.exe but made it the CLI, and shipped the daemon as
# playitd-windows-*.exe. A playitd- twin of an asset we use is the sign of that kind of change.
twins=""
for a in "${assets[@]}"; do
  [[ "$a" == playit-* ]] || continue
  twin="playitd-${a#playit-}"
  if jq -e --arg n "$twin" 'any(.assets[]; .name == $n)' <<<"$release" >/dev/null; then
    twins+="${twins:+, }\`$twin\`"
  fi
done

warning=""
if [[ $missing -eq 1 ]]; then
  warning+=$'> [!WARNING]\n> At least one asset the app downloads is not in this release. Playit may have renamed it: check which asset is the daemon in their `.github/workflows/build-release.yml` before bumping.\n\n'
fi
if [[ -n "$twins" ]]; then
  warning+="> [!WARNING]"$'\n'"> This release also ships $twins. The \`playit-\` asset of the same platform may now be the CLI and not the daemon: check their \`.github/workflows/build-release.yml\`."$'\n'
fi

body="$(cat <<EOF
Playit published a new stable agent: **[$tag]($url)** ($published). The app still pins **v$pinned** in \`$runner\`.

$warning
### Assets the app downloads

| Asset | SHA-256 published by GitHub |
|---|---|
$rows
These digests come from the same release as the binaries, so they only catch a damaged download. Download the assets and hash them yourself before pinning.

### Release notes

$notes

### To update

- [ ] Read what changed (release notes above and the [compare view](https://github.com/$upstream/compare/v$pinned...$tag)), especially \`packages/playitd\`.
- [ ] Check that each asset above is still the daemon (\`playitd\`), not the CLI.
- [ ] Check that \`playitd --help\` still lists \`--secret-path\` and \`--socket-path\`.
- [ ] Bump \`VersionMajor/Minor/Patch\` and \`AssetSha256\` in \`PlayitAgentRunner\` (and \`AssetFor\` if a name changed), and the version in \`playit-proxy/README.md\`.
- [ ] Try a real tunnel on Windows before merging.

_Opened by the \`playit-agent-watch\` workflow. Steps: "Upgrading the agent" in \`playit-proxy/README.md\`._
EOF
)"

if [[ "${DRY_RUN:-0}" == "1" ]]; then
  echo "---- would open: $title"
  echo "$body"
  exit 0
fi

args=(--title "$title" --body "$body" --label dependencies)
[[ -n "${ASSIGNEE:-}" ]] && args+=(--assignee "$ASSIGNEE")
gh issue create "${args[@]}"

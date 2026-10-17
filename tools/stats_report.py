#!/usr/bin/env python3
"""
Resumen de las estadísticas guardadas por tools/collect_stats.py.

    git fetch origin stats
    git worktree add ../stats-data origin/stats       # una sola vez
    python tools/stats_report.py ../stats-data

Imprime descargas por sistema y por versión, cuánto han crecido en 7 y 30 días, visitas al
repositorio y de dónde vienen. Solo biblioteca estándar.
"""

import csv
import datetime
import pathlib
import sys
from collections import defaultdict


def rows(folder, name):
    path = folder / name
    if not path.exists():
        return []
    with path.open(newline="", encoding="utf-8") as handle:
        return list(csv.DictReader(handle))


def system_of(asset):
    lower = asset.lower()
    if lower.endswith(".exe"):
        return "Windows"
    if lower.endswith(".appimage"):
        return "Linux"
    if lower.endswith(".dmg"):
        return "macOS"
    return "otros"


def totals_on(downloads, day):
    """Descargas acumuladas por sistema en la foto más reciente que no pase de `day`."""
    snapshots = sorted({r["date"] for r in downloads if r["date"] <= day})
    if not snapshots:
        return None
    result = defaultdict(int)
    for r in downloads:
        if r["date"] == snapshots[-1]:
            result[system_of(r["asset"])] += int(r["downloads"])
    return result


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    folder = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else "stats-data")
    downloads = rows(folder, "downloads.csv")
    if not downloads:
        sys.exit("No hay datos en %s: ejecuta antes tools/collect_stats.py." % folder)

    latest = max(r["date"] for r in downloads)
    today = datetime.date.fromisoformat(latest)
    now = totals_on(downloads, latest)

    print("== Descargas (foto del %s) ==" % latest)
    def growth(system, days):
        before = totals_on(downloads, str(today - datetime.timedelta(days=days)))
        # Sin una foto tan antigua no hay con qué comparar: mejor «n/d» que un crecimiento inventado.
        return "n/d" if before is None else "+%d" % (now[system] - before.get(system, 0))

    for system in sorted(now, key=now.get, reverse=True):
        print("  %-8s %5d total   %s en 7 días   %s en 30 días"
              % (system, now[system], growth(system, 7), growth(system, 30)))
    print("  %-8s %5d" % ("TOTAL", sum(now.values())))

    by_tag = defaultdict(int)
    for r in downloads:
        if r["date"] == latest:
            by_tag[r["tag"]] += int(r["downloads"])
    print("\n== Por versión (las 8 más descargadas) ==")
    for tag, count in sorted(by_tag.items(), key=lambda kv: -kv[1])[:8]:
        print("  %-10s %d" % (tag, count))

    repo = rows(folder, "repo.csv")
    if repo:
        last = repo[-1]
        print("\n== Repositorio == %s estrellas · %s forks · %s siguiendo"
              % (last["stars"], last["forks"], last["watchers"]))

    views = rows(folder, "views.csv")
    if views:
        last30 = views[-30:]
        print("\n== Visitas al repositorio (últimos %d días guardados) ==" % len(last30))
        print("  %d visitas · %d únicas por día sumadas"
              % (sum(int(v["views"]) for v in last30), sum(int(v["uniques"]) for v in last30)))
    else:
        print("\n(Sin tráfico guardado: falta el secreto TRAFFIC_TOKEN, ver tools/collect_stats.py)")

    referrers = rows(folder, "referrers.csv")
    if referrers:
        day = max(r["date"] for r in referrers)
        print("\n== De dónde vienen (14 días hasta %s) ==" % day)
        for r in sorted((r for r in referrers if r["date"] == day), key=lambda r: -int(r["views"]))[:10]:
            print("  %-34s %s visitas · %s únicas" % (r["referrer"], r["views"], r["uniques"]))


if __name__ == "__main__":
    main()

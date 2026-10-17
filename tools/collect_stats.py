#!/usr/bin/env python3
"""
Guarda una foto diaria de las estadísticas públicas del repositorio en archivos CSV.

    python tools/collect_stats.py --out stats-data

Por qué existe: GitHub solo conserva 14 días de tráfico (visitas, clones, referrers) y los
contadores de descargas son un total acumulado sin historia. Sin guardar una foto cada día,
«cuántas descargas hubo esta semana» o «de dónde vino la gente en septiembre» no se pueden
saber después. Lo ejecuta .github/workflows/stats.yml cada día; los CSV viven en la rama
`stats`, no en `main`.

Variables de entorno:
  GITHUB_TOKEN     obligatoria. Con el token del propio workflow valen las descargas y las
                   estrellas.
  TRAFFIC_TOKEN    opcional. Token personal con permiso de escritura en el repositorio: la API
                   de tráfico lo exige y el token del workflow no basta. Sin él, el tráfico se
                   omite (con un aviso) y todo lo demás se guarda igualmente.
  STATS_REPO       opcional, «propietario/repositorio». Por defecto JuanP-G/MC-ServerLauncher.

Solo usa la biblioteca estándar, para no instalar nada en el workflow.
"""

import argparse
import csv
import datetime
import json
import os
import pathlib
import sys
import urllib.error
import urllib.request

REPO = os.environ.get("STATS_REPO", "JuanP-G/MC-ServerLauncher")
API = "https://api.github.com/repos/" + REPO


def get(path, token):
    request = urllib.request.Request(
        API + path,
        headers={"Authorization": "Bearer " + token,
                 "Accept": "application/vnd.github+json",
                 "X-GitHub-Api-Version": "2022-11-28",
                 "User-Agent": "mc-serverlauncher-stats"})
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def read_rows(path):
    if not path.exists():
        return []
    with path.open(newline="", encoding="utf-8") as handle:
        return list(csv.DictReader(handle))


def write_rows(path, header, rows):
    with path.open("w", newline="", encoding="utf-8") as handle:
        writer = csv.DictWriter(handle, fieldnames=header, lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)


def replace_today(path, header, today, new_rows):
    """Foto del día: sustituye las filas de hoy, así ejecutarlo dos veces no duplica nada."""
    kept = [r for r in read_rows(path) if r["date"] != today]
    write_rows(path, header, kept + new_rows)


def upsert_by_day(path, header, new_rows):
    """Tráfico: GitHub devuelve 14 días con los de hoy aún incompletos; el último dato gana."""
    merged = {r["date"]: r for r in read_rows(path)}
    for row in new_rows:
        merged[row["date"]] = row
    write_rows(path, header, [merged[k] for k in sorted(merged)])


def collect_repo(out, today, token):
    info = get("", token)
    replace_today(out / "repo.csv", ["date", "stars", "forks", "watchers", "open_issues"], today,
                  [{"date": today, "stars": info["stargazers_count"], "forks": info["forks_count"],
                    "watchers": info["subscribers_count"], "open_issues": info["open_issues_count"]}])

    rows = []
    for release in get("/releases?per_page=100", token):
        if release.get("draft"):
            continue
        for asset in release["assets"]:
            if asset["name"].endswith((".sha256", ".txt")):
                continue   # sumas de verificación: no son descargas de la app
            rows.append({"date": today, "tag": release["tag_name"], "asset": asset["name"],
                         "downloads": asset["download_count"]})
    replace_today(out / "downloads.csv", ["date", "tag", "asset", "downloads"], today, rows)
    print("repo: %d estrellas · descargas: %d archivos" % (info["stargazers_count"], len(rows)))


def collect_traffic(out, today, token):
    views = get("/traffic/views", token)
    clones = get("/traffic/clones", token)
    day = lambda stamp: stamp[:10]
    upsert_by_day(out / "views.csv", ["date", "views", "uniques"],
                  [{"date": day(v["timestamp"]), "views": v["count"], "uniques": v["uniques"]}
                   for v in views["views"]])
    upsert_by_day(out / "clones.csv", ["date", "clones", "uniques"],
                  [{"date": day(c["timestamp"]), "clones": c["count"], "uniques": c["uniques"]}
                   for c in clones["clones"]])
    # Referrers y páginas llegan ya sumados a 14 días: se guarda la foto de cada día.
    replace_today(out / "referrers.csv", ["date", "referrer", "views", "uniques"], today,
                  [{"date": today, "referrer": r["referrer"], "views": r["count"],
                    "uniques": r["uniques"]} for r in get("/traffic/popular/referrers", token)])
    replace_today(out / "paths.csv", ["date", "path", "views", "uniques"], today,
                  [{"date": today, "path": p["path"], "views": p["count"], "uniques": p["uniques"]}
                   for p in get("/traffic/popular/paths", token)])
    print("tráfico: %d días de visitas, %d de clones" % (len(views["views"]), len(clones["clones"])))


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--out", required=True, help="carpeta donde se escriben los CSV")
    args = parser.parse_args()

    token = os.environ.get("GITHUB_TOKEN")
    if not token:
        sys.exit("ERROR: falta GITHUB_TOKEN")
    out = pathlib.Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    today = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d")

    collect_repo(out, today, token)

    traffic_token = os.environ.get("TRAFFIC_TOKEN")
    if not traffic_token:
        print("AVISO: sin TRAFFIC_TOKEN no se guarda el tráfico (visitas, clones, referrers). "
              "Ver cabecera de tools/collect_stats.py.")
        return
    try:
        collect_traffic(out, today, traffic_token)
    except urllib.error.HTTPError as err:
        # No se tumba el trabajo entero por esto: las descargas ya están guardadas.
        print("AVISO: la API de tráfico respondió %s; revisa los permisos de TRAFFIC_TOKEN." % err.code)


if __name__ == "__main__":
    main()

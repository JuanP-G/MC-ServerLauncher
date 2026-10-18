#!/usr/bin/env python3
"""Builds the store catalogue the app downloads.

    python tools/store-catalog/build_catalog.py build --out catalog-data
    python tools/store-catalog/build_catalog.py probe sodium
    python tools/store-catalog/build_catalog.py status --out catalog-data

`build` lists the most downloaded Modrinth projects that support a recent Minecraft version, and for
each one that is new, or whose text changed, or that was made by an older pipeline, asks the models
for its topic and its summaries. It stops when the day's free allowance is spent and picks up from
there the next day: every entry is written as soon as it is made, and an entry that is up to date is
never asked about again.

`probe` runs one project and prints what the models answered, raw. Run it first with a new token, or
after changing the questions: it is the quickest way to see an answer's shape or a refusal.

`evaluate.py` measures the same pipeline against the hand-labelled projects. When the pipeline id
changes (a different model, new questions) `build` runs that measurement first and publishes nothing
if it falls below the gate: a model that got worse must not replace one that did not.

Output, in --out: store-catalog.json (readable, diffable), store-catalog.json.gz (what the app
downloads) and meta.json. See catalog_lib.py for the environment variables.
"""

import argparse
import datetime
import gzip
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import catalog_lib as lib  # noqa: E402

CATALOG = "store-catalog.json"
GZIPPED = "store-catalog.json.gz"
META = "meta.json"


def load_catalog(folder):
    path = os.path.join(folder, CATALOG)
    if os.path.exists(path):
        with open(path, encoding="utf-8") as handle:
            data = json.load(handle)
        if isinstance(data.get("projects"), dict):
            return data
    return {"version": 1, "projects": {}}


def save_catalog(folder, catalog, pipeline, changed=True):
    """Writes the three files.

    Sorted keys, no spaces in the gzip and a timestamp that only moves when something was made: a
    run that finds everything current rewrites identical bytes, so the branch has nothing to commit
    and every installed app keeps its ETag instead of downloading megabytes for no news.
    """
    os.makedirs(folder, exist_ok=True)
    catalog["version"] = 1
    catalog["pipeline"] = pipeline
    if changed or "generated" not in catalog:
        catalog["generated"] = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%MZ")

    with open(os.path.join(folder, CATALOG), "w", encoding="utf-8", newline="\n") as handle:
        json.dump(catalog, handle, ensure_ascii=False, sort_keys=True, indent=0)
        handle.write("\n")
    compact = json.dumps(catalog, ensure_ascii=False, sort_keys=True, separators=(",", ":"))
    # mtime=0: the same catalogue is the same bytes, so the app's ETag does not change for nothing.
    with open(os.path.join(folder, GZIPPED), "wb") as handle:
        handle.write(gzip.compress(compact.encode("utf-8"), 9, mtime=0))

    counts = {
        "projects": len(catalog["projects"]),
        "with_topic": sum(1 for e in catalog["projects"].values() if "t" in e),
        "with_summary": sum(1 for e in catalog["projects"].values() if "s" in e),
    }
    meta_path = os.path.join(folder, META)
    meta = json.load(open(meta_path, encoding="utf-8")) if os.path.exists(meta_path) else {}
    meta.update({"pipeline": pipeline, "generated": catalog["generated"], **counts})
    with open(meta_path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(meta, handle, indent=1, sort_keys=True)
        handle.write("\n")
    return counts


def candidates(limit, versions):
    """(project id, kind) for the projects worth having an entry for, most downloaded first."""
    mods = lib.list_projects("mod", versions, limit)
    plugins = lib.list_projects("plugin", versions, limit)
    ordered, seen = [], set()
    # Interleaved, so a short budget does not spend itself all on mods or all on plugins.
    for pair in zip(mods, plugins):
        for kind, pid in zip(("mod", "plugin"), pair):
            if pid not in seen:
                seen.add(pid)
                ordered.append((pid, kind))
    for kind, pids in (("mod", mods[len(plugins):]), ("plugin", plugins[len(mods):])):
        for pid in pids:
            if pid not in seen:
                seen.add(pid)
                ordered.append((pid, kind))
    return ordered


def build(args):
    models = lib.Models(args.text_model, args.budget)
    if not models.configured:
        print("No model configured: set CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_API_TOKEN "
              "(see catalog_lib.py). Nothing was changed.")
        return 0

    topics = lib.load_topics()
    pipeline = lib.pipeline_id(args.text_model, topics)
    catalog = load_catalog(args.out)
    previous = catalog.get("pipeline")

    if previous != pipeline and not args.skip_gate:
        print(f"Pipeline {previous or '(none)'} -> {pipeline}: measuring it before it publishes anything.")
        import evaluate
        passed = evaluate.run(models, topics, gate=args.gate)
        if not passed:
            print("Below the gate: the catalogue was not touched.")
            return 1

    versions = args.versions.split(",") if args.versions else lib.recent_release_versions(3)
    queue = candidates(args.limit, versions)
    print(f"{len(queue)} candidate projects for {', '.join(versions)}; "
          f"{len(catalog['projects'])} already in the catalogue.")

    done = skipped = failed = 0
    stopped = None
    for start in range(0, len(queue), 50):
        chunk = queue[start:start + 50]
        projects = lib.fetch_projects([pid for pid, _ in chunk])
        for pid, kind in chunk:
            project = projects.get(pid)
            if project is None:
                continue
            facts = lib.describe(project, kind)
            fingerprint = lib.fingerprint(facts, pipeline)
            existing = catalog["projects"].get(pid)
            if existing and existing.get("h") == fingerprint:
                skipped += 1
                continue
            if not models.neurons.has_room():
                stopped = "the day's budget is spent"
                break
            try:
                catalog["projects"][pid] = lib.entry_for(models, project, kind, topics, pipeline,
                                                         with_summary=not args.no_summaries)
                done += 1
            except lib.ApiError as err:
                if err.out_of_budget:
                    stopped = "the account's free allowance is spent"
                    break
                failed += 1
                print(f"  ! {project.get('slug', pid)}: {err}", file=sys.stderr)
                if failed >= 10:
                    stopped = "too many failures in a row"
                    break
            if done and done % 25 == 0:
                save_catalog(args.out, catalog, pipeline)
                print(f"  {done} done, ~{models.neurons.spent:.0f} neurons")
        if stopped:
            break

    counts = save_catalog(args.out, catalog, pipeline, changed=done > 0)
    print(f"Done: {done} made, {skipped} already current, {failed} failed"
          f"{', stopped because ' + stopped if stopped else ''}. ~{models.neurons.spent:.0f} neurons. {counts}")
    return 0


def probe(args):
    models = lib.Models(args.text_model, args.budget)
    if not models.configured:
        print("Set CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_API_TOKEN first.")
        return 1
    topics = lib.load_topics()
    project = lib.fetch_projects([args.project])
    if not project:                                        # a slug, not an id
        project = {p["id"]: p for p in [lib.http_json(f"{lib.MODRINTH}/project/{args.project}")]}
    pid, data = next(iter(project.items()))
    kind = lib.project_kind(data)
    print(f"== {data['title']} ({pid}), a {kind}")
    print(json.dumps(lib.describe(data, kind), indent=1, ensure_ascii=False))

    question = lib.topic_question(kind, topics, seed=pid)
    answers = models.decide(lib.describe(data, kind), {"topic": question})
    print("\n-- raw topic answer --")
    print(json.dumps(answers, indent=1, ensure_ascii=False))
    print("-- read as --", lib.read_topic(answers.get("topic"), kind, topics))

    raw = models.write(lib.summary_messages(lib.describe(data, kind), lib.LANGS), lib.summary_schema(lib.LANGS))
    print("\n-- raw summary reply --")
    print(raw if isinstance(raw, str) else json.dumps(raw, indent=1, ensure_ascii=False))
    print("-- after the verifier --", json.dumps(lib.summarise(models, data, kind), ensure_ascii=False))
    print(f"\n~{models.neurons.spent:.1f} neurons")
    return 0


def status(args):
    catalog = load_catalog(args.out)
    entries = catalog["projects"].values()
    print(f"{len(catalog['projects'])} projects, pipeline {catalog.get('pipeline')}, "
          f"generated {catalog.get('generated')}")
    print(f"  with a topic: {sum(1 for e in entries if 't' in e)}; "
          f"with summaries: {sum(1 for e in entries if 's' in e)}")
    return 0


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    def common(p):
        p.add_argument("--out", default="catalog-data", help="folder holding the catalogue")
        p.add_argument("--text-model", default=lib.DEFAULT_TEXT_MODEL)
        p.add_argument("--budget", type=float, default=9000,
                       help="neurons this run may spend (the free plan allows 10,000 a day)")

    b = sub.add_parser("build", help="make or refresh the catalogue")
    common(b)
    b.add_argument("--limit", type=int, default=2000, help="projects per kind, most downloaded first")
    b.add_argument("--versions", help="comma separated Minecraft versions; default: the three newest releases")
    b.add_argument("--no-summaries", action="store_true", help="topics only (about a sixth of the cost)")
    b.add_argument("--gate", type=float, default=0.80, help="accuracy a changed pipeline must reach")
    b.add_argument("--skip-gate", action="store_true")
    b.set_defaults(run=build)

    p = sub.add_parser("probe", help="run one project and print the raw answers")
    common(p)
    p.add_argument("project", help="a project id or slug")
    p.set_defaults(run=probe)

    s = sub.add_parser("status", help="what the catalogue holds")
    s.add_argument("--out", default="catalog-data")
    s.set_defaults(run=status)

    args = parser.parse_args(argv)
    return args.run(args)


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Measures the topic classifier against the hand-labelled projects in golden_set.json.

    python tools/store-catalog/evaluate.py                # prints the report
    python tools/store-catalog/evaluate.py --gate 0.80    # exits 1 when accuracy is lower

A project counts as right when the model's topic is the one labelled `primary` or one of the `ok`
topics. A model that declines to answer ('none', or a probability under the threshold) counts as
wrong here, because for the app an unlabelled project is the failure being fixed.

The table at the end is how the thresholds in catalog_lib.py are meant to be chosen: for each
confidence it shows what share of the projects would get a topic and how often that topic is right.
"""

import argparse
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import catalog_lib as lib  # noqa: E402


def load_golden(path=lib.GOLDEN_FILE):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)["projects"]


def judge(ranked, none_p, label, threshold=lib.MIN_PRIMARY_PROBABILITY):
    """('right' | 'wrong' | 'declined', best topic, its probability) for one project."""
    if not ranked:
        return "declined", None, 0.0
    best_p, best = ranked[0]
    if best_p < threshold or none_p > best_p:
        return "declined", best, best_p
    return ("right" if best == label["primary"] or best in label.get("ok", []) else "wrong"), best, best_p


def run(models, topics, gate=None, limit=None, golden=None):
    """Prints the report. True when the accuracy reaches the gate (or no gate was asked for)."""
    labelled = golden or load_golden()
    if limit:
        labelled = labelled[:limit]
    projects = lib.fetch_projects([p["id"] for p in labelled])

    results = []
    for label in labelled:
        project = projects.get(label["id"])
        if project is None:
            continue
        kind = label.get("kind") or lib.project_kind(project)
        question = lib.topic_question(kind, topics, seed=project["id"])
        try:
            answers = models.decide(lib.describe(project, kind), {"topic": question})
        except lib.ApiError as err:
            print(f"Stopped after {len(results)} projects: {err}", file=sys.stderr)
            return False
        ranked, none_p = lib.rank_topic(answers.get("topic"), kind, topics)
        outcome, best, probability = judge(ranked, none_p, label)
        results.append((label, outcome, best, probability, ranked, none_p))

    if not results:
        print("No project could be fetched.")
        return False

    total = len(results)
    right = sum(1 for r in results if r[1] == "right")
    declined = sum(1 for r in results if r[1] == "declined")
    strict = sum(1 for r in results if r[2] == r[0]["primary"] and r[1] != "declined")
    answered = total - declined
    accuracy = right / total

    print(f"{total} projects. Right: {right} ({accuracy:.1%}). Exactly the primary: {strict} "
          f"({strict / total:.1%}). Declined: {declined}. Right when it answered: "
          f"{(right / answered if answered else 0):.1%}.")

    print("\nBy labelled topic (right / projects):")
    by_tag = {}
    for label, outcome, *_ in results:
        got, n = by_tag.get(label["primary"], (0, 0))
        by_tag[label["primary"]] = (got + (outcome == "right"), n + 1)
    for tag, (got, n) in sorted(by_tag.items(), key=lambda kv: -kv[1][1]):
        print(f"  {tag:<15} {got:>3} / {n:<3}")

    print("\nWhat a confidence threshold would do (answer only above it):")
    print("  threshold  answered  right-when-answered")
    for threshold in (0.30, 0.35, 0.45, 0.55, 0.65, 0.75, 0.85):
        judged = [judge(r[4], r[5], r[0], threshold) for r in results]
        got = sum(1 for j in judged if j[0] == "right")
        said = sum(1 for j in judged if j[0] != "declined")
        print(f"  {threshold:<9.2f}  {said / total:>7.0%}  {(got / said if said else 0):>18.0%}")

    wrong = [r for r in results if r[1] == "wrong"]
    if wrong:
        print(f"\nWrong ({len(wrong)}):")
        for label, _, best, probability, *_ in wrong[:25]:
            ok = "/".join([label["primary"], *label.get("ok", [])])
            print(f"  {label['slug']:<34} expected {ok:<28} got {best} ({probability:.2f})")

    print(f"\n~{models.neurons.spent:.0f} neurons")
    if gate is None:
        return True
    print(f"Gate {gate:.0%}: {'passed' if accuracy >= gate else 'FAILED'}")
    return accuracy >= gate


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--gate", type=float, help="exit 1 below this accuracy")
    parser.add_argument("--limit", type=int, help="only the first N labelled projects")
    parser.add_argument("--budget", type=float, default=9000)
    args = parser.parse_args(argv)

    models = lib.Models(budget=args.budget)
    if not models.configured:
        print("Set CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_API_TOKEN (see catalog_lib.py).")
        return 1
    return 0 if run(models, lib.load_topics(), args.gate, args.limit) else 1


if __name__ == "__main__":
    sys.exit(main())

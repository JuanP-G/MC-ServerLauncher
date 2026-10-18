"""Tests for the catalogue pipeline. No network: the models and Modrinth are replaced.

    python -m unittest discover -s tools/store-catalog

They are about the decisions the pipeline makes, not about whether a model happens to be right
today: that is what evaluate.py is for.
"""

import gzip
import json
import os
import sys
import tempfile
import types
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import build_catalog  # noqa: E402
import catalog_lib as lib  # noqa: E402
import evaluate  # noqa: E402

TOPICS = lib.load_topics()


def choice(**probabilities):
    """The shape of a Clef 'choice' answer, built from the probabilities."""
    best = max(probabilities, key=probabilities.get)
    return {"type": "choice", "choice": best, "probabilities": probabilities,
            "confidence": probabilities[best]}


class FakeModels:
    """Stands in for the decision and text models, with the answers a test chose."""

    def __init__(self, topic=None, summaries=None, unsupported=0.05, faithful=0.95, budget=9000):
        self.topic = topic or choice(storage=0.7, qol=0.25, none=0.05)
        self.summaries = summaries if summaries is not None else {
            "en": "Adds backpacks that hold more items.", "es": "Añade mochilas que guardan más objetos.",
            "de": "Fügt Rucksäcke hinzu, die mehr Gegenstände fassen.",
            "fr": "Ajoute des sacs à dos qui contiennent plus d'objets.",
            "pt": "Adiciona mochilas que guardam mais objetos."}
        self.unsupported, self.faithful = unsupported, faithful
        self.neurons = lib.Neurons(budget)
        self.configured = True
        self.calls = []

    def decide(self, state, questions):
        self.calls.append(("decide", sorted(questions)))
        self.neurons.add(lib.DECISION_MODEL, 700)
        if "topic" in questions:
            return {"topic": self.topic}
        answers = {"unsupported": {"type": "noul", "noul": self.unsupported}}
        for key in questions:
            if key.startswith("faithful_"):
                answers[key] = {"type": "noul", "noul": self.faithful}
        return answers

    def write(self, messages, schema):
        self.calls.append(("write",))
        self.neurons.add(lib.DEFAULT_TEXT_MODEL, 600, 200)
        return json.dumps(self.summaries)


def project(pid="p1", title="Backpacks", description="Adds backpacks.", categories=("storage", "fabric")):
    return {"id": pid, "slug": title.lower(), "title": title, "description": description,
            "categories": list(categories), "additional_categories": [], "loaders": ["fabric"],
            "body": "# Backpacks\n\nStore **more** items. [Docs](https://example.com)"}


class Taxonomy(unittest.TestCase):
    def test_every_topic_of_the_app_has_a_hint_and_no_hint_is_orphaned(self):
        # A tag added to store-tags.json without a line here would be offered to the model as a
        # bare word, and the catalogue would quietly choose it for the wrong reasons.
        ids = {t["id"] for t in TOPICS}
        self.assertEqual(set(), ids - set(lib.TOPIC_HINTS), "tags with no hint")
        self.assertEqual(set(), set(lib.TOPIC_HINTS) - ids, "hints for tags that do not exist")

    def test_a_plugin_is_not_offered_mod_topics_and_the_other_way_round(self):
        self.assertNotIn("economy", lib.topics_for("mod", TOPICS))
        self.assertIn("economy", lib.topics_for("plugin", TOPICS))
        self.assertIn("storage", lib.topics_for("mod", TOPICS))

    def test_the_options_keep_none_last_and_a_project_always_gets_the_same_order(self):
        first = lib.topic_question("mod", TOPICS, seed="abc")
        again = lib.topic_question("mod", TOPICS, seed="abc")
        other = lib.topic_question("mod", TOPICS, seed="xyz")

        self.assertEqual(list(first["criteria"]), list(again["criteria"]))
        self.assertNotEqual(list(first["criteria"]), list(other["criteria"]))
        self.assertEqual("none", list(first["criteria"])[-1])


class ReadingAnswers(unittest.TestCase):
    def test_a_confident_answer_gives_a_topic_and_the_close_runners_up(self):
        primary, secondary, confidence = lib.read_topic(
            choice(storage=0.6, qol=0.25, utility=0.1, none=0.05), "mod", TOPICS)

        self.assertEqual("storage", primary)
        self.assertEqual(["qol"], secondary)
        self.assertEqual(0.6, confidence)

    def test_an_unsure_answer_gives_no_topic_rather_than_a_guess(self):
        self.assertIsNone(lib.read_topic(choice(storage=0.2, qol=0.2, magic=0.2, none=0.4), "mod", TOPICS)[0])

    def test_none_winning_means_no_topic(self):
        self.assertIsNone(lib.read_topic(choice(storage=0.4, none=0.6), "mod", TOPICS)[0])

    def test_a_topic_the_app_does_not_know_is_ignored(self):
        primary, _, _ = lib.read_topic(choice(**{"made-up": 0.9, "storage": 0.1}), "mod", TOPICS)

        self.assertIsNone(primary)

    def test_garbage_is_no_answer(self):
        for answer in (None, "storage", [], {}, {"probabilities": None}):
            self.assertEqual((None, [], 0.0), lib.read_topic(answer, "mod", TOPICS))

    def test_a_mod_cannot_get_a_plugin_only_topic(self):
        primary, _, _ = lib.read_topic(choice(economy=0.9, none=0.1), "mod", TOPICS)

        self.assertIsNone(primary)


class Summaries(unittest.TestCase):
    LANGS = lib.LANGS

    def test_json_inside_chatter_is_found(self):
        reply = 'Sure! Here you go:\n{"en": "Adds backpacks.", "es": "Añade mochilas."}\nHope it helps.'

        self.assertEqual({"en": "Adds backpacks.", "es": "Añade mochilas."},
                         lib.read_summaries(reply, self.LANGS))

    def test_no_english_sentence_means_no_summaries(self):
        self.assertEqual({}, lib.read_summaries('{"en": "", "es": "Añade mochilas."}', self.LANGS))
        self.assertEqual({}, lib.read_summaries("no json here", self.LANGS))
        self.assertEqual({}, lib.read_summaries('{"en": ', self.LANGS))

    def test_marketing_and_long_sentences_are_dropped(self):
        shouty = lib.read_summaries('{"en": "The best mod ever!"}', self.LANGS)
        long_one = lib.read_summaries(json.dumps({"en": "word " * 80}), self.LANGS)

        self.assertEqual({}, shouty)
        self.assertEqual({}, long_one)

    def test_the_verifier_can_throw_everything_away(self):
        kept = lib.check_summaries({"unsupported": {"noul": 0.9}}, {"en": "x", "es": "y"})

        self.assertEqual({}, kept)

    def test_one_bad_translation_costs_only_that_language(self):
        answers = {"unsupported": {"noul": 0.05}, "faithful_es": {"noul": 0.9}, "faithful_de": {"noul": 0.1}}

        kept = lib.check_summaries(answers, {"en": "a", "es": "b", "de": "c"})

        # "faithful" is the probability that the translation says the same: Spanish passes, German does not.
        self.assertEqual({"en": "a", "es": "b"}, kept)

    def test_a_missing_answer_is_not_trusted(self):
        kept = lib.check_summaries({"unsupported": {"noul": 0.05}}, {"en": "a", "es": "b"})

        self.assertEqual({"en": "a"}, kept)

    def test_the_body_is_reduced_to_plain_words(self):
        text = lib.clean_body("# Title\n\n![img](http://x/y.png) Some **bold** [link](http://z) <b>html</b>")

        self.assertEqual("Title Some bold link html", text)


class Identity(unittest.TestCase):
    def test_the_fingerprint_follows_the_text_and_the_pipeline(self):
        facts = lib.describe(project(), "mod")
        changed = lib.describe(project(description="Adds big backpacks."), "mod")

        self.assertEqual(lib.fingerprint(facts, "a"), lib.fingerprint(facts, "a"))
        self.assertNotEqual(lib.fingerprint(facts, "a"), lib.fingerprint(changed, "a"))
        self.assertNotEqual(lib.fingerprint(facts, "a"), lib.fingerprint(facts, "b"))

    def test_platform_names_are_not_shown_as_subjects(self):
        facts = lib.describe(project(categories=("storage", "fabric", "forge", "datapack")), "mod")

        self.assertEqual(["storage"], facts["categories_ticked_by_the_author"])

    def test_a_different_model_is_a_different_pipeline(self):
        self.assertNotEqual(lib.pipeline_id("model-a", TOPICS), lib.pipeline_id("model-b", TOPICS))


class Budget(unittest.TestCase):
    def test_spending_is_counted_by_the_published_rates(self):
        neurons = lib.Neurons(10)
        neurons.add("clef-flash", 1_000_000)

        self.assertAlmostEqual(3455, neurons.spent)
        self.assertFalse(neurons.has_room())


class OneProject(unittest.TestCase):
    def test_an_entry_carries_the_topic_the_summaries_and_what_made_it(self):
        entry = lib.entry_for(FakeModels(), project(), "mod", TOPICS, "pipe")

        self.assertEqual("storage", entry["t"])
        self.assertEqual(["qol"], entry["x"])
        self.assertEqual("Adds backpacks that hold more items.", entry["s"]["en"])
        self.assertEqual("pipe", entry["p"])
        self.assertTrue(entry["h"])

    def test_an_unsure_model_leaves_the_topic_out_but_keeps_the_summary(self):
        entry = lib.entry_for(FakeModels(topic=choice(storage=0.2, qol=0.2, none=0.6)),
                              project(), "mod", TOPICS, "pipe")

        self.assertNotIn("t", entry)
        self.assertIn("s", entry)

    def test_a_summary_the_verifier_rejects_never_reaches_the_catalogue(self):
        entry = lib.entry_for(FakeModels(unsupported=0.9), project(), "mod", TOPICS, "pipe")

        self.assertNotIn("s", entry)

    def test_a_project_with_no_text_is_not_summarised(self):
        models = FakeModels()
        bare = project(description="")
        bare["body"] = ""

        entry = lib.entry_for(models, bare, "mod", TOPICS, "pipe")

        self.assertNotIn("s", entry)
        self.assertNotIn(("write",), models.calls)


class Building(unittest.TestCase):
    """The whole `build`, with Modrinth and the models replaced."""

    def setUp(self):
        self.folder = tempfile.mkdtemp(prefix="catalog-test-")
        self.projects = {"p1": project("p1", "Backpacks"), "p2": project("p2", "Chests", "Adds chests."),
                         "p3": project("p3", "Vaults", "Adds vaults.")}
        self.models = FakeModels()
        self._saved = (lib.list_projects, lib.fetch_projects, lib.recent_release_versions, lib.Models)
        lib.list_projects = lambda kind, versions, limit: list(self.projects) if kind == "mod" else []
        lib.fetch_projects = lambda ids: {i: self.projects[i] for i in ids if i in self.projects}
        lib.recent_release_versions = lambda count=3: ["26.2"]
        lib.Models = lambda *a, **k: self.models

    def tearDown(self):
        lib.list_projects, lib.fetch_projects, lib.recent_release_versions, lib.Models = self._saved

    def run_build(self, **overrides):
        args = types.SimpleNamespace(out=self.folder, text_model=lib.DEFAULT_TEXT_MODEL, budget=9000, limit=100,
                                     versions=None, no_summaries=False, gate=0.8, skip_gate=True)
        vars(args).update(overrides)
        return build_catalog.build(args)

    def catalogue(self):
        return json.load(open(os.path.join(self.folder, build_catalog.CATALOG), encoding="utf-8"))

    def test_it_writes_the_readable_file_the_gzip_and_the_meta(self):
        self.assertEqual(0, self.run_build())

        names = sorted(os.listdir(self.folder))
        self.assertEqual(["meta.json", "store-catalog.json", "store-catalog.json.gz"], names)
        zipped = json.loads(gzip.decompress(open(os.path.join(self.folder, "store-catalog.json.gz"), "rb").read()))
        self.assertEqual(self.catalogue(), zipped)
        self.assertEqual({"p1", "p2", "p3"}, set(zipped["projects"]))

    def test_a_second_run_asks_the_models_nothing_new(self):
        self.run_build()
        calls = len(self.models.calls)

        self.run_build()

        self.assertEqual(calls, len(self.models.calls))

    def test_the_same_catalogue_is_the_same_bytes(self):
        self.run_build()
        first = open(os.path.join(self.folder, "store-catalog.json.gz"), "rb").read()
        meta = open(os.path.join(self.folder, "meta.json"), "rb").read()

        self.run_build()

        # A run with nothing new must leave nothing to commit and every app's ETag as it was.
        self.assertEqual(first, open(os.path.join(self.folder, "store-catalog.json.gz"), "rb").read())
        self.assertEqual(meta, open(os.path.join(self.folder, "meta.json"), "rb").read())

    def test_only_the_project_whose_text_changed_is_asked_again(self):
        self.run_build()
        before = len(self.models.calls)
        self.projects["p2"]["description"] = "Adds big chests."

        self.run_build()

        self.assertEqual(3, len(self.models.calls) - before)       # topic, summary, verification

    def test_a_small_budget_stops_the_run_and_keeps_what_it_made(self):
        self.models = FakeModels(budget=30)

        self.run_build(budget=30)

        made = self.catalogue()["projects"]
        self.assertGreaterEqual(len(made), 1)
        self.assertLess(len(made), 3)

    def test_without_a_model_nothing_is_touched(self):
        self.models.configured = False

        self.assertEqual(0, self.run_build())
        self.assertEqual([], os.listdir(self.folder))

    def test_a_changed_pipeline_below_the_gate_publishes_nothing(self):
        self.run_build()
        before = self.catalogue()
        saved = evaluate.run
        evaluate.run = lambda *a, **k: False
        try:
            result = self.run_build(text_model="another-model", skip_gate=False)
        finally:
            evaluate.run = saved

        self.assertEqual(1, result)
        self.assertEqual(before, self.catalogue())


class Measuring(unittest.TestCase):
    def test_the_golden_set_is_made_of_topics_the_app_has(self):
        golden = evaluate.load_golden()
        ids = {t["id"] for t in TOPICS}

        self.assertGreaterEqual(len(golden), 100)
        self.assertEqual(len(golden), len({p["id"] for p in golden}), "duplicate project")
        for p in golden:
            self.assertIn(p["primary"], ids, p["slug"])
            for other in p.get("ok", []):
                self.assertIn(other, ids, p["slug"])

    def test_an_alternative_label_is_not_an_error(self):
        label = {"primary": "qol", "ok": ["utility"]}

        self.assertEqual("right", evaluate.judge([(0.7, "utility"), (0.2, "qol")], 0.0, label)[0])
        self.assertEqual("wrong", evaluate.judge([(0.7, "combat")], 0.0, label)[0])
        self.assertEqual("declined", evaluate.judge([(0.2, "qol")], 0.0, label)[0])
        self.assertEqual("declined", evaluate.judge([(0.5, "qol")], 0.6, label)[0])


if __name__ == "__main__":
    unittest.main()

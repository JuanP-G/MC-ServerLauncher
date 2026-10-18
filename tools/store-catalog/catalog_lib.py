"""Shared pieces of the store catalogue pipeline.

The catalogue is a JSON file that says, for each Modrinth project the app is likely to show, which
topic it is about and what it does, in the five languages the app speaks. It is built here, outside
the app, by two kinds of model:

* a *decision* model (Cloudflare's Clef, or anything that speaks the same "state + questions"
  protocol, Jev included) answers closed questions and returns a probability per option. It picks the
  topic and checks the summaries; it cannot write;
* a small *text* model writes the one-sentence summary and translates it.

Nothing in the app calls either. The app downloads the finished file (see StoreCatalogService), so
there is no key in the app, nothing is sent from a player's machine, and the whole thing costs what
the free allowance of the account running this costs: nothing.

Only the standard library is used, so the workflow installs nothing.
"""

import hashlib
import json
import os
import random
import re
import time
import urllib.error
import urllib.parse
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.normpath(os.path.join(HERE, "..", ".."))
TAGS_FILE = os.path.join(REPO, "McServerLauncher", "Resources", "store-tags.json")
GOLDEN_FILE = os.path.join(HERE, "golden_set.json")

MODRINTH = "https://api.modrinth.com/v2"
USER_AGENT = {"User-Agent": "JuanP-G/MC-ServerLauncher store-catalog"}
LANGS = ("en", "es", "de", "fr", "pt")
LANG_NAMES = {"en": "English", "es": "Spanish", "de": "German", "fr": "French", "pt": "Portuguese"}

# Bump when the questions, the prompts or the way answers are read change in a way that should make
# every entry be looked at again. The model ids are part of the pipeline id as well.
PIPELINE_REVISION = 1

DECISION_MODEL = "clef-flash"
DEFAULT_TEXT_MODEL = "@cf/meta/llama-3.1-8b-instruct-fp8-fast"

# A topic is only claimed when the model puts at least this much probability on it. Chosen from the
# golden set: `evaluate.py` prints how accuracy moves with it, which is how to change it.
MIN_PRIMARY_PROBABILITY = 0.35
MIN_SECONDARY_PROBABILITY = 0.20
MAX_SECONDARY = 2

# A summary is dropped when the verifier thinks it says more than the text supports.
MAX_UNSUPPORTED = 0.35
MIN_FAITHFUL = 0.65
MAX_SUMMARY_CHARS = 170

# Workers AI bills in neurons, and the free plan stops at 10,000 a day. Rates are per million tokens.
NEURONS_PER_MILLION = {
    "clef-flash": (3455, 0),
    "clef": (21818, 0),
    "@cf/meta/llama-3.1-8b-instruct-fp8-fast": (4119, 34868),
    "@cf/meta/llama-3.3-70b-instruct-fp8-fast": (26668, 204805),
    "@cf/google/gemma-4-26b-a4b-it": (9091, 27273),
    "@cf/qwen/qwen3-30b-a3b-fp8": (4625, 30475),
}

# What each topic means, in the words the decision model is asked to choose between. One line each,
# written for a stranger who has never seen the app. A tag added to store-tags.json must be added
# here too: test_catalog_lib fails otherwise, which is the point.
TOPIC_HINTS = {
    "performance": "Makes the game or the server run faster or use less memory (FPS, TPS, lag, chunk loading).",
    "library": "A library or API that other mods need. It adds nothing to play with by itself.",
    "worldgen": "Changes how the world is generated: terrain, ores, dimensions, world types.",
    "biomes": "Adds or changes biomes.",
    "structures": "Adds or changes generated structures: villages, dungeons, ruins, towers.",
    "mobs": "Adds or changes mobs, animals, villagers, bosses or their behaviour.",
    "technology": "Machines, automation, redstone, energy, pipes, factories.",
    "magic": "Magic systems: spells, wands, rituals, mana, enchanting overhauls.",
    "adventure": "Quests, progression, RPG systems, skills, dungeons to explore, new gameplay goals.",
    "exploration": "Helps to explore and find things: maps, minimaps, waypoints, locators.",
    "combat": "Changes fighting: weapons, melee or ranged combat, PvP, combat mechanics.",
    "equipment": "Adds or changes armor, tools, trinkets or other items players wear and carry.",
    "storage": "Backpacks, chests, drawers, inventory sorting and other ways to store items.",
    "decoration": "Visual and cosmetic changes: decorative blocks, shaders, textures, animations, player models, sounds.",
    "building": "Helps building: block placement, schematics, world editing, building tools.",
    "farming": "Crops, farms, harvesting, trees, animals as a resource.",
    "food": "Food, cooking, hunger and nutrition.",
    "transportation": "Moving around: teleporting, warps, vehicles, elytra, portals, home commands.",
    "economy": "Money, shops, trading, auctions, selling.",
    "protection": "Protects land and builds from griefing: claims, regions, rollback of damage.",
    "moderation": "Tools for staff: anti-cheat, bans, mutes, punishments, logging of player actions.",
    "permissions": "Permissions, groups and ranks that decide who can do what.",
    "social": "Chat, voice chat, messaging, Discord links, friends and parties.",
    "minigames": "Minigames, arenas, events, lobbies, game modes.",
    "management": "Helps the owner run a server: admin tools, configuration, world management, web maps, monitoring.",
    "backup": "Backups and restoring the world.",
    "compatibility": "Lets other versions or platforms connect: older clients, Bedrock players, cross-play.",
    "qol": "Small improvements to how the game plays or feels, without changing what it is (quality of life).",
    "utility": "A general-purpose tool that fits none of the other topics.",
}

NONE_HINT = "The text says too little to tell what this does, or it fits none of the topics."

# Words in a project's category list that name a platform, not a subject.
PLATFORM_CATEGORIES = {
    "fabric", "forge", "neoforge", "quilt", "paper", "spigot", "bukkit", "purpur", "folia",
    "velocity", "bungeecord", "waterfall", "sponge", "datapack", "liteloader", "ornithe", "rift",
    "babric", "bta-babric", "modloader", "java-agent", "legacy-fabric", "nilloader", "geyser",
    "minecraft", "iris", "optifine", "vanilla", "canvas",
}

# --- The taxonomy: store-tags.json is the one list of topics, so the app and the catalogue agree ---


def load_topics(path=TAGS_FILE):
    """The topic tags of the app, in catalogue order: [{id, projectTypes}]."""
    with open(path, encoding="utf-8") as handle:
        tags = json.load(handle)["tags"]
    topics = [t for t in tags if t.get("kind", "topic") == "topic"]
    return sorted(topics, key=lambda t: t.get("priority", 100))


def topics_for(kind, topics):
    """The topic ids that exist for a mod or for a plugin."""
    return [t["id"] for t in topics if not t.get("projectTypes") or kind in t["projectTypes"]]


# --- Modrinth ------------------------------------------------------------------------------------


def http_json(url, data=None, headers=None, timeout=60, retries=3):
    """GET or POST JSON, retrying the failures worth retrying. Raises ApiError on the rest."""
    body = None if data is None else json.dumps(data).encode("utf-8")
    all_headers = dict(USER_AGENT)
    all_headers.update(headers or {})
    if body is not None:
        all_headers["Content-Type"] = "application/json"

    for attempt in range(retries + 1):
        request = urllib.request.Request(url, data=body, headers=all_headers)
        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                return json.load(response)
        except urllib.error.HTTPError as err:
            text = err.read().decode("utf-8", "replace")
            retryable = err.code in (429, 500, 502, 503, 504) and "daily" not in text.lower()
            if retryable and attempt < retries:
                time.sleep(min(30, 2 ** attempt * 2))
                continue
            raise ApiError(err.code, text) from None
        except (urllib.error.URLError, TimeoutError) as err:
            if attempt < retries:
                time.sleep(2 ** attempt * 2)
                continue
            raise ApiError(0, str(err)) from None


class ApiError(Exception):
    def __init__(self, status, text):
        super().__init__(f"HTTP {status}: {text[:300]}")
        self.status = status
        self.text = text

    @property
    def out_of_budget(self):
        """The account's free allowance for the day is spent: stop, do not retry."""
        lowered = self.text.lower()
        return "daily free allocation" in lowered or "4006" in lowered


def recent_release_versions(count=3):
    """The newest release versions of Minecraft, newest first."""
    versions = http_json(f"{MODRINTH}/tag/game_version")
    releases = [v["version"] for v in versions if v.get("version_type") == "release"]
    return releases[:count]


def list_projects(project_type, versions, limit):
    """Ids of the most downloaded projects that support any of the given versions."""
    groups = [[f"versions:{v}" for v in versions], [f"project_type:{project_type}"]]
    ids = []
    for offset in range(0, limit, 100):
        query = urllib.parse.urlencode({
            "facets": json.dumps(groups), "index": "downloads",
            "limit": min(100, limit - offset), "offset": offset})
        hits = http_json(f"{MODRINTH}/search?{query}").get("hits", [])
        ids.extend(h["project_id"] for h in hits)
        if len(hits) < 100:
            break
    return ids


def fetch_projects(ids):
    """Full projects by id, 50 at a time (the bulk endpoint's comfortable size)."""
    found = {}
    for start in range(0, len(ids), 50):
        chunk = ids[start:start + 50]
        url = f"{MODRINTH}/projects?ids=" + urllib.parse.quote(json.dumps(chunk))
        for project in http_json(url):
            found[project["id"]] = project
    return found


# --- What the models are shown -------------------------------------------------------------------

_MARKDOWN_NOISE = [
    (re.compile(r"<[^>]+>"), " "),                      # html
    (re.compile(r"!\[[^\]]*\]\([^)]*\)"), " "),         # images
    (re.compile(r"\[([^\]]*)\]\([^)]*\)"), r"\1"),      # links keep their text
    (re.compile(r"https?://\S+"), " "),
    (re.compile(r"[#>*_`|~-]{1,}"), " "),
    (re.compile(r"\s+"), " "),
]


def clean_body(body, limit=900):
    """The start of a project's long description, as plain text. Enough to tell what it is."""
    text = body or ""
    for pattern, replacement in _MARKDOWN_NOISE:
        text = pattern.sub(replacement, text)
    return text.strip()[:limit]


def project_kind(project, hits_kind=None):
    """'plugin' for what runs on a plugin server, 'mod' for the rest."""
    if hits_kind:
        return hits_kind
    loaders = set(project.get("loaders", []))
    mod_loaders = {"fabric", "forge", "neoforge", "quilt"}
    plugin_loaders = {"paper", "spigot", "bukkit", "purpur", "folia"}
    return "plugin" if loaders & plugin_loaders and not loaders & mod_loaders else "mod"


def describe(project, kind):
    """The facts about a project the models get to see. Nothing the author did not write."""
    categories = [c for c in project.get("categories", []) + project.get("additional_categories", [])
                  if c not in PLATFORM_CATEGORIES]
    return {
        "name": project.get("title", ""),
        "kind": "server plugin" if kind == "plugin" else "game mod",
        "one_line_description": (project.get("description") or "").strip(),
        "categories_ticked_by_the_author": sorted(set(categories)),
        "details": clean_body(project.get("body")),
    }


def fingerprint(facts, pipeline):
    """Changes when what the models would see changes, or when the pipeline does."""
    blob = json.dumps(facts, sort_keys=True, ensure_ascii=False) + "|" + pipeline
    return hashlib.sha1(blob.encode("utf-8")).hexdigest()[:12]


def pipeline_id(text_model, topics):
    """What a catalogue entry was made with: changing any of it makes entries stale."""
    parts = [str(PIPELINE_REVISION), DECISION_MODEL, text_model,
             ",".join(t["id"] for t in topics), json.dumps(TOPIC_HINTS, sort_keys=True)]
    return hashlib.sha1("|".join(parts).encode("utf-8")).hexdigest()[:10]


# --- Questions and how their answers are read ------------------------------------------------------


def topic_question(kind, topics, seed):
    """The one closed question that picks the topic.

    The options are shuffled, with a seed taken from the project: a decision model leans towards
    the first option when it knows nothing, and a fixed order would turn that lean into the same
    topic for every project it was unsure about. 'none' stays last, as the way out.
    """
    ids = topics_for(kind, topics)
    random.Random(seed).shuffle(ids)
    criteria = {tag: TOPIC_HINTS[tag] for tag in ids}
    criteria["none"] = NONE_HINT
    return {
        "type": "choice",
        "instructions": (
            f"Which ONE topic best describes what this {'server plugin' if kind == 'plugin' else 'Minecraft mod'} "
            "does for the people who use it? Judge by what the text says it does. The categories the author "
            "ticked are a hint at most: authors tick many. Answer 'none' only if the text says too little."),
        "criteria": criteria,
    }


def rank_topic(answer, kind, topics):
    """The topics of a choice answer, most likely first, and the probability put on 'none'."""
    if not isinstance(answer, dict):
        return [], 0.0
    probabilities = answer.get("probabilities") or {}
    valid = set(topics_for(kind, topics))
    ranked = sorted(((p, tag) for tag, p in probabilities.items() if tag in valid), reverse=True)
    return ranked, float(probabilities.get("none", 0.0))


def read_topic(answer, kind, topics):
    """Turns a choice answer into (primary, secondary, confidence). Primary is None when unsure."""
    ranked, none_p = rank_topic(answer, kind, topics)
    if not ranked:
        return None, [], 0.0

    best_p, best = ranked[0]
    primary = best if best_p >= MIN_PRIMARY_PROBABILITY and best_p >= none_p else None
    secondary = []
    if primary is not None:
        secondary = [tag for p, tag in ranked[1:1 + MAX_SECONDARY] if p >= MIN_SECONDARY_PROBABILITY]
    return primary, secondary, round(float(best_p), 3)


def verify_questions(languages):
    """Yes/no questions that check the summaries against the text and against each other."""
    questions = {
        "unsupported": {
            "type": "noul",
            "instructions": ("Does the English summary state or imply anything that the one-line description "
                             "and the details do not say?")},
    }
    for lang in languages:
        if lang == "en":
            continue
        questions[f"faithful_{lang}"] = {
            "type": "noul",
            "instructions": (f"Does the {LANG_NAMES[lang]} summary say exactly the same as the English "
                             "summary, with nothing added and nothing missing?")}
    return questions


def check_summaries(answers, summaries):
    """Keeps the summaries the verifier trusts. English failing drops them all."""
    def probability(key):
        value = (answers or {}).get(key)
        return float(value.get("noul", 0.5)) if isinstance(value, dict) else 0.5

    if probability("unsupported") > MAX_UNSUPPORTED:
        return {}
    kept = {"en": summaries["en"]}
    for lang, text in summaries.items():
        if lang != "en" and probability(f"faithful_{lang}") >= MIN_FAITHFUL:
            kept[lang] = text
    return kept


SUMMARY_SYSTEM = (
    "You write one-sentence descriptions of Minecraft mods and server plugins for a server owner who "
    "is deciding whether to install one. Use ONLY facts stated in the text you are given. Never add a "
    "feature, a number, a version or a loader that the text does not mention. Third person, present "
    "tense, plain words, no marketing, no exclamation marks, no 'you' and no emoji. One sentence, at "
    f"most {MAX_SUMMARY_CHARS} characters, that says what it does and, when the text says so, what for. "
    "If the text does not say what it does, return empty strings."
)


def summary_messages(facts, languages):
    names = ", ".join(f'"{lang}" ({LANG_NAMES[lang]})' for lang in languages)
    user = (
        "Text about the project, as JSON:\n" + json.dumps(facts, ensure_ascii=False) + "\n\n"
        f"Reply with ONE JSON object whose keys are {names}. Each value is the same sentence in that "
        "language, a faithful translation of the English one. Keep names of mods and mechanics as they are.")
    return [{"role": "system", "content": SUMMARY_SYSTEM}, {"role": "user", "content": user}]


def summary_schema(languages):
    return {
        "type": "json_schema",
        "json_schema": {
            "name": "summaries",
            "schema": {
                "type": "object",
                "properties": {lang: {"type": "string"} for lang in languages},
                "required": list(languages),
            },
        },
    }


def read_summaries(raw, languages):
    """The JSON object in a model's reply, cleaned: {} when there is no usable English sentence."""
    if isinstance(raw, dict):
        data = raw
    else:
        match = re.search(r"\{.*\}", raw or "", re.DOTALL)
        if not match:
            return {}
        try:
            data = json.loads(match.group(0))
        except ValueError:
            return {}

    clean = {}
    for lang in languages:
        text = " ".join(str(data.get(lang, "")).split())
        if text and len(text) <= MAX_SUMMARY_CHARS + 40 and "!" not in text:
            clean[lang] = text
    return clean if clean.get("en") else {}


# --- The models ----------------------------------------------------------------------------------


class Neurons:
    """Counts what a run has spent, so it can stop before the account's free day is over."""

    def __init__(self, budget):
        self.budget = budget
        self.spent = 0.0

    def add(self, model, input_tokens, output_tokens=0):
        rate_in, rate_out = NEURONS_PER_MILLION.get(model, (30000, 200000))
        self.spent += input_tokens * rate_in / 1e6 + output_tokens * rate_out / 1e6

    @property
    def left(self):
        return self.budget - self.spent

    def has_room(self, estimate=25):
        return self.left >= estimate


class Models:
    """Talks to the decision model and the text model. Cloudflare by default.

    Environment:
      CLOUDFLARE_ACCOUNT_ID, CLOUDFLARE_API_TOKEN  Workers AI (a token with the "Workers AI" permission).
      DECISION_URL    optional; any server speaking the same protocol (Kev or Jebadiah locally,
                      "http://localhost:8000/v1/systemone"). Replaces Cloudflare for decisions.
      DECISION_TOKEN  optional bearer token for DECISION_URL.
      TEXT_URL        optional; an Ollama server ("http://localhost:11434"). Replaces Cloudflare for text.
    """

    def __init__(self, text_model=DEFAULT_TEXT_MODEL, budget=9000, environ=None):
        env = os.environ if environ is None else environ
        self.account = env.get("CLOUDFLARE_ACCOUNT_ID", "")
        self.token = env.get("CLOUDFLARE_API_TOKEN", "")
        self.decision_url = env.get("DECISION_URL", "")
        self.decision_token = env.get("DECISION_TOKEN", "")
        self.text_url = env.get("TEXT_URL", "")
        self.text_model = text_model
        self.neurons = Neurons(budget)

    @property
    def configured(self):
        cloudflare = bool(self.account and self.token)
        return (cloudflare or bool(self.decision_url)) and (cloudflare or bool(self.text_url))

    def _cloudflare(self, model_path, payload):
        url = f"https://api.cloudflare.com/client/v4/accounts/{self.account}/ai/run/{model_path}"
        reply = http_json(url, payload, {"Authorization": f"Bearer {self.token}"})
        if isinstance(reply, dict) and reply.get("success") is False:
            raise ApiError(400, json.dumps(reply.get("errors")))
        return reply.get("result", reply) if isinstance(reply, dict) else reply

    def decide(self, state, questions):
        """Asks the decision model. Returns {question id: answer}."""
        payload = {"model": DECISION_MODEL, "state": state, "questions": questions}
        if self.decision_url:
            headers = {"Authorization": f"Bearer {self.decision_token}"} if self.decision_token else {}
            result = http_json(self.decision_url, payload, headers)
        else:
            result = self._cloudflare(f"@cf/cloudflare/{DECISION_MODEL}", payload)
        usage = (result or {}).get("usage", {})
        self.neurons.add(DECISION_MODEL, usage.get("input_tokens", len(json.dumps(payload)) // 3))
        return (result or {}).get("answers", {})

    def write(self, messages, schema):
        """Asks the text model for JSON. Returns the raw reply (a dict or a string)."""
        if self.text_url:
            reply = http_json(self.text_url.rstrip("/") + "/api/chat",
                              {"model": self.text_model, "messages": messages, "stream": False,
                               "format": "json", "options": {"temperature": 0.2}})
            return (reply.get("message") or {}).get("content", "")

        payload = {"messages": messages, "max_tokens": 700, "temperature": 0.2, "response_format": schema}
        try:
            result = self._cloudflare(self.text_model, payload)
        except ApiError as err:
            if err.out_of_budget or err.status not in (400, 422):
                raise
            payload.pop("response_format")      # this model does not take a schema: ask in the prompt only
            result = self._cloudflare(self.text_model, payload)

        usage = (result or {}).get("usage", {}) if isinstance(result, dict) else {}
        self.neurons.add(self.text_model,
                         usage.get("prompt_tokens", len(json.dumps(messages)) // 3),
                         usage.get("completion_tokens", 120))
        return (result or {}).get("response", result) if isinstance(result, dict) else result


# --- One project through the whole pipeline ------------------------------------------------------


def classify(models, project, kind, topics):
    """Topic of one project: (primary, secondary, confidence)."""
    facts = describe(project, kind)
    question = topic_question(kind, topics, seed=project["id"])
    answers = models.decide(facts, {"topic": question})
    return read_topic(answers.get("topic"), kind, topics)


def summarise(models, project, kind, languages=LANGS):
    """Summaries in every language the verifier trusts. {} when there is nothing trustworthy."""
    facts = describe(project, kind)
    if not facts["one_line_description"] and not facts["details"]:
        return {}

    raw = models.write(summary_messages(facts, languages), summary_schema(languages))
    summaries = read_summaries(raw, languages)
    if not summaries:
        return {}

    judged = {
        "one_line_description": facts["one_line_description"],
        "details": facts["details"],
        **{f"summary_{lang}": text for lang, text in summaries.items()},
    }
    answers = models.decide(judged, verify_questions([l for l in languages if l in summaries]))
    return check_summaries(answers, summaries)


def entry_for(models, project, kind, topics, pipeline, with_summary=True):
    """The catalogue entry of one project, or None when nothing could be said about it."""
    primary, secondary, confidence = classify(models, project, kind, topics)
    summaries = summarise(models, project, kind) if with_summary else {}
    entry = {"h": fingerprint(describe(project, kind), pipeline), "p": pipeline, "c": confidence}
    if primary:
        entry["t"] = primary
        if secondary:
            entry["x"] = secondary
    if summaries:
        entry["s"] = summaries
    return entry

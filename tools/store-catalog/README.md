# Store catalogue

For each popular Modrinth project, which of the app's topics it is about and a one-sentence summary in
es, en, de, fr and pt. Built here, outside the app, and downloaded by the app as one gzip — see
`StoreCatalogService` and the *store* section of `docs/articles/architecture.md`.

It costs nothing: Cloudflare Workers AI gives an account 10,000 neurons a day and, on the free plan,
stops answering when they are gone instead of charging.

| What | Model | About what one project costs | Projects a day, free |
|---|---|---|---|
| Topic | `clef-flash` (decision, 9B) | 1–3 neurons | thousands |
| Summary + 4 translations | `@cf/meta/llama-3.1-8b-instruct-fp8-fast` (text) | ≈ 10 neurons | ≈ 600 |
| Check of the summaries | `clef-flash` | 1–2 neurons | thousands |

So a first fill of two thousand projects takes a few days, after which only new or changed projects
are asked about. `build --no-summaries` does topics only, about a sixth of the cost.

## Setting it up

1. Create a Cloudflare API token with the **Workers AI** permission (Read is enough) and note the
   account id.
2. Add them to the repository as the secrets `CLOUDFLARE_ACCOUNT_ID` and `CLOUDFLARE_API_TOKEN`.
   `.github/workflows/store-catalog.yml` runs daily and does nothing, successfully, until they exist.
3. Before trusting it, run it by hand with the same two variables set:

```bash
python tools/store-catalog/build_catalog.py probe sodium      # one project, raw answers
python tools/store-catalog/evaluate.py                        # accuracy on the hand-labelled projects
python tools/store-catalog/build_catalog.py build --out catalog-data --limit 50
```

`probe` prints exactly what the models answered. It is the first thing to run with a new token and
after any change to the questions, because the shape of an answer is the one thing a unit test cannot
check against the real service.

## Choosing the thresholds

`evaluate.py` ends with a table: for each confidence, what share of the labelled projects would get a
topic and how often that topic is right. `MIN_PRIMARY_PROBABILITY` in `catalog_lib.py` is meant to be
read off that table, not guessed. A topic left out is better than a wrong one: the app falls back to its
own rules for it.

## Changing the topics

The topics are the `kind: topic` tags of `McServerLauncher/Resources/store-tags.json`, so the app and the
catalogue always speak about the same ones. A tag added there needs a line in `TOPIC_HINTS`
(`catalog_lib.py`); `test_catalog_lib.py` fails until it has one. Changing a hint, a prompt or a model
changes the pipeline id, which makes `build` measure it against `golden_set.json` before it replaces
anything, and re-ask the projects gradually afterwards.

## Using something else

Anything that speaks the same "state + questions" protocol can decide the topics, locally or hosted:

```bash
DECISION_URL=http://localhost:8000/v1/systemone   # Kev, Jebadiah, a Jev-compatible server
TEXT_URL=http://localhost:11434                   # Ollama, for the summaries
```

Jev itself is a paid hosted service with restricted access and no published weights, so it is not the
default; point `DECISION_URL` at it if you have a key (`DECISION_TOKEN`).

## The hand-labelled projects

`golden_set.json` holds about 190 projects with the topic a server owner would expect to see first
(`primary`) and other topics that are just as right (`ok`). The labels are one person's judgement:
argue with them in a pull request. They lean towards popular projects, because those are what the
store shows first.

## Tests

```bash
python -m unittest discover -s tools/store-catalog
```

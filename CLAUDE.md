# MC Server Launcher — instructions for Claude

Read `.github/copilot-instructions.md` first: it holds this repository's conventions (architecture,
style, commits, documentation, security) and applies to you in full. This file only adds what must
not be missed.

## Pull requests are written in English. Always.

**Every pull request title and description is in English** — and so is every reply on a public
issue or pull request. It does not matter that the issue was in Spanish, that the commits are in
Spanish, or that the person you are talking to writes Spanish. Anyone who follows the project has to
be able to read a pull request, and Spanish is a barrier for many of them.

Before you run `gh pr create`, `gh pr edit` or write any PR text:

1. Title: `type(scope): what changes` — English, lower case, English scope
   (`feat(wake): a Bedrock player now wakes the server too`). The scopes are listed in
   `docs/articles/contributing.md`, section "Pull requests".
2. Description: follow `.github/pull_request_template.md` (its headings are already in English).
3. Commit messages are the exception: they stay in **Spanish**, as the rest of the guide says.
4. If you notice Spanish in a pull request you are about to open, translate it first. If you notice it
   in an existing one, say so.

The user's own conversation with you can be in Spanish; only what goes to GitHub is English.

## Everything the project publishes is in English

Not only pull requests. **Issues you open, replies, labels and their descriptions, project boards and
saved issue views** are English too: the whole community reads them. People who report a problem may
write in Spanish or English (the forms say so); what the maintainer or an assistant publishes is
English. Commits stay in Spanish and the docs keep both languages.

## Audits are published

Every audit (the `auditoria` skill, `docs/articles/auditing.md`) publishes its findings as issues, so
that everyone can see how the project is doing, help, and know what problems it has: a tracking issue
`Audit YYYY-MM-DD`, one issue per pull request with a checkbox per finding, label `audit`, and what is
already fixed recorded as closed issues that name their pull request. A GitHub project and shared
views keep them organised. **The one exception is security**: a finding that could be exploited today
and is not fixed yet goes through the private channel in `SECURITY.md` and is published once the fix is
out. When unsure, treat it as security and ask. Details: "Publishing" in `docs/articles/auditing.md`.

## Everything else

- Docs move with the code, in both languages (`README.md` + `README.es.md`,
  `architecture.md` + `architecture.es.md`, and so on). The checklist is in the pull request template.
- The translated web pages are generated: after touching `web/index.html`, `web/_i18n/strings.json`,
  `web/assets/site.js` or `site.css`, run `python web/_i18n/build.py` and commit what it writes (CI checks).
- Network reads go through `AppHttp` (capped size, 30 s deadline for silence), never `GetStringAsync`,
  `ReadAsStringAsync` or `ReadFromJsonAsync`; a test enforces it.
- A new convention or workflow goes into `docs/articles/contributing.md`, `contributing.es.md`,
  `.github/copilot-instructions.md` **and this file**.

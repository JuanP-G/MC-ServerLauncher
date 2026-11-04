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

## Everything else

- Docs move with the code, in both languages (`README.md` + `README.es.md`,
  `architecture.md` + `architecture.es.md`, and so on). The checklist is in the pull request template.
- A new convention or workflow goes into `docs/articles/contributing.md`, `contributing.es.md`,
  `.github/copilot-instructions.md` **and this file**.

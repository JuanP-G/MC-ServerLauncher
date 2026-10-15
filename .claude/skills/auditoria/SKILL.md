---
name: auditoria
description: Audit the code added since the last audit of MC Server Launcher — bugs a user can see (internal errors on screen), hidden bugs, duplication and house style — then report it the documented way and plan the fixes. Use when the user asks to audit, review "the recent code", or asks how long it has been since the last audit.
---

# Auditing MC Server Launcher

The method is `docs/articles/auditing.md` (English) / `auditing.es.md` (Spanish). Read it first; this
skill is the order to do it in. Talk to the user in their language (the maintainer writes Spanish).

## 1. Find the range

```bash
git fetch origin -q
git describe --tags --match 'auditoria/*' origin/main     # last audit + commits since
git tag --list 'auditoria/*' --sort=-creatordate | head -1
git diff --stat <last-tag>..origin/main -- McServerLauncher McServerLauncher.Tests
```

If the user names another starting point (a version tag), use theirs. Note the head commit: it is
what the new tag will point at.

## 2. Read it, by area

Three reviewers in parallel (Explore agents, "very thorough"), one per area from the doc's Scope
table: settings/UI, content, lifecycle. Give each one:

- its file list (from the diff), and the instruction to read every file **whole**;
- the doc's "What to look for" list verbatim, plus the traps of this repo: `x:CompileBindings="False"`
  hides wrong binding paths; the neutral `Strings.resx` is Spanish and every key goes in all five;
  `ex.Message` must not reach the UI; never `git stash` (the stash list holds a user backup);
- the output shape: severity, category, `file:line`, one-sentence defect, concrete scenario,
  confirmed/plausible, one-line fix, quoting the code for high and medium.

Meanwhile do the cross-cutting checks yourself: keys used but missing from the `.resx`, keys nothing
uses, every `ex.Message` that is formatted into user text.

## 3. Verify

Open the code for every **high** and every medium that would change the plan, and read the path
yourself. Check outside facts against their source (the Modrinth API, Minecraft's behaviour) before
believing them. Move findings down when they do not hold, and say so in the report. A UI finding that
cannot be settled by reading gets reproduced in a throwaway Avalonia host before it is fixed.

## 4. Report

Publish one HTML artifact (load the artifact skills first), built as the doc's "The report" section
says:

- header: range, `auditoria/YYYY-MM-DD`, the previous tag, files and lines read;
- one count tile per severity (high, medium, low, design);
- a list filterable by severity and by area; each row: severity stripe and tag, area, PR number,
  confirmed/plausible, a title that is **what the user sees or loses**, the scenario, `file:line`;
- the fix plan as one card per pull request.

Then ask (AskUserQuestion) what gets fixed now — high+medium, everything, or only high — and whether
design refactors are in. Do not start fixing before the answer.

## 5. Plan and fix

One pull request per area, each from `origin/main` in its own worktree
(`../MC-ServerLauncher-<tema>`); a cross-cutting change the others build on goes first. Every fix
gets a test that fails against the old code (`git show origin/main:<path> > tmp` to check — not
stash). Lessons go into "Correctness rules learnt the hard way" in both `contributing` pages, in the
pull request of the fix. Commits and PR titles follow `docs/articles/contributing.md`.

## 6. Close the audit

With the user's OK (tags and issues are public):

```bash
git tag -a auditoria/YYYY-MM-DD <reviewed-commit> -m "Auditoría YYYY-MM-DD: <range>, <counts>"
git push origin auditoria/YYYY-MM-DD
gh issue create --title "Auditoría YYYY-MM-DD" --body "<every finding as a task, link to the report>"
```

Add the row to the Log table in both `auditing.md` and `auditing.es.md` (date, tag, range, counts,
issue, PRs), and fill in the PRs and close the issue when the last fix is merged.

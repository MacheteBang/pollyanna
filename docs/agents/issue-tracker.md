# Issue tracker: GitHub

Issues and specs for this repo live as GitHub issues on `MacheteBang/pollyanna`. Use the `gh` CLI for all operations. Inside the clone, `gh` infers the repo from `origin`; elsewhere, pass `-R MacheteBang/pollyanna`.

Always pass `--title` and `--body` or `--body-file` (and a body for comments). Omitting them makes `gh` prompt or open `$EDITOR`, which hangs a non-interactive session.

The repo is public: issues, comments and PR bodies are as public as the code, so the CLAUDE.md rule on deployment specifics applies to them too.

## Conventions

- **Create an issue**: `gh issue create --title "<title>" --body-file <file>`, adding `--label "<label>"` to label it at once. Write multi-line bodies to a temp file first.
- **Read an issue**: `gh issue view <number>` for the title and body, plus `gh issue view <number> --comments` for the thread.
- **List issues**: `gh issue list --state open|closed|all --label <label>`, with `--search "<query>"` to search.
- **Sub-issues**: don't use GitHub's native sub-issues. Put `Part of #<parent>` at the top of the child body and add the child to a task list in the parent body.
- **Comment on an issue**: `gh issue comment <number> --body "<body>"`, or `--body-file <file>` for multi-line.
- **Apply / remove labels**: `gh issue edit <number> --add-label "<label>"` / `--remove-label "<label>"`. Labels must already exist on the repo; create a missing one with `gh label create "<label>" --description "<meaning>"`.
- **Close**: `gh issue close <number> --comment "<comment>"`, with `--reason "not planned"` when it won't be done.

## Pull requests as a triage surface

**PRs as a request surface: no.** _(Set to `yes` if this repo treats external PRs as feature requests; `/triage` reads this flag.)_

When set to `yes`, PRs run through the same labels and states as issues, using the `gh pr` equivalents (`gh pr view`, `gh pr comment`, `gh pr close`). GitHub shares one number space across issues and PRs, so a bare `#42` may be either: try `gh pr view 42` and fall back to `gh issue view 42`.

## When a skill says "publish to the issue tracker"

Create a GitHub issue.

## When a skill says "fetch the relevant ticket"

Read it as in **Read an issue** above.

## Wayfinding operations

Used by `/wayfinder`. The **map** is a single issue with **child** issues as tickets. The `wayfinder:*` labels must exist on the repo first (see **Apply / remove labels**).

- **Map**: a single issue labelled `wayfinder:map`, holding the Notes / Decisions-so-far / Fog body.
- **Child ticket**: an issue with `Part of #<map>` at the top of its body, listed in a task list in the map body. Labels: `wayfinder:<type>` (`research`/`prototype`/`grilling`/`task`). Once claimed, the ticket is assigned to the driving dev.
- **Blocking**: a `Blocked by: #<n>, #<n>` line at the top of the child body, rather than GitHub's native dependencies. A ticket is unblocked when every blocker is closed.
- **Frontier query**: walk the map's task list in order, skipping closed tickets, tickets with an open issue in their `Blocked by` line, and assigned tickets (`gh issue view <n> --json assignees`). The first remaining ticket wins.
- **Claim**: `gh issue edit <n> --add-assignee @me`, the session's first write.
- **Resolve**: `gh issue comment <n> --body "<answer>"`, then `gh issue close <n>`, then append a context pointer (gist + link) to the map's Decisions-so-far with `gh issue edit <map> --body-file <file>` (it replaces the whole body, so read it first with `gh issue view <map> --json body -q .body`).

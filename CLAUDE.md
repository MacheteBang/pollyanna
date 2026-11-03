# Pollyanna

Self-hosted gift exchange (.NET 10, Razor Pages, EF Core on SQLite). README.md has the dev
setup, test and migration commands and the code layout; DEPLOYMENT.md covers running it.

## Working rules

- SPEC.md is the source of truth. Don't make product or technology decisions on your own:
  ask one question at a time, then write the answer into SPEC.md.
- Before starting work, read the relevant SPEC.md sections, including §14
  (implementation status) and §12 (the "Paper slips" visual design) for UI changes.
- Run `dotnet test` before calling anything done.
- Keep the repo free of anyone's deployment specifics (hosts, paths, proxies, backups).
  Pollyanna ships as an image plus an example Compose file; where it runs is the
  operator's business. The repo is public, so this covers issues, PR bodies and commit
  messages too: use generic examples (`pollyanna.example`), never details of a real
  deployment.
- Don't commit to main. Work on a feature branch, push it with `git push -u origin
  <branch>`, and open a PR to main with
  `gh pr create --base main --head <branch> --title "<title>" --body-file <file>`. Commit
  freely on feature and worktree branches: the user reviews through the PR, and nothing
  reaches main until they merge it.
- Pushing a `vX.Y.Z` tag publishes the container image through
  `.github/workflows/ci.yml`. Ask before tagging.

## Agent skills

### Issue tracker

Issues live on the GitHub repo `MacheteBang/pollyanna` and are managed with the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five default triage labels (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`). See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `GLOSSARY.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.

### Test seams

The agreed seams for test-first work (requests in; pages, database, logs and mail out; the fake clock). See `docs/agents/testing.md`.

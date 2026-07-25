# Memory Index

Durable project knowledge, committed to the repo so it travels with a clone. One file per fact, each
with `name` / `description` / `metadata.type` frontmatter. Link between them with `[[name]]`.

**Only `project` and `reference` memories belong here** — this repo is public. Anything about a
particular person or workstation (`user`, `feedback`) stays in the local agent memory directory and is
never committed. See `CLAUDE.md` → "Memory" for the full rule.

**Not a substitute for the docs.** `.claude/PLAN.md` is the design record and `CLAUDE.md` is the
working guide; if a fact belongs in either, put it there instead. This directory is for things that
fit neither — discoveries, external constraints, and decisions made in passing.

<!-- Add entries as: - [Title](file.md) — one-line hook. -->

- [Discord API docs & rate limits](discord-api-docs.md) — docs moved to `docs.discord.com`; unversioned API calls silently hit deprecated v6; global cap is 50 req/s, interactions exempt.

---
name: discord-api-docs
description: Canonical Discord API doc URLs (moved hosts in 2025), the versioning trap, and the global rate limit the hand-rolled client must respect.
metadata:
  type: reference
---

Verified 2026-07-25. Discord **moved its developer docs to a new host and dropped the `/docs`
segment** — every `discord.com/developers/docs/...` link 301s to `docs.discord.com/developers/...`.
Old deep links in blog posts and Stack Overflow answers still resolve, but write the new form:

| Topic | URL |
|---|---|
| API reference / versioning | `https://docs.discord.com/developers/reference` |
| Rate limits | `https://docs.discord.com/developers/topics/rate-limits` |
| Interactions | `https://docs.discord.com/developers/interactions/receiving-and-responding` |
| Gateway | `https://docs.discord.com/developers/events/gateway` |
| **Machine-readable index** | `https://docs.discord.com/llms.txt` |

Start at `llms.txt` — it is the full doc index in one fetch, which beats guessing section paths.

**Versioning trap.** The base URL is `https://discord.com/api` (still on the original host — only the
*docs* moved). Omitting the version does **not** get you the current API: unversioned requests route to
**v6, which is deprecated**. The version must be explicit in the path — `https://discord.com/api/v10`.
This is why pinning the version in the base URL is a correctness requirement, not hygiene.

**Rate limiting**, for `DiscordRateLimiter` (Phase 1):

- Per-route buckets via `X-RateLimit-Limit` / `-Remaining` / `-Reset` / `-Reset-After` / `-Bucket`.
- `X-RateLimit-Scope` distinguishes `user` / `global` / `shared` — a shared-scope 429 is not the bot's
  fault and must not poison the per-route bucket.
- **A global ceiling of 50 requests/second per bot token**, independent of per-route buckets. A build
  farm bursting on a broken stream will hit this before it hits any route limit.
- **Interaction endpoints are exempt from the global limit** — so triage button/modal responses (Phase 4)
  stay responsive even while job notifications are being throttled. Worth keeping them on a separate
  path through the limiter rather than one global queue.
- 429 bodies carry `message`, `retry_after` (seconds), `global`, and sometimes `code`.

See `.claude/PLAN.md` §3.3.5 for why this needs real bucket handling rather than Polly retries, and
§3.3.3 for the embed/message size limits.

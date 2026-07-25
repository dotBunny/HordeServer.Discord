---
name: notification-type
description: Implement an INotificationSink member — turning a Horde notification into a Discord message at Slack parity. Use when implementing or changing any Notify*/Send* member, building embeds, routing to channels, or porting behaviour the Slack sink already has.
allowed-tools:
  - Read
  - Write
  - Edit
  - Glob
  - Grep
  - Bash
  - WebFetch
---

# Implementing a notification type

The core repeating task of Phases 1–4: one `INotificationSink` member goes from a logging no-op to a
real Discord message. Work one member at a time and keep it shippable.

## 1. Read how Slack does it — do not copy it

The reference implementation is `SlackNotificationSink.cs` under
`<UE>/Engine/Source/Programs/Horde/Plugins/Build/HordeServer.Build/Notifications/Sinks/`. Read it for
**what information the notification carries and what the user needs to see** — which fields matter,
what the message is for, when it updates.

**Then close it and write ours from the interface.** This repo is public and MIT; Epic's source is
not ours to relicense. Mirroring the architecture is fine and intended. Pasting their code — even
lightly edited, even a single formatted block — is a licence breach. If you catch yourself
transcribing, stop and re-derive from the data on the parameters.

Never edit anything in the engine tree. It is Perforce-controlled and read-only to us.

## 2. Decide the Discord surface

| Horde shape | Discord shape |
|---|---|
| One-off broadcast (job/step outcome, agent report) | One message, one embed, to a configured channel |
| Something that updates over time (an issue) | A message edited in place; store its id in the message-state collection |
| Parent event with follow-ups (issue triage) | A real Discord thread off the parent message |
| Aimed at a person | DM channel via `POST /users/@me/channels`, or an @-mention if unmapped |

An unmapped or un-DMable user must degrade to a channel mention or plain-text name — **never a
dropped notification**. Log the miss once, not per message.

## 3. Respect the limits — they are hard 400s, not soft guidance

Verified against `docs.discord.com` on 2026-07-25:

| Limit | Value |
|---|---|
| Fields per embed | 25 |
| Title / field name / author name | 256 |
| Field value | 1024 |
| Description | 4096 |
| Footer text | 2048 |
| **Combined across all embeds in a message** | **6000** |
| Embeds per message | 10 |
| Message content | 2000 |

The 6000 combined ceiling is the one that bites: it sums `title`, `description`, `field.name`,
`field.value`, `footer.text` and `author.name` across *every* embed in the message, so a message that
passes field-by-field can still fail.

Log excerpts and error lists are unbounded input. Truncate deliberately — take the first N events,
append a count of what was dropped, and link to the Horde page for the rest. A truncation that hides
the fact it truncated is worse than a short message.

## 4. Route it — never resolve a channel yourself

**Horde has already decided which channel this belongs in.** Every notification carries a Slack
channel id, or one is reachable from `BuildConfig`. Your job is to hand that string to
`DiscordChannelResolver` and post where it says. Do not read `DiscordServerConfig` channel settings
directly, and do not invent a second routing mechanism — `PLAN.md` §3.3.2 and §3.3.8 explain why.

```csharp
// A channel carried on the notification
DiscordDestination? destination = _channels.Resolve(report.Channel);

// One of the base categories, when the notification carries nothing
IReadOnlyList<DiscordDestination> destinations = _channels.ResolveCategory(DiscordChannelCategory.Agent);

// Job completion, which Horde routes by job then stream, each with an outcome filter
IReadOnlyList<DiscordDestination> destinations = _channels.ResolveJobCompletion(job, streamConfig, outcome);
```

Then send through `DiscordNotificationProcessor.SendAsync`, which is the single exit point — it
applies the configured-or-not gate and the fallback-channel note.

Things the resolver already handles, so you do not have to: the map lookup, the catch-all fallback,
warn-once on an unmapped channel, deduplicating two Horde channels that point at one Discord channel,
and rejecting a Slack id pasted into a Discord setting.

Two traps worth knowing:

- **Discord channel ids are snowflakes, not names** — there is no `#channel`. Those go on the *value*
  side of the map.
- **Horde is not consistent about its own ids.** `JobNotificationChannel` and
  `UpdateStreamsNotificationChannel` hold a bare channel *name*; everything else holds a Slack id. The
  resolver copes, but do not write code that assumes one form.

If a notification needs routing Horde has no opinion about — split by outcome, a role ping, a specific
guild — that is §3.3.8 territory. Raise it rather than bolting a second mechanism on.

## 5. Go through the client, never around it

All traffic goes through the rate-limited client. Discord enforces per-route buckets *and* a global
50 requests/second per bot token, and a build farm on a broken stream will hit it. Interaction
endpoints are exempt from the global limit, so keep interaction responses off the same queue as bulk
notifications. See the `discord-api-docs` memory in `.claude/memory/`.

## 6. Write it like the rest of the file

Tabs, Allman braces, the copyright header, XML doc comments on everything public (the build fails on
a missing one). Keep members in `INotificationSink` order. Use `<remarks>` for the *why*, especially
where Discord forces a departure from what Slack does — that is the comment a future reader needs.

The sink is fault-isolated: `NotificationService` wraps each sink in try/catch, so throwing cannot
disturb Slack delivery. That is a safety net, not a licence to let exceptions escape — an unhandled
throw still means a silently missing notification.

## 7. Verify

Run the `verify-plugin` skill. A clean build plus a green probe proves the plugin still loads; it
proves nothing about the message itself. Say so precisely when reporting.

**Never put an engine type in a `[DataRow]`** — MSTest drops the whole test during discovery and the
run still reports green. See the gotchas in `CLAUDE.md`.

For the message, the honest check is a real send to a test channel. If no Discord credentials exist
yet, say the formatting is unverified rather than implying it was tested.

## Finally

Update `.claude/PLAN.md` when a decision changes — noting the reversal rather than rewriting history —
and tick the member off the phase list. If Discord's API forced a departure from the plan, that is a
design change and belongs in the document.

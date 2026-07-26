# CLAUDE.md

Guidance for Claude Code (claude.ai/code) when working in this repository.

## What this is

**HordeServer.Discord** is a notification plugin for **Horde**, Epic's build automation server (part of
Unreal Engine). It delivers Horde's build notifications to Discord, running *alongside* Horde's
built-in Slack sink rather than replacing it.

The whole project builds to **one assembly**: `HordeServer.Discord.dll`. Installing it is a file copy
into a Horde server's application directory — no changes to Horde or to Unreal Engine are required.

**The design and roadmap live in [`.claude/PLAN.md`](.claude/PLAN.md). Read it before doing
substantive work** — it records the architecture investigation (with engine file/line references),
the decisions taken and why, and the phase breakdown. Current status: **Phases 0–3 complete and verified against a real Discord
server** (2026-07-26) — fifteen of the seventeen `INotificationSink` members deliver, with DMs and
mentions, and all fifteen `DiscordSmoke` scenarios post to a live guild. Phase 4 is the gateway and
interactive issue triage.

## The two trees

This repo compiles against a **built Unreal Engine source tree** that is not part of it:

| | |
|---|---|
| This repo | `D:\Repositories\dotBunny\HordeServer.Discord` — public GitHub, MIT, plain **git** |
| Horde source | `<UE>\Engine\Source\Programs\Horde` — Epic's, under **Perforce**, read-only |

They live on different roots, so there is no stable relative path between them.

### Resolving the engine path

`$(HordeBinDir)` points at a **built** Horde server output directory (the same directory the plugin is
eventually dropped into). Resolve it in this order:

1. `Horde.local.props` at the repo root — git-ignored, created per machine from
   `Horde.local.props.template`.
2. The `HORDE_BIN_DIR` environment variable.

`Directory.Build.targets` errors clearly if neither resolves. On this machine the value is
`D:\Workspaces\dotBunny\DETHOL\Engine\Source\Programs\Horde\HordeServer\bin\Development\net10.0`.

To read Horde's own source (for interfaces, or to see how the Slack sink does something), look under
`<UE>\Engine\Source\Programs\Horde\Plugins\Build\HordeServer.Build\Notifications\`. **Never edit
anything in the engine tree** — it is Perforce-controlled and not ours.

## Hard rules

1. **Never vendor Epic code into this repo.** It is public and MIT-licensed; committing Epic engine
   source or compiled `HordeServer.*.dll` / `EpicGames.*.dll` would breach the UE EULA, and
   MIT-licensing them is not ours to do. Mirror Epic's *architecture* freely; do not paste their
   source. `.gitignore` blocks those DLL patterns as a backstop.
2. **Never commit `Horde.local.props`** or `.claude/settings.local.json` — both are machine-specific
   and git-ignored.
3. **Never take a Discord SDK dependency.** The client is hand-rolled against `HttpClient`,
   `System.Text.Json` and `ClientWebSocket`, mirroring how Epic hand-rolls `EpicGames.Slack`. This
   keeps the drop to one file with zero transitive dependencies and avoids assembly-version
   collisions inside the host server. Rationale in `PLAN.md` §3.2.
4. **`AssemblyName` must stay `HordeServer.Discord`.** Horde discovers plugins by scanning its app
   directory for `HordeServer.*.dll`; renaming the assembly makes the plugin invisible.
5. **Do not commit or push** unless asked. Leave work staged for review.

## Building and verifying

```bash
dotnet build -c Development          # default configuration, matches Horde's own
dotnet build -c Release
```

Configurations are `Debug` / `Development` / `Release`. `Development` is the default and is what
`HordeBinDir` normally points at. The `.slnx` must declare all three explicitly or solution-level
builds fail with MSB4126 even when the project itself is configured correctly.

The build should be **clean with zero warnings** — `GenerateDocumentationFile` is on, so every public
member needs an XML doc comment. Keep it that way.

Output is a single `HordeServer.Discord.dll` (~97 KB at Phase 2). If engine assemblies ever appear in
`bin/`, a `<Private>false</Private>` is missing from a `<Reference>` — `DropIsASingleAssembly` in the
test suite guards this.

### Verifying the plugin still loads

The full check is to copy the DLL into a Horde server app directory and confirm the server logs
`Added plugin 'Discord'` — but that needs MongoDB and Redis.

**`dotnet test` is the lighter check that needs neither, and is the gate.**

```bash
dotnet test -c Development
```

`HordeServer.Discord.Tests` deploys the freshly built plugin into `$(HordeBinDir)` itself and then
replicates `ServerApp.CreatePluginCollection` against it — enumerate the app dir for
`HordeServer.*.dll`, `Assembly.LoadFrom` each, look for `[Plugin]`, then call `PluginCollection.Add`.
That last call is the valuable one; it validates the generic constraints on the config types. It also
asserts the sink still implements every `INotificationSink` member and that the drop is still one file.

The probe logic lives in `tools/PluginProbe` and is shared. Running it directly prints a readable
report, which is what you want when a test goes red after an engine change:

```bash
dotnet run --project tools/PluginProbe -c Development
```

Both take the server directory from the build-time `$(HordeBinDir)` or `HORDE_BIN_DIR` (the probe also
accepts argv[0]), so on a configured machine neither needs arguments.

### Verifying the messages themselves

`dotnet test` proves what the plugin *would* send. It proves nothing about what Discord does with it.
`tools/DiscordSmoke` closes that gap: it posts one of every notification to a real channel, driving the
real `DiscordNotificationProcessor` with stand-in Horde data. **No Horde server, MongoDB or Redis.**

```bash
dotnet run --project tools/DiscordSmoke -c Development                 # all scenarios
dotnet run --project tools/DiscordSmoke -c Development -- step label   # just those
dotnet run --project tools/DiscordSmoke -c Development -- --help       # list them
```

Credentials come from `Horde.local.props` (git-ignored, same file as `HordeBinDir`) or the `DISCORD_*`
environment variables; unconfigured, the tool prints what is missing and exits 1. They are baked into
that tool's assembly and nothing else, so **rebuild after editing them**. Never print the bot token —
`SmokeSettings.Describe` deliberately omits it, and every diagnostic goes through it.

The scenario data is deliberately awkward: names containing markdown, a compile error long enough to
truncate, more failing steps than fit in an embed. That is the point — clean data would not tell you
anything you did not already know from the unit tests.

Two naming notes. Nothing under `tools/` is named `HordeServer.*` — not `PluginProbe`,
`HordeTestDoubles` or `DiscordSmoke` — so none can ever be mistaken for a plugin.
`HordeServer.Discord.Tests.dll` *does* match that pattern, exactly as Epic's own test assemblies do —
harmless because it is never deployed, but never copy it beside the server.

`tools/HordeTestDoubles` and `tools/DiscordSmoke` are the two projects with `GenerateDocumentationFile`
off — the first is engine interfaces satisfied member for member, the second a console tool whose
scenarios document themselves by what they print. `PluginProbe` keeps documentation on for the opposite
reason, since someone reading a red test ends up in its types.

## Architecture

- **`DiscordPlugin`** — `[Plugin("Discord", EnabledByDefault = false, …)]`, implements `IPluginStartup`.
  Its `ConfigureServices` registers the sink. The constructor may take `IConfiguration`, `IServerInfo`,
  and/or the server config type; nothing else is injectable there.
- **`DiscordServerConfig : PluginServerConfig`** — bound from `Horde:Plugins:Discord` in `server.json`.
  Restart required. Credentials and infrastructure only.
- **`DiscordConfig : IPluginConfig`** — the global config, hot-reloaded by Horde's config system. The
  user map and (from Phase 3) per-stream routing live here so they change without a restart.
- **`DiscordNotificationSink : INotificationSink`** — 17 members. Horde's `NotificationService`
  resolves `IEnumerable<INotificationSink>` and fans out with **per-sink exception handling**, so this
  plugin cannot disturb the Slack sink even if it throws. Keep it thin — each member either forwards to
  the processor or logs — and keep members in interface order; both are what make diffing against the
  interface tractable when Epic changes it.
- **`Notifications/DiscordNotificationProcessor`** — all the formatting. Split out so the sink stays
  diffable, mirroring how the Experimental plugin splits its Slack sink from its processor. Its
  `#region`s mirror the sink's, so the two files read side by side. `SendAsync` is its single exit
  point; everything goes through it, including the single-destination `SendToAsync`.
- **`Notifications/DiscordUserResolver`** — which Discord account belongs to a Horde user, and which
  Discord role stands in for one of Horde's user-group aliases. Behind `IDiscordUserResolver` so a
  `/link` slash command can join it as a second provider. Deliberately **not** cached — it reads a
  dictionary out of the hot-reloadable config, and a cache would only delay the reload.
- **`Notifications/DiscordRepeatFilter`** — what has already been announced, keyed by event id and
  state digest. Some notifications describe a *state* rather than an event and arrive on a ticker; a
  config failure would otherwise be reposted every pass. It also gates the recovery messages, which are
  only sent to a channel that heard about the problem. In memory and bounded; the persistent
  message-state collection is Phase 4. Rationale in `PLAN.md` §5, Phase 2.
- **`Notifications/DiscordChannelResolver`** — all the routing, and the only place that decides where a
  message goes. Horde already resolved which channel a notification belongs in and hands us a **Slack
  channel id**; this translates that to a Discord guild and channel via the map in `DiscordConfig`.
  Never resolve a channel anywhere else. `DiscordRoutingReport` names unmapped channels at startup.
  Design and rationale: `PLAN.md` §3.3.2.
- **`Client/`** — the hand-rolled Discord client. `DiscordRateLimiter` (per-route buckets, global 50/s,
  behind an `IDiscordClock` seam so tests assert decisions rather than sleep), `DiscordClient` (REST,
  `/api/v10` pinned, owns a private `HttpClient`), and the embed/message builders, which enforce
  Discord's size limits rather than letting the API 400.

`INotificationSink` is internal to Horde with **no stability guarantee**. After an engine upgrade,
rebuild — a stale DLL against a newer server fails at plugin load rather than degrading.

## Conventions

- **Tabs** for indentation, Allman braces — matches Horde's own source, since this code sits beside it
  conceptually and gets diffed against it.
- File header on every `.cs`:
  `// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.`
- `RootNamespace` is `HordeServer`. Plugin and config classes sit in `namespace HordeServer` (matching
  Horde's convention); everything else goes under `HordeServer.Discord.*` to avoid colliding with
  Epic's types.
- XML doc comments on all public members (enforced by the build). Use `<remarks>` for the *why* —
  especially where Discord's API forces a departure from what the Slack sink does.
- Prefer explaining non-obvious constraints in comments over leaving them to be rediscovered. Several
  already-load-bearing ones are noted inline in the csproj and `Directory.Build.targets`.
- **Read the engine tree with `Read` / `Grep` / `Glob`, not with shell `grep` / `find` / `sed -n`.**
  Reading Epic's source is the single most common activity in this repo, and the dedicated tools give
  clickable results, cost fewer tokens, and don't accumulate one-off entries in
  `.claude/settings.local.json`. `Read` takes `offset`/`limit` for "show me lines 460-500 of the Slack
  sink", which is the case people reach for `sed -n` to do.

## Gotchas found the hard way

- **Discord does not expand `:shortcode:` emoji.** Slack resolves them server-side and Epic's sink
  relies on it; Discord's *client* expands them as a human types, so anything a bot posts through the
  API keeps the colons and shows them as text. Use the literal unicode character, or `<:name:id>` for a
  custom guild emoji. This shipped in `ErrorPrefix` / `WarningPrefix` and survived three phases,
  because the unit tests blank both to keep expected payloads readable. Whenever you port a *value*
  across from the Slack sink rather than its structure, ask whether Slack was interpreting it.
- **`DiscordClient` logs a rejected request and returns; it never throws.** That is required of it —
  inside a real server a sink that throws is a sink that disturbs the other sinks — but it means
  "`SendAsync` returned" is not "the message arrived". Anything judging delivery has to watch the log,
  which is what `SmokeLog` exists for. `DiscordSmoke` reported fifteen of fifteen scenarios sent while
  Discord was 403-ing every one.
- **Anything outside the server that loads a Horde type needs `EngineAssemblyResolver` installed
  first**, and installed in a method that mentions no Horde type — the JIT resolves a method's types
  when it compiles it, so the install must be one call frame above the first use. The tests do it in a
  `[ModuleInitializer]`; `PluginProbe` and `DiscordSmoke` do it in a `Main` that immediately hands off
  to a `NoInlining` method. Symptom when missing: `FileNotFoundException` for `HordeServer.Shared`.
- **`ILogEventData` lives in `HordeServer.Compute`**, not `HordeServer.Build` — it reaches
  `INotificationSink` through the job-step members. Not discoverable by reading the interface; only
  the compiler tells you.
- **Namespaces split across the server/shared boundary in ways that read wrong.** `IUser` is
  `HordeServer.Users`, *not* `EpicGames.Horde.Users` (which exists and contains `UserId`).
  `LogEventSeverity` is `EpicGames.Horde.Logs`, while `ILogEventData` is `HordeServer.Logs`. Copy the
  using block from `DiscordNotificationSink.cs` rather than guessing.
- **`field` is a contextual keyword in C# 14.** A loop variable named `field` inside a property
  accessor now binds to the synthesized backing field and fails to compile. Relevant here because
  "field" is the natural name for a Discord embed field.
- **Never put an engine type in a `[DataRow]`.** MSTest reads attributes during *discovery*, before the
  test assembly's `[ModuleInitializer]` has installed the engine assembly resolver, so `EpicGames.Horde`
  cannot load and the whole test method is **silently dropped** — absent from the run, with the summary
  still green. Pass a `string` or `int` and convert in the test body, which runs after initialization.
  Caught once already with `LabelOutcome`; a green `dotnet test` will not warn you.
- **Nor may the test assembly *declare* a type that implements an engine interface.** Same root cause,
  louder symptom: discovery calls `Module.GetTypes()`, which resolves the base types and interfaces of
  every type the assembly defines, and the whole run dies with "Unable to load one or more of the
  requested types" before a single test executes. Test doubles for `IUser`, `IServerInfo`,
  `IUserCollection`, `ITestHealthReport` and friends therefore live in **`tools/HordeTestDoubles`** and
  are referenced from the tests, where they resolve lazily at method-JIT time. Add new fakes there, not
  beside the tests.
- **`AgentId` upper-cases whatever it is given.** `new AgentId("render-01").ToString()` is
  `RENDER-01`, and that is what belongs in a dashboard link — the constructor also rewrites `.` and
  rejects some inputs outright. Do not assume a round trip.
- **The `HordeBinDir` validation target must run `BeforeTargets="ResolveAssemblyReferences"`.** At
  `BeforeCompile` it fires *after* MSBuild has already emitted MSB3245 warnings for every engine
  reference, burying the actual explanation.
- **`PluginName` normalises to lowercase** — the plugin registers as `discord`, not `Discord`.
- **Discord modals accept text inputs only, max 5.** Slack's "Mark Fixed" view uses radio groups and a
  select menu and can present seven inputs. This is a component-type mismatch, not a count problem.
  The agreed resolution is in `PLAN.md` §3.3.4.
- **Discord has no lookup-user-by-email**, so the Horde-user → Discord-user association must be
  supplied by configuration. See `PLAN.md` §3.3.1.
- **A DM is a channel, and opening one can succeed while sending still fails.** There is no
  send-to-user endpoint: `POST /users/@me/channels` opens a two-member channel, then you post to it
  normally. Discord will happily open that channel and *then* refuse the message with 50007 if the
  recipient does not accept DMs from server members, so a fallback hung off the channel lookup alone
  would silently drop notifications.
- **Horde takes the first non-null deep link from any sink and ignores the rest.**
  `NotificationService.GetDirectMessageLinkAsync` / `GetChannelLinkAsync` iterate sinks in registration
  order, which a plugin does not control. Answering unconditionally would decide by luck whether a
  studio's dashboard buttons opened Discord or Slack, so `DiscordServerConfig.EnableDeepLinks` defaults
  to answering only when the Build plugin has no `SlackToken`.
- **The four user-targeted job/step members are DMs, not channel posts.** Slack sends them per
  subscriber, and a subscription notification broadcast to a shared channel makes that channel unusable
  on a busy stream. Phase 1 posted them to the job channel as an interim; Phase 3 corrected it. If you
  are adding a member that takes an `IUser` or `usersToNotify`, it almost certainly wants
  `SendToUsersAsync` rather than `SendAsync`.

## Documentation split

- **`README.md`** — for people *installing and configuring* the plugin. Keep it task-oriented: install,
  configure, verify, troubleshoot. No internals.
- **`CLAUDE.md`** (this file) — for people and agents *working on* the plugin.
- **`.claude/PLAN.md`** — the design record: investigation, decisions and their rationale, phasing.
  Update it when a decision changes, and note the reversal rather than silently rewriting history.
- **`.claude/memory/`** — durable facts that fit neither of the above. See below.

## Skills

Repeating procedures live in `.claude/skills/`, loaded on demand rather than kept in this file:

| Skill | Use it when |
|---|---|
| `verify-plugin` | Checking the plugin builds clean and a server would load it. The inner loop. |
| `notification-type` | Implementing or changing any `INotificationSink` member — the Phase 1–4 workhorse. Carries the verified Discord embed limits and the rule about reading Epic's sink without copying it. |
| `engine-upgrade` | The engine tree moved, or the plugin fails to load with `TypeLoadException`. |

`verify-plugin` and `engine-upgrade` are user-invocable as `/verify-plugin` and `/engine-upgrade`.

## Memory

Agents keep persistent notes. **This repo carries its own set in `.claude/memory/`**, committed so it
travels with a clone. Read `.claude/memory/MEMORY.md` at the start of a session — it is the index, one
line per memory, and the memories themselves are one file each with `name` / `description` /
`metadata.type` frontmatter. Cross-link with `[[name]]`.

**Only `project` and `reference` memories go in the repo.** This is a public repository: anything about
a particular person or workstation — `user` (who someone is, their preferences) and `feedback` (how an
agent should work with them) — stays in the agent's own local memory directory and is never committed.
The same applies to anything naming a private project or a local workspace path that isn't already
public in `PLAN.md`.

Before writing a memory, check it doesn't belong in `PLAN.md` (a design decision) or this file (a
convention or gotcha) instead — those are the primary records and a memory that restates them will
drift out of sync. Update an existing memory rather than adding a near-duplicate, and delete ones that
turn out to be wrong.

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
the decisions taken and why, and the phase breakdown. Current status: **Phases 0–4 complete, and
running on a live Horde server** (2026-07-26). All seventeen `INotificationSink` members deliver; the
gateway holds a session; triage buttons call Horde's `IssueService`; the Mark Fixed modal round-trips;
and each issue keeps one message in a thread, rewritten as it changes.

The plugin is **no longer verified only against stand-in data**. It has been installed in a real Horde
server and left running against a real stream: it is discovered and registers, real jobs and issues
produce the messages, and a button press mutates Horde's own issue database. Two consequences for
working here. First, `IHordeIssues`/`HordeIssues` — the adapter with no test coverage, kept to one call
per method precisely because nothing could exercise it — **has now been exercised**, so a change to it
is a change to something known to work rather than to something merely plausible. Second, the sink now
runs where a throw would reach `NotificationService`; the never-throw contract in `DiscordClient` is
load-bearing in fact, not just in principle.

What is still thin is **variety**: one server, one studio's configuration. The parts of Horde's config
surface that deployment does not use are still only covered by `dotnet test` and `DiscordSmoke`.

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
- **`Client/DiscordGateway`** — the inbound half, and the only part of the plugin Discord talks *to*.
  A websocket, because the alternative is an HTTP interactions endpoint needing a publicly reachable
  URL, which a build server usually is not. Registered as an `IHostedService` and gated on
  `EnableInteractions`; notifications work with it switched off. Two seams keep it testable —
  `IDiscordWebSocket` for the socket and `IDiscordClock` for the heartbeat — and the decisions
  (close-code classification, backoff) are pure functions in `DiscordGatewayPolicy`. Live-checkable
  with `dotnet run --project tools/DiscordSmoke -c Development -- --gateway 50`.
- **`Notifications/DiscordInteractionRouter`** — gateway dispatch to whoever registered for a custom-id
  scope. It exists for one reason: **Discord gives three seconds to answer an interaction**, so it
  acknowledges with a deferred update *before* calling the handler, which then has fifteen minutes to
  edit the message through the interaction token. Handlers never see the deadline. They also run off
  the receive loop, because that loop is what reads heartbeat acknowledgements. Live-checkable with
  `-- --interact`, which needs someone to actually press the button.
- **`Notifications/DiscordIssueTriage`** — what the buttons *do*. Registers for the `issue` scope and
  turns each verb into a call on Horde's `IssueService`, behind **`IHordeIssues`** because
  `IssueService` reaches MongoDB in its constructor and the test suite must run without one. That
  adapter (`HordeIssues`) is the only class here with no coverage, and is kept to one call per method
  for exactly that reason. Note `IDiscordUserResolver.GetEmail` — the user map read *backwards*,
  because a press arrives as a Discord snowflake and every issue operation is audited against a Horde
  user.

`INotificationSink` is internal to Horde with **no stability guarantee**. After an engine upgrade,
rebuild — a stale DLL against a newer server fails at plugin load rather than degrading.

## Conventions

- **Tabs** for indentation, Allman braces — matches Horde's own source, since this code sits beside it
  conceptually and gets diffed against it.
- File header on every `.cs`:
  `// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.`
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
- **A glyph that looks like an emoji may not be one, and a pair that looks symmetrical may not match.**
  Discord renders with Twemoji, which has an image for anything in Unicode's emoji set and nothing for
  anything outside it — U+26A0 `⚠` gets one with or without a variation selector, U+2718 `✘` never
  does. The log-event list paired those two and came out a colour emoji beside a monochrome text
  glyph. Take severity markers from the circle vocabulary (`🔴 🟠 🟡 🔵`) the processor already uses.
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
- **Resolving Horde's managed assemblies is not enough — some are only a wrapper around a native one.**
  `EpicGames.IoHash` p/invokes `blake3_dotnet`, which the server ships under `runtimes/{rid}/native`
  rather than beside the managed DLLs, a layout only its `deps.json` knows how to read. So
  `EngineAssemblyResolver` installs a `ResolvingUnmanagedDll` handler beside the managed one. Found on
  the 5.8.0 → 5.8.1 upgrade (2026-07-30): `StreamConfig.PostLoad` began hashing each stream into a
  `Revision` field (`StreamConfig.cs:481-483`), which took every test that builds a `BuildConfig`
  from green to `DllNotFoundException`. The failure lands far from the cause — an engine method deep
  inside `PostLoad`, not the load of the wrapper — and it only bites *outside* the server. Match the
  RID exactly rather than scanning `runtimes` by filename: a win-arm64 binary sits beside the win-x64
  one, and loading the wrong architecture fails worse, as `BadImageFormatException`.
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
- **`PluginName` normalises to lowercase** — the plugin registers as `discord`, not `Discord`. It
  normalises rather than rejects: `StringId.TryParse` lowercases `A`–`Z` in place
  (`EpicGames.Horde/StringId.cs:201-218`), so **config key casing is irrelevant everywhere** —
  `plugins.Discord` in `globals.json` binds exactly like `plugins.discord`, and `Horde:Plugins` binds
  into an `OrdinalIgnoreCase` dictionary besides. The README asserted the opposite for three phases.
  What *is* silently skipped is an **unrecognised** name: `PluginConfigCollectionConverter.Read` calls
  `reader.Skip()` for any key not in `NameToType`, which `ConfigService` fills from **loaded plugins
  only**. So a `server.json` mistake that stops the plugin loading also swallows its `globals.json`
  section without a word — one cause, two symptoms, and neither logs anything.
- **`Plugins` goes *inside* `Horde` in `server.json`, and getting it wrong is completely silent.**
  Horde reads `Horde:Plugins:<name>` for both discovery (`ServerApp.cs:257`) and server-config binding
  (`Startup.cs:282-286`). A top-level `"Plugins"` block is never read, so nothing enables the plugin —
  and because `EnabledByDefault = false`, the `Unable to find plugin(s) enabled in config file` throw
  at `ServerApp.cs:313` never fires either. `Loading …HordeServer.Discord.dll` still appears, because
  that is the directory scan; the sink's own registration line is what is missing.
- **Hosted-service registration order in `DiscordPlugin.ConfigureServices` is load-bearing.**
  `IHostedService` instances start in registration order, and `DiscordIssueTriage.StartAsync` is what
  calls `DiscordInteractionRouter.Register`. Registering the router first left it logging
  `listening (no scopes registered yet)` at every boot — harmless, but it is precisely the line an
  operator checks to confirm triage is live, so it read as a fault. Triage is now registered first.
- **Closing a gateway socket with `1000` destroys the session.** A clean close tells Discord the
  session is finished, so the state a `RESUME` would replay from is discarded and the next connection
  has no choice but to identify again. Deliberate hang-ups use `4000`
  (`DiscordGateway.ResumableCloseCode`). This fails silently — everything still works, it just quietly
  re-identifies every time and eats the daily session-start allowance.
- **A thread created from a message has the *same id* as that message.** Verified live 2026-07-26, not
  just read in the docs. It is what lets issue triage keep all its state in one URL — `DiscordMessageLink`
  parses `channels/{guild}/{channel}/{message}` into the channel, the message to edit in place, and the
  thread to post into — and it is why the planned Mongo message-state collection was dropped. Posting
  "into a thread" is just posting to a channel whose id is the parent message's.
- **A Discord role id means nothing outside its own guild.** Mentioning one from elsewhere does not
  fail — it renders as raw `<@&id>` text that pings nobody, which reads as a formatting bug. Hence
  `roles` being `alias → { guild?, role }` and `IDiscordUserResolver.GetRole` taking the destination's
  guild. Related: `allowed_mentions` lists roles *explicitly* rather than adding `roles` to `parse`,
  which would honour any role mention that came out of a build log.
- **`IIssue.WorkflowThreadUrl` has one slot and the Slack sink writes it too.** It is where Epic's sink
  keeps its triage thread permalink. Taking it unconditionally would silently replace a studio's Slack
  links, so `DiscordServerConfig.EnableTriageThreads` defaults to claiming it only when the Build
  plugin has no `SlackToken` — the same shape as `EnableDeepLinks`, for the same reason. Anything in
  that field that is not a `discord.com` link is left strictly alone.
- **A modal can only be the *first* answer to an interaction.** Once anything has been sent — including
  a deferral — Discord refuses to open one, because there is nothing left for the dialog to attach to.
  That is directly at odds with `DiscordInteractionRouter`'s acknowledge-everything-first rule, which
  is why `Register` takes an `answersForItself` predicate: the modal-opening verb runs unacknowledged
  and owes Discord a response inside three seconds itself. Affordable only because opening a modal is
  one request and nothing else — never put work in front of it.
- **After deferring, a further message is a *followup*, not a response.** `POST /webhooks/{appId}/
  {token}` rather than the callback endpoint. This is how the root-cause category dropdown reaches the
  operator once the fix has already been applied and acknowledged.
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
- **Issue triage routing is decided by the issue's *spans*, not by `IIssue.Streams`.** A span names the
  stream, the template, and the failing step whose annotations pick the workflow — and `IIssue` carries
  none of that, only a flat list of streams. Guessing from that list is how the plugin ended up posting
  to *every* workflow a stream defines instead of the one that owns the failure. Epic's three rules, all
  in `SlackNotificationSink.cs:860-963`: **one** workflow, from `spans[0].LastFailure.Annotations.WorkflowId`
  (`:871-877`); gated on `TriageWarnings` / `TriageErrors`, which both default to `true` (`:879`,
  `WorkflowConfig.cs:51,54`); then per span the *template's* triage channel **else** the stream's — an
  `else`, never both (`:921-936`). `IHordeIssues.FindSpansAsync` is what makes this reachable without
  MongoDB. Note `DiscordRoutingReport` already validated template triage channels the plugin then never
  used, so a green routing report was never evidence the routing was right.
- **A `BuildConfig` is only usable after `PostLoad`, and `PostLoad` needs a `ComputeConfig`.**
  `TryGetStream` reads a private lookup that nothing else fills, so a hand-built `BuildConfig` silently
  finds no streams — which is why triage routing had no test coverage for four phases and the divergence
  above went unnoticed. `BuildConfig.UpdateWorkspacesForPools` then does `plugins.OfType<ComputeConfig>().First()`
  despite a comment claiming it skips when absent, so omitting it throws `Sequence contains no elements`
  from inside `PostLoad`. `tools/HordeTestDoubles/BuildConfigFakes.cs` handles both. `StreamConfig.TryGetWorkflow`
  and `TryGetTemplate` need none of this — each is a `FirstOrDefault` over a public list.
- **The dashboard has no page for an issue, and a bad path redirects instead of failing.** There is no
  `issue/{id}` route — an issue opens as a *modal over an existing page*, so every issue link is some
  page plus `?issue={id}`. The route table is `HordeDashboard/src/App.tsx:142-178`, and its
  `errorElement` is literally `<Navigate to="/index" replace={true} />` (`App.tsx:53-55`), so an
  unmatched path silently lands the reader on their home page rather than 404-ing. `issue/{id}` shipped
  and survived four phases looking exactly like a link that does nothing. Epic's Slack sink anchors to
  the failing step's job — `job/{jobId}?step={stepId}&issue={id}`, `SlackNotificationSink.cs:1776-1779`
  — which needs the issue's spans; we hold only an `IIssue`, so we use `stream/{streamId}?tab=summary&issue={id}`
  from its `Streams` list at no extra cost, and fall back to the bare dashboard root when an issue has
  no streams. **Verify any new dashboard link against that route table**, remembering that dashboard
  *plugins* push extra routes in at `App.tsx:183-184` — that is why `test-automation` is valid despite
  being absent from the main list.
- **Issue ids are unique server-wide, not per stream or project.** They come from one singleton counter
  (`IssueCollection.cs:39-43`, `826-831`) and are the Mongo `[BsonId]` of `IssuesV2`. An issue also
  *crosses* streams — `IIssue.Streams` is a list — so no stream is "the" stream for one. Which stream a
  link is anchored to therefore decides only where the reader lands behind the modal, never which issue
  opens; pick one deterministically, because triage messages are rewritten in place and a link that
  moved between renders reads as a bug.
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

# Horde → Discord Notification Plugin — Investigation & Plan

> **Status: Phases 0–4 complete**, built 2026-07-25/26 and **verified against a real Discord server**.
> All seventeen `INotificationSink` members deliver. The gateway holds a session, triage buttons call
> Horde's `IssueService`, the hybrid Mark Fixed modal round-trips, and each issue keeps one message in
> a thread that is rewritten as it changes. What remains is the deliberate non-goals in §3.4 and the
> `/link` slash command deferred in §7 — plus one parity gap, role mentions (§3.3.7).
>
> Every phase gate found something the unit tests could not: emoji shortcodes Slack resolves and
> Discord does not (Phase 3), a smoke tool structurally incapable of failing (Phase 3), and a modal
> that cannot follow a deferral (Phase 4). Keep sending real messages.
> **Written:** 2026-07-25, against the DETHOL source engine (UE 5.8).
> All line references below point into `Engine/Source/Programs/Horde` in that engine — re-verify them
> after an engine upgrade, since none of it is a stable public API.

**Target:** a `HordeServer.Discord` plugin providing Discord notifications at parity with the existing
Slack sink, running alongside Slack, built and shipped out-of-tree as a drop-in DLL.

---

## 1. How Horde notifications actually work

### 1.1 The extension point

`INotificationSink` — `Plugins/Build/HordeServer.Build/Notifications/INotificationSink.cs`,
namespace `HordeServer.Notifications`. Seventeen members:

| Group | Methods |
|---|---|
| Jobs | `NotifyJobScheduledAsync`, `NotifyJobCompleteAsync` (×2 — broadcast + per-user), `NotifyJobStepCompleteAsync`, `NotifyJobStepAbortedAsync`, `NotifyLabelCompleteAsync` |
| Issues | `NotifyIssueUpdatedAsync`, `SendIssueReportAsync` |
| Config | `NotifyConfigUpdateAsync`, `NotifyConfigUpdateFailureAsync` |
| Farm ops | `NotifyDeviceServiceAsync`, `SendDeviceIssueReportAsync`, `SendAgentReportAsync`, `SendSessionConflictReportAsync`, `NotifyTestHealthReportAsync` |
| Links | `GetDirectMessageLinkAsync`, `GetChannelLinkAsync` |

### 1.2 Dispatch is already multi-sink and fault-isolated

`NotificationService` (`Notifications/NotificationService.cs:132`) takes `IEnumerable<INotificationSink>`
and fans out. Two dispatch styles, both safe:

- `EnqueueTasks(...)` (line 416) — fire-and-forget `Task.Run` per sink, each wrapped in try/catch
  that logs and swallows (line 383-410).
- Direct `foreach` + `await` with per-sink try/catch (e.g. lines 350-361, 366-377, 473-485).

**Consequence: adding a sink cannot break Slack delivery.** A throwing or hanging Discord sink
degrades to logged errors. This is the single most important fact for de-risking the project.

### 1.3 Sinks can be registered from a *different assembly*

Precedent already in-tree — `Plugins/Experimental/HordeServer.Experimental/ExperimentalPlugin.cs:35`
registers a second `INotificationSink` from its own assembly via `AddSingleton`.

The Build plugin registers its own Slack sink at `BuildPlugin.cs:187-190`, gated on
`_staticConfig.SlackToken != null`. Nothing anywhere assumes a single sink.

`ExperimentalSlackNotificationSink` is the template to copy: 143 lines, most methods
`=> Task.CompletedTask`, real work delegated to a separate processor class.

### 1.4 Plugin discovery is assembly scanning — this is what makes drop-in viable

`HordeServer/ServerApp.cs:254-300`, `CreatePluginCollection`:

1. Binds `Horde:Plugins` from server config.
2. Enumerates **`AppDir`** for files matching `HordeServer.*.dll` (line 259-267).
3. `Assembly.LoadFrom` each, scans `GetExportedTypes()` for `[Plugin]` (line 285-289).
4. Enables per `Horde:Plugins:<Name>:Enabled`, falling back to `EnabledByDefault`.
5. Throws if a plugin enabled in config was not found.

A `HordeServer.Discord.dll` dropped next to the server binaries is discovered with **zero edits
to any Epic-owned file**. No `Horde.sln` entry, no `HordeServer.csproj` `ProjectReference`.

Server config binds automatically: `PluginCollection.cs` `LoadedPlugin<TServerConfig,…>.ConfigureServices`
calls `serviceCollection.Configure<TServerConfig>(config)` against the plugin's config section, and
the startup class constructor may take `IConfiguration`, `IServerInfo`, and/or `TServerConfig`.

### 1.5 What the Slack sink actually does (the parity bar)

`Notifications/Sinks/SlackNotificationSink.cs` — **4,229 lines**. It is far more than a message poster:

- `IHostedService, INotificationSink, IAvatarService, IRcaNotifier, IAsyncDisposable` (line 58).
- **Socket Mode gateway**, hand-rolled on raw `ClientWebSocket` (lines 3492-3640).
- **Interactive triage** (line 3646 `HandleInteractionMessageAsync`):
  - `block_actions` → issue verbs `ack` / `accept` / `decline`, in two flavours: DM
    (`HandleIssueDmResponseAsync`, line 3841) and channel (`HandleIssueChannelResponseAsync`, line 3874).
  - `view_submission` → a **`markfixed` modal** built at lines 3944-4062 — up to *seven* inputs,
    three of which are non-text components. Full breakdown in §3.3.4.
- **Edit-in-place message state in MongoDB** — `_messageStates` collection `"SlackV2"`, unique index
  on (`Recipient`, `EventId`) (line 276), upserted at line 356. Lets the sink update an existing
  issue message rather than spamming new ones.
- **User resolution by email** — `GetSlackUserIdAsync` (line 3022) looks up Slack IDs from
  `user.Email`, cached in a `MemoryCache` (line 256).
- Escalation ticker (`clock.AddSharedTicker<SlackNotificationSink>`, line 283), admin-token user
  invites (line 1036).

### 1.6 Epic hand-rolls its Slack client — no vendor SDK

`Engine/Source/Programs/Shared/EpicGames.Slack/EpicGames.Slack.csproj` has exactly three
`PackageReference`s: `Microsoft.Extensions.Http`, `Microsoft.Extensions.Http.Polly`,
`Microsoft.Extensions.Logging.Abstractions`. Everything else — REST, blocks, elements, attachments,
views, and the Socket Mode websocket loop — is hand-written against `HttpClient`,
`System.Text.Json`, and `ClientWebSocket`.

**This precedent drives a key recommendation in §3.2.**

### 1.7 Existing configuration surfaces

- **Server config** (`Plugins/Build/HordeServer.Build/BuildServerConfig.cs`): `SlackToken`,
  `SlackSocketToken`, `SlackAdminToken`, `SlackUsers`, `SlackErrorPrefix`, `SlackWarningPrefix`,
  `ConfigNotificationChannel`, `UpdateStreamsNotificationChannel`, `JobNotificationChannel`
  (`;`-separated), `AgentNotificationChannel`. Restart to change.
- **Per-template routing**: `NotificationChannel` / `NotificationChannelFilter` on template refs,
  copied onto jobs (`Jobs/IJobCollection.cs:64-68`, `JobCollection.cs:116-117`).
- **Hot-reloadable global config**: the Experimental plugin's `NotificationConfig.cs` — a
  `[JsonSchema]`/`[ConfigIncludeRoot]` document keyed by stream and stream-tag, then by template,
  with regex step grouping and a `Channels` list. Originally recorded here as "the newer, better model
  and the one to mirror for per-stream Discord routing"; **not mirrored** — see §1.8 for what it
  actually covers and §3.3.2 for what we did instead.

**Every one of these resolves to a Slack channel *id*** — `C0832ESJUR5`, not `#horde-builds`. Corrected
2026-07-25; the original draft of §3.3.2 assumed names. That single fact is what makes the routing
design in §3.3.2 work at all.

### 1.8 Workflows vs. Experimental's `NotificationConfig` — two domains, not a migration

Worth settling, because it decides whether building on `WorkflowConfig.TriageChannel` is building on
sand. Investigated 2026-07-25 against this engine snapshot.

**They do not overlap.** `ExperimentalSlackNotificationSink` returns `Task.CompletedTask` from
*every* issue member — `NotifyIssueUpdatedAsync` and `SendIssueReportAsync` included. The only members
it implements are the broadcast job/step ones. `NotificationConfig`'s vocabulary agrees: `Template`,
`NamePattern`, regex step grouping, `Channels`, and nothing about triage, escalation, report times or
RCA.

**Nothing is deprecated.** No `[Obsolete]` in `WorkflowConfig` at all, and none on
`StreamConfig.NotificationChannel` or `TemplateRefConfig.NotificationChannel`. (The `[Obsolete]`
markers in `StreamConfig` concern stream tags and preflight change queries.)

So `NotificationConfig` reads as a **richer replacement for job-notification routing** — which in core
is one channel plus an outcome filter on a stream or template ref — not as a successor to workflow
issue channels.

| | Configured in | Signs of movement |
|---|---|---|
| Issue triage and reports | `WorkflowConfig` | **None** |
| Job/step notification routing | `StreamConfig` / `TemplateRefConfig` | Experimental is incubating a richer model |

**Consequences for this plugin:**

- Per-workflow triage and report routing — the thing the channel map in §3.3.2 exists to serve — sits
  on the side with no sign of change. Safe to build on.
- Job-notification routing is where churn is plausible. Use it (it is current and non-deprecated), but
  re-check it on an engine upgrade; the `engine-upgrade` skill says so.

**Caveat on all of the above:** this is inference from one source snapshot, not knowledge of Epic's
roadmap. The plugin is named "Experimental" and is `EnabledByDefault = false`, so Epic is plainly
incubating *something* — that does not say where it lands. The investment is real:
`SlackNotificationProcessor.cs` is 70 KB with a Mongo-backed `JobNotificationCollection` behind it.
Re-read this section after an engine upgrade rather than trusting it.

---

## 2. Decisions taken

| Question | Decision |
|---|---|
| Transport | **Bot + Gateway** — full parity including interactive components |
| Scope | **All of it** — jobs/steps, issues, config failures, agent/device/test-health |
| Slack | **Runs alongside**, unchanged |
| Location | **Out-of-tree**, drop-in `HordeServer.Discord.dll`, this repo |
| Guilds | **One** — single `GuildId` in config |
| Mark Fixed | **Hybrid** — 4-field text modal, then a category dropdown only when a root-cause summary was entered |
| User targeting | **Full parity** — triage-channel threads with @-mentions **and** DMs with their own buttons |
| User map | **Hand-maintained** `email → snowflake`, in the hot-reloadable plugin config (no restart) |

---

## 3. Consequences of those decisions — read before starting

### 3.1 "Out-of-tree" removes the *merge* cost, not the *coupling* cost

The plugin still needs compile-time types from the engine tree: `INotificationSink`, `IJob`, `IGraph`,
`IIssue`, `IUser`, `ILogEventData`, `StreamConfig`, `IssueReportGroup`, `AgentReport`,
`DeviceIssueReport`, `ITestHealthReport` (all `HordeServer.Build`), plus `IPluginStartup`,
`PluginAttribute`, `PluginServerConfig`, `IPluginConfig`, `IServerInfo`, `IMongoService`
(`HordeServer.Shared`), and `EpicGames.Horde` / `EpicGames.Core`.

Two ways to get them:

- **`ProjectReference` into the engine tree.** Best IDE experience (F12 into engine source), but
  building the plugin then builds Epic's entire Horde tree — nuget restore, protobuf codegen, ~15
  projects — and writes intermediates into the Perforce workspace.
- **`Reference` to compiled DLLs** from a built server output.

**Decided: DLL references** (this reverses the earlier recommendation here, which was
`ProjectReference`). Rationale that emerged during Phase 0:

- The plugin is deployed *beside these exact assemblies*, so compiling against them is the honest
  model — and it matches the §6 mitigation of "rebuild the plugin as part of the engine-upgrade
  checklist".
- No engine rebuild, and nothing written into the Perforce workspace. Build is sub-second.
- The engine's `bin/` output is not in the depot (verified with `p4 files`), so nothing is dirtied.
- The IDE cost is smaller than expected: `EpicGames.Horde.xml` ships alongside the DLLs, so XML doc
  IntelliSense still works. (`HordeServer.Build.xml` does not — Horde only sets
  `GenerateDocumentationFile` in its `Analyze` configuration.)

Use `<Private>false</Private>` on every engine reference so they are **not** copied into the drop
output. Verified: the build produces exactly one DLL and no engine assemblies.

**Resolving the path.** This repo and the engine (`D:\Workspaces\dotBunny\DETHOL\Engine`) live on
different roots, so there is **no stable relative path** and no absolute path may be committed.
Resolve it the way DETHOL resolves `___WORKSPACEROOT___`: a git-ignored local props file, falling
back to an environment variable.

```xml
<!-- Directory.Build.props -->
<Import Project="$(MSBuildThisFileDirectory)Horde.local.props" Condition="Exists('$(MSBuildThisFileDirectory)Horde.local.props')" />
<PropertyGroup>
  <HordeBinDir Condition="'$(HordeBinDir)' == ''">$(HORDE_BIN_DIR)</HordeBinDir>
</PropertyGroup>
```

Ship a `.template` alongside, `.gitignore` the `.local.props`, and fail the build with a readable
message. The validation target **must** run `BeforeTargets="ResolveAssemblyReferences"` — at
`BeforeCompile` it fires too late and the user sees a wall of MSB3245 "could not resolve reference"
warnings before the actual explanation.

### 3.1a This repo is public and MIT — do not vendor Epic code

`github.com/dotBunny/HordeServer.Discord`, MIT, © 2026 dotBunny. Referencing the engine locally at
build time is fine; **committing Epic engine source or compiled Epic DLLs to this repo would breach
the UE EULA**, and MIT-licensing them is not ours to do. Practical rules:

- Never vendor `HordeServer.*.dll`, `EpicGames.*.dll`, or any engine `.cs` into the repo.
- Don't copy Slack-sink code wholesale — reimplement against the public interface. Mirroring the
  *architecture* is fine; pasting Epic's source into an MIT repo is not.
- `.gitignore` build output so engine DLLs copied next to the plugin never get committed by accident.
- CI on a public runner won't have the engine, so it can lint/format but cannot compile. Plan for a
  self-hosted runner or local-only builds.
- This document itself carries method names, line numbers, and the root-cause category vocabulary
  read out of Epic's source. That's descriptive rather than a copy of the source, but if the repo is
  ever made a public showcase, give it a second look first.

> `INotificationSink` is an internal interface with **no stability guarantee** — `SendSessionConflictReportAsync`
> looks like a recent addition. A stale plugin DLL against a newer server surfaces as a
> `TypeLoadException`/`MissingMethodException` during plugin load, not a graceful skip. Mitigations in §6.

### 3.2 Do **not** take a Discord SDK dependency — hand-roll it

`Assembly.LoadFrom` in .NET Core resolves a loaded assembly's dependencies by probing the directory
it was loaded from — which here is `AppDir`, so co-dropped DLLs do resolve. But:

- `Discord.Net` / `DSharpPlus` drag in `Newtonsoft.Json` and several satellite assemblies. Horde is
  `System.Text.Json` throughout.
- The app's own `deps.json` wins for assembly names it already knows. Version collisions between
  plugin dependencies and server assemblies are resolved first-load-wins — a genuinely nasty class
  of runtime bug to diagnose inside someone else's CI server.
- A "drop-in DLL" that is actually 12 DLLs is a worse deployment story.

Hand-rolling mirrors `EpicGames.Slack` exactly and keeps the drop to **one file with zero new
transitive dependencies** — `System.Net.WebSockets` and `System.Text.Json` are in-box, and
`Microsoft.Extensions.Http` / `.Polly` already ship in the server output.

Cost: we own the gateway state machine. Discord's is meaningfully more involved than Slack Socket
Mode — `IDENTIFY`, heartbeat with jitter, `RESUME` with session id + sequence number, resumable vs.
non-resumable close codes, and reconnect backoff. Budget for it (§5, Phase 4).

### 3.3 Behavioural gaps that need a design answer, not just code

1. **No email→user lookup.** *(Resolved: hand-maintained map.)* Discord's API has no equivalent of
   Slack's `users.lookupByEmail`, and Horde only knows `user.Email`. A static `email → snowflake`
   map lives in the **hot-reloadable plugin config** (not server config) so adding a new hire doesn't
   need a server restart. Put it behind a small `IDiscordUserResolver` so a `/link` slash command can
   be added later as a second provider without redesign. An unmapped user must degrade to plain-text
   name, never to a dropped notification — and should log once at warning, not per message.
2. **Channels are snowflake IDs, not names.** *(Resolved 2026-07-25: a Slack-id → Discord translation
   table. This supersedes the original text below, which was written on a false premise.)*

   **The premise was wrong.** Horde does not address channels by name — it stores **Slack channel
   ids**, `C0832ESJUR5`, everywhere it carries one. That changes the answer entirely, and for the
   better:

   - Slack channel ids are **stable across renames**, so a mapping keyed on one cannot silently rot
     the way a `#name` key would. This was the only real objection to keying on the Slack value.
   - Slack ids (`[CGD]` + uppercase base-36) and Discord snowflakes (15–25 digits) are **disjoint
     formats**, so a value in the wrong place is *detectable* rather than merely broken.
   - Every destination in Horde already arrives at the sink as one of these strings — see the table
     below — so the plugin never has to reproduce Horde's routing. It translates the last hop only.

   **Design: a flat map in the hot-reloadable `DiscordConfig`, keyed by Slack channel id.**

   ```jsonc
   {
     "guilds": { "studio": "112233445566778899" },
     "channels": {
       "C0832ESJUR5": { "label": "horde-triage", "guild": "studio", "channel": "9988…" },
       "C085J3A6FHN": { "label": "horde-builds", "channel": "1122…" }   // default guild
     },
     "fallbackChannel": "5555…"
   }
   ```

   `label` is documentation, not data — both sides are opaque ids and nothing else makes the file
   readable. A single configured guild is the default without naming one.

   Where every channel reaches us:

   | Source | How |
   |---|---|
   | `IssueReportGroup.Channel` | passed to `SendIssueReportAsync` |
   | `IssueReport.TriageChannel` | per report, same call |
   | `WorkflowConfig.ReportChannel` / `.TriageChannel` | `IOptionsMonitor<BuildConfig>` → `TryGetStream` → `TryGetWorkflow` |
   | `StreamConfig.TriageChannel`, `TemplateRefConfig.TriageChannel` | the same, as fallbacks |
   | `DeviceIssueReport.Channel` | on the report |
   | `BuildServerConfig.*NotificationChannel` | `IOptions<BuildServerConfig>` |

   All three injection points are registered generically — `PluginCollection` calls
   `AddPluginConfig<TGlobalConfig>` (which registers `IOptionsFactory`/`IOptionsChangeTokenSource`,
   so `IOptionsMonitor<T>` works and hot-reloads) and `Configure<TServerConfig>` for every plugin.
   **Consequence: per-workflow triage routing costs nothing extra and is available from Phase 1, not
   Phase 3.**

   **Unmapped channels go to a configured `fallbackChannel`,** with the message naming the Horde
   channel it was meant for; without one, they are logged once per distinct channel and dropped.
   `DiscordRoutingReport` walks `BuildConfig` at startup and on every reload and names every unmapped
   channel, because a gap in an id-to-id map is otherwise invisible until a notification fails to
   arrive.

   `DiscordServerConfig.*NotificationChannel` remain as **Discord-native overrides** that win over
   the translation, so a deployment running Discord without Slack never has to invent Slack ids.

   **Horde is not consistent about ids, though** (found 2026-07-25, after the design was written).
   Two Build plugin settings hold a bare channel *name*, because the Slack sink prepends the `#`
   itself — `SlackNotificationSink.cs:411`, `:644`, `:2640`:

   | Setting | Holds |
   |---|---|
   | `JobNotificationChannel`, `UpdateStreamsNotificationChannel` | bare channel **name** |
   | everything else, including all workflow and report channels | Slack channel **id** |

   So a map key is "whatever string Horde carries", usually an id and occasionally a name. Both are
   accepted; only a key that is plainly the *Discord* side, or one carrying a `#` Horde never stores,
   is warned about. Being stricter would produce false alarms on the two name-based settings.

   **Job completion routing is separate from the base category.** Horde routes completions through
   `job.NotificationChannel` then `streamConfig.NotificationChannel`, each with an optional
   `NotificationChannelFilter` (`|`-separated `LabelOutcome` names), and *not* through
   `JobNotificationChannel` — that one is for scheduling notices and timed-out steps. Mirrored in
   `DiscordChannelResolver.ResolveJobCompletion`, with one deliberate departure: when neither is
   configured we fall back to the Discord-native override, because a fresh install with only that
   filled in should not be silent. Horde would send nothing.

   > *Original text, retained per the no-silent-rewrites rule:* "Slack config accepts `#channel`.
   > Either require IDs in config or resolve names once at startup from the guild channel list and
   > cache."
3. **Embed limits.** 10 embeds/message, 25 fields/embed, 1024 chars/field value, 6000 chars total,
   2000 chars/message content. The Slack sink builds long log-excerpt blocks
   (`AddLogDataContext`) that will need truncation.
4. **Components — the `markfixed` modal cannot be ported straight across.** *(Resolved: hybrid.)*
   Slack `ActionsBlock` → Discord action rows (5 buttons/row, 5 rows) maps cleanly. The modal does not.
   Slack builds **up to seven** inputs (`SlackNotificationSink.cs:3944-4062`) and three of them are
   non-text components, which Discord modals do not support at all:

   | Field | Slack element | Line | Disposition |
   |---|---|---|---|
   | `fixed_by` | `RadioButtonGroupElement` | 3965 | Default to the invoking user — already Slack's default (`InitialOption = ownerOptions[0]` = "Me", 3966), and the block only renders when the issue has a *different* owner |
   | `fix_cl` | `PlainTextInputElement` | 3971 | **Text input, required** — the only non-optional field in the whole modal |
   | `rootcause_owner` | `RadioButtonGroupElement` | 4035 | Left unset; `Optional = true` (4039) |
   | `rootcause_category` | `StaticSelectMenuElement`, 12 options | 3993 | **Follow-up dropdown**, see below |
   | `rootcause_summary` | `PlainTextInputElement` multiline | 4045 | Text input, optional, prefilled from `issue.RootCauseSummary` |
   | `rootcause_cl` | `PlainTextInputElement` | 4049 | Text input, optional, prefilled from `issue.RootCommitId?.Name` |
   | `rootcause_dupeid` | `PlainTextInputElement` | 4055 | Text input, optional, prefilled from `issue.DuplicateIssueId` |

   **Hybrid flow:** the *Mark Fixed* button opens one modal carrying the four text-typed fields
   (4 of the 5 allowed). On submit, apply the fix immediately. Then — **only if `rootcause_summary`
   came back non-empty** — post an ephemeral follow-up with the 12-option category select (legal in
   a message, just not in a modal) and apply the category on selection.

   Rationale: closing out a fix stays a single interaction, which is the overwhelmingly common path;
   the controlled category vocabulary is only demanded of people actually doing root-cause analysis.
   Cost is one extra interaction handler and a short-lived (issue id → pending category) association,
   which can be encoded entirely in the component `custom_id` — no server-side state needed.

   **Built and verified live, 2026-07-26.** The flow works as designed, and building it turned up one
   thing the design did not anticipate: **a modal can only be the first answer to an interaction.**
   Discord refuses to open one against an interaction that has already been answered, deferral
   included — which is exactly what `DiscordInteractionRouter` does to everything to beat the
   three-second deadline. The resolution is an `answersForItself` predicate on `Register`, per verb
   rather than per scope, so `markfixed` runs unacknowledged and answers with the modal itself while
   `ack` and the rest keep their deferral. It is affordable only because opening a modal is a single
   request; anything slower in front of it would blow the deadline.

   The follow-up dropdown is a consequence of the same rule from the other end. By the time the fix
   has been applied the submission is long since acknowledged, so the category question cannot be the
   *response* to it — it is posted as an ephemeral **followup**
   (`POST /webhooks/{appId}/{token}`, `DiscordClient.CreateFollowupMessageAsync`).
5. **Rate limits.** Per-route buckets plus a global limit, communicated via `X-RateLimit-*` headers
   and `429` + `retry_after`. A build farm bursts hard on a broken stream. The client needs real
   bucket handling, not just Polly retries.
6. **Threading.** The triage flow is thread-shaped in Slack — `CreateOrUpdateWorkflowThreadAsync`
   (line 1069) posts a parent message per issue, then hangs `_buttons`, `_triage`, fixed/fix-failed
   updates off it (lines 1238, 1405, 1422, 1443). Discord threads are real channel objects with their
   own lifecycle and auto-archive window. Recommend genuine Discord threads (`POST /channels/{id}/threads`
   from the parent message) since the parent→children shape matches exactly; set a long auto-archive
   and store the thread id in the message-state document alongside the parent message id.

   **Correction (2026-07-26): threads need no message-state document at all.** Two things were missed
   when the above was written.

   - **Horde already stores the pointer.** `IIssue.WorkflowThreadUrl` is a per-issue field, written via
     `IIssueCollection.TryUpdateIssueAsync(…, new UpdateIssueOptions { WorkflowThreadUrl = … })` — and
     it is exactly what the Slack sink stores its triage thread permalink in
     (`SlackNotificationSink.cs:1154`). `IssueService.UpdateIssueAsync` also takes it directly.
   - **A Discord thread's id *is* its source message's id.** So a single stored link of the form
     `channels/{guild}/{channel}/{messageId}` yields the parent channel, the parent message id for
     edit-in-place, *and* the thread id. Both halves of §3.3.6 fall out of one field Horde already
     persists.

   **But the field has one slot and Slack wants it too.** A studio running both sinks would find its
   Slack triage links overwritten by Discord ones, which breaks the "runs alongside Slack, unchanged"
   promise in §2. **Decision (2026-07-26):** mirror the `EnableDeepLinks` precedent — a `bool?` that
   defaults to writing `WorkflowThreadUrl` only when the Build plugin has no `SlackToken`, and can be
   set either way explicitly. When Slack owns the field, Discord threads are created per issue and not
   reused, which is a degradation rather than a failure.

   This retires `DiscordMessageStateCollection` as a prerequisite. It stays on the table only if
   per-message state beyond the triage parent is ever needed.
7. **No invite-to-channel equivalent.** Slack pulls suspects into the triage channel with
   `InviteUsersAsync` (line 1011), escalating through an admin token for restricted users (1036).
   Discord has no API for adding an existing member to a channel they can already see, and no
   equivalent of Slack's restricted-user problem. The Discord behaviour is simply to **@-mention**
   them in the thread — which is why the user map is required even though DMs are also in scope.
   `SlackAdminToken` and its whole escalation path have no counterpart and should not be ported.
8. **A bespoke Discord routing document.** *(Considered 2026-07-25, recorded, deliberately not built.)*

   Nothing prevents it. `DiscordConfig` is ours, and the config machinery is available as ordinary
   API — `[JsonSchema]`, `[JsonSchemaCatalog]`, `[ConfigDoc]`, `[ConfigIncludeRoot]`,
   `[ConfigMacroScope]` on our own document type, held as a list on the plugin config, exactly the
   shape Experimental uses. Applying Epic's attributes is API use, not copying (§3.1a). Everything a
   rule would match on is already on the notification parameters: `IJob.StreamId`, `TemplateId`,
   `Name`, `INode.Name`, the outcome enums, and for issues the span's `StreamId` plus workflow id.

   **What it would buy** — only things Horde has no opinion about, and therefore nothing to translate:
   splitting failures and successes across two channels, choosing a guild per rule, thread versus
   top-level message, a role to ping per stream, and working with **no Slack configuration at all**,
   which the §3.3.2 table structurally cannot do since it needs a Slack id as its key.

   **Why not now.** Three reasons, in order of weight:

   - It cannot express *per workflow* cleanly. Rules key on stream and template; a workflow is a
     different axis. For issue triage the channel id **is** the workflow's routing identity, so the
     table is not merely sufficient, it is the better fit.
   - It is a second source of truth. Re-point a workflow's `triageChannel` in Horde and Discord would
     not follow, because a bespoke rule matched first — the precise drift §3.3.2 avoids.
   - §1.8 suggests Epic is incubating a core job-notification routing model. A bespoke document would
     most likely overlap with whatever ships.

   **If it is ever built:** precedence must be explicit — bespoke rule → §3.3.2 table → base category
   → fallback — and `DiscordRoutingReport` should print *which* mechanism resolved each channel.
   With two overlapping systems, "why did it go there" is the only question that matters.

### 3.4 What is *not* needed

`IAvatarService` and `IRcaNotifier` are Slack-only side roles the Build plugin wires up at
`BuildPlugin.cs:188-190`. Since Slack keeps running alongside, the Discord plugin implements
`INotificationSink` only.

---

## 4. Proposed shape

Legend: ✅ built in Phase 0, ▫️ still to come.

```
HordeServer.Discord/                         (this repo)
├─ Directory.Build.props                  ✅ TargetFramework net10.0, Nullable, ImplicitUsings,
│                                            Configurations=Debug;Development;Release
│                                            (do NOT import UnrealEngine.csproj.props — replicate
│                                             the ~6 properties that matter); resolves $(HordeBinDir)
├─ Directory.Build.targets                ✅ validates HordeBinDir before ResolveAssemblyReferences
├─ Horde.local.props.template             ✅ committed; copied + edited per machine
├─ HordeServer.Discord.slnx               ✅ must declare the Development configuration explicitly
├─ .gitignore                             ✅ Horde.local.props, bin/, obj/, engine DLLs
├─ HordeServer.Discord/
│  ├─ HordeServer.Discord.csproj          ✅ AssemblyName is load-bearing (HordeServer.*.dll scan);
│  │                                         engine refs via $(HordeBinDir), all Private=false
│  ├─ DiscordPlugin.cs                    ✅ [Plugin("Discord", EnabledByDefault=false,
│  │                                          ServerConfigType=…, GlobalConfigType=…)]
│  ├─ DiscordServerConfig.cs              ✅ : PluginServerConfig — bot token, ids, channel ids
│  ├─ DiscordConfig.cs                    ✅ : IPluginConfig — user map, guilds, and the Slack-id
│  │                                          -> Discord channel map (§3.3.2), validated in PostLoad
│  ├─ DiscordChannelMapping.cs            ✅ one entry in that map: label, guild, channel
│  ├─ Notifications/
│  │  ├─ DiscordNotificationSink.cs       ✅ the 17 INotificationSink members; everything except the
│  │  │                                      two issue members and the two link members forwards to
│  │  │                                      the processor
│  │  ├─ DiscordNotificationProcessor.cs  ✅ formatting + routing (the ExperimentalSlack… pattern),
│  │  │                                      grouped in regions matching the sink's own grouping
│  │  ├─ DiscordRepeatFilter.cs           ✅ what has already been said, so a condition that persists
│  │  │                                      is announced once and its recovery pairs with it
│  │  ├─ DiscordUserResolver.cs           ✅ IDiscordUserResolver over the config user and role maps,
│  │  │                                      warn-once on unmapped users. Not cached - see Phase 3
│  │  ├─ DiscordChannelResolver.cs        ✅ Slack channel id -> Discord guild+channel (§3.3.2),
│  │  │                                      with the catch-all fallback and warn-once
│  │  ├─ DiscordChannelIds.cs             ✅ tells Slack ids and Discord snowflakes apart, which is
│  │  │                                      what makes a misplaced value detectable
│  │  ├─ DiscordRoutingReport.cs          ✅ names every unmapped Horde channel at startup and on
│  │  │                                      each config reload
│  │  ├─ DiscordMessageStateCollection.cs ▫️ Mongo "DiscordV1", unique (Recipient, EventId),
│  │  │                                      also stores thread ids (§3.3.6). Deferred to Phase 4,
│  │                                         where the first consumer is - see Phase 1 below
│  └─ Client/                                the hand-rolled client (mirrors EpicGames.Slack)
│     ├─ DiscordClient.cs                 ✅ REST: create/edit message, open a DM channel (cached).
│     │                                      Threads and members arrive with Phase 4
│     ├─ DiscordRateLimiter.cs            ✅ per-route buckets + global, over an IDiscordClock seam
│     ├─ DiscordEmbed*.cs / Message*.cs   ✅ builders, with every limit in §3.3.3 enforced
│     ├─ DiscordMarkdown.cs               ✅ escaping for text that came from a build log
│     ├─ DiscordGateway.cs                ✅ identify/heartbeat/resume/dispatch, over an IDiscordWebSocket
│     │                                      seam. DiscordGatewayProtocol.cs holds the opcodes and the
│     │                                      pure close-code/backoff policy
│     ├─ DiscordComponent.cs              ✅ action rows and buttons, wrapping at 5 per row. Modals
│     │                                      and select menus arrive with the Mark Fixed flow
│     ├─ DiscordInteraction.cs            ✅ inbound interaction, callback types, and DiscordCustomId
│     │                                      (Slack's issue_{id}_{verb}[_{userId}] grammar, kept)
│     └─ Notifications/                   ✅ DiscordInteractionRouter: gateway dispatch → registered
│        DiscordInteractionRouter.cs         handler, acknowledging before it runs
├─ tools/PluginProbe/                     ✅ the load probe - replicates
│                                            ServerApp.CreatePluginCollection without Mongo/Redis.
│                                            Now a shared library with a console front end; the
│                                            tests run the same Probe.Run and assert on its result
├─ tools/DiscordSmoke/                    ✅ posts one of every notification to a real channel, so the
│                                            formatting can be *looked at*. Drives the real processor
│                                            with stand-in data; no Horde server, Mongo or Redis.
│                                            Credentials from Horde.local.props, baked into this
│                                            assembly and no other
├─ tools/HordeTestDoubles/                ✅ stand-ins for the Horde types a notification arrives
│                                            with. Its own assembly because MSTest resolves the base
│                                            types of everything the *test* assembly declares during
│                                            discovery, before the module initializer has installed
│                                            the engine assembly resolver - so a fake implementing an
│                                            engine interface cannot live beside its tests
└─ HordeServer.Discord.Tests/             ✅ MSTest, mirroring HordeServer.Experimental.Tests.
                                             Deploys the plugin into $(HordeBinDir) itself, then
                                             probes - no copy step to forget
```

**Reference set** (discovered by building): `HordeServer.Build`, `HordeServer.Shared`,
**`HordeServer.Compute`** (`ILogEventData`, reached through the job-step members — not obvious from
reading the interface), `EpicGames.Horde`, `EpicGames.Core`, plus
`<FrameworkReference Include="Microsoft.AspNetCore.App" />` for `IApplicationBuilder` /
`IServiceCollection` / `ILogger`.

**Server config** (`server.json`) — secrets and infrastructure. Restart to change.

```jsonc
"Horde": {
  "Plugins": {
    "Discord": {
      "Enabled": true,
      "BotToken": "…",                      // prefer the Secrets plugin / env var
      "ApplicationId": "…",
      "GuildId": "…",                       // single guild
      "EnableInteractions": true,           // gateway on/off, independent of posting
      // Overrides only, normally unset. Routing comes from the Build plugin's own channel settings,
      // translated through the 'channels' map below. Discord snowflakes, ';'-separated.
      "JobNotificationChannel": "…",
      "AgentNotificationChannel": "…",
      "ConfigNotificationChannel": "…",
      "UpdateStreamsNotificationChannel": "…",
      "DeviceNotificationChannel": "…",
      "ErrorEmoji": "<:horde_error:…>",
      "WarningEmoji": "<:horde_warning:…>"
    }
  }
}
```

**Global plugin config** (`*.discord.json`, hot-reloadable via the config system) — routing and the
user map, so onboarding someone or re-pointing a stream doesn't need a server restart.

```jsonc
{
  "userMap": {
    "someone@dotbunny.com": "1234567890"    // email → Discord snowflake
  },
  "roles": {                                // Horde alias → Discord role, for triage pings (Phase 4)
    "S0123456789": "7766…"
  },
  "guilds": { "studio": "112233445566778899" },
  "channels": {                             // Slack channel id → Discord destination (§3.3.2)
    "C0832ESJUR5": { "label": "horde-triage", "guild": "studio", "channel": "9988…" },
    "C085J3A6FHN": { "label": "horde-builds", "channel": "1122…" }
  },
  "fallbackChannel": "5555…"                // anything unmapped, so nothing is silently lost
}
```

There is no per-stream routing block. Horde already routes per workflow, stream and template, and
hands the sink the resulting channel; reproducing that here would be a second, competing source of
truth.

**Narrower than it first sounds.** This does not close off a bespoke routing document — see §3.3.8. It
says only that *for the routing Horde itself performs*, there is nothing left for us to model.

Note `GuildId` is only needed for member lookup and slash-command registration — posting uses
`POST /channels/{id}/messages`, and channel snowflakes are globally unique. Keeping the guild out of
the posting path is what makes multi-guild additive later rather than a refactor.

---

## 5. Phasing

Ordered so the riskiest unknown is resolved first and every phase ships something usable.

### Phase 0 — Prove the drop-in ✅ **DONE**
Skeleton repo, csproj, `[Plugin("Discord")]` startup, both config classes, and an
`INotificationSink` whose 17 members are no-ops that log at debug.

**Verified** by a harness replicating `ServerApp.CreatePluginCollection` (filename scan →
`Assembly.LoadFrom` → `GetExportedTypes` → `PluginAttribute` → `PluginCollection.Add` → config bind),
run against the real server output directory with the plugin DLL dropped in:

- `HordeServer.Discord.dll` matches the scan pattern and is discovered as a **peer of Epic's 13
  plugins** — `found plugin 'Discord' (default-off)`.
- `PluginCollection.Add` succeeds, which is what validates the generic constraints on
  `DiscordServerConfig` / `DiscordConfig`. Note `PluginName` normalises to lowercase (`'discord'`).
- Server config binds from `Horde:Plugins:Discord:*`, and the enablement rule resolves to *load*.
- Build output is **one 13.8 KB DLL**, no engine assemblies leaked.

Not yet done: booting the real server (needs Mongo + Redis) to watch the sink receive live callbacks.
That is the one remaining Phase 0 confirmation and it needs a deployed Horde.

The harness lives in the repo at **`tools/PluginProbe`** (rescued 2026-07-25 from a session scratchpad,
where it would have been garbage-collected). It was **promoted into `HordeServer.Discord.Tests` the
same day**, which is now the gate:

```bash
dotnet test -c Development
```

The probe is the shared implementation; the console tool is its human-facing renderer, kept because a
step-by-step report beats a single failed assertion when an engine upgrade breaks something. The test
project deploys the plugin into `$(HordeBinDir)` itself, so the copy step that used to be the most
common source of misleading results is gone.

Two things in the probe are load-bearing and easy to break: an `AssemblyLoadContext.Default.Resolving`
hook is needed because neither the probe nor the test host is the app that owns those assemblies, and
it must be installed **before the JIT touches any Horde type**. The console tool does that with a
`[MethodImpl(MethodImplOptions.NoInlining)]` split; the test assembly does it with a
`[ModuleInitializer]`, because MSTest reflects over test classes long before it would run an
`[AssemblyInitialize]`. Get either wrong and the engine assemblies fail to load.

The test project deliberately references the plugin with `ReferenceOutputAssembly="false"`. A normal
reference would put a second copy of `HordeServer.Discord.dll` in the test output, and since the
default load context resolves by assembly identity, `Assembly.LoadFrom` would hand back *that* copy —
so the tests would pass against a build that was never deployed.

Beyond the original Phase 0 checks, the suite adds two guards worth keeping:

- **`INotificationSink` member count (17).** A member added or removed breaks the build, so it never
  reaches a test — but a *default* interface method would not, and would leave a notification silently
  unhandled. `GetInterfaceMap` catches exactly that case.
- **Drop shape.** One assembly, no `HordeServer.*` / `EpicGames.*` leakage — the automated form of the
  `<Private>false</Private>` rule in §3.1 and the no-vendoring rule in §3.1a.

### Phase 1 — REST client + job/step outcomes ✅ **COMPLETE, VERIFIED AGAINST DISCORD 2026-07-26**
Minimal REST client (post/edit message, embeds) + rate limiter. Implement `NotifyJobCompleteAsync`,
`NotifyJobStepCompleteAsync`, `NotifyJobStepAbortedAsync`, `NotifyLabelCompleteAsync`,
`NotifyJobScheduledAsync`. Channel routing from server config. **This is the point where it's
genuinely useful.**

Built 2026-07-25:

- `DiscordRateLimiter` — per-route buckets, the 50/s global ceiling, `X-RateLimit-Scope` handling.
  Time is behind an `IDiscordClock` seam so the tests assert *what it decides* rather than sleeping.
- `DiscordClient` — `/api/v10` pinned in the base address, create/edit message, failures reported as
  null rather than thrown. Owns a private `HttpClient`; **not** an `IHttpClientFactory` typed client,
  because a transient typed client held by a singleton sink is a captive dependency and registering a
  bare `HttpClient` would hijack the host server's own resolution.
- `DiscordEmbedBuilder` / `DiscordMessageBuilder` — every limit in §3.3.3 enforced, including the
  combined 6000 that the per-value limits do not imply. Truncation is always marked.
- `DiscordNotificationProcessor` + the five job/step members.
- `DiscordChannelResolver` — the Slack-id → Discord translation from §3.3.2, with the catch-all
  fallback and warn-once, plus `DiscordRoutingReport` naming unmapped channels at startup. Routing
  moved out of `DiscordServerConfig` and into the hot-reloadable config as part of this.

**78 tests.** Two real bugs came out of writing them: the overflow-notice reserve was taken *after* the
description had already spent the budget, and truncation could split a surrogate pair.

> **Deferred: the Mongo message-state collection.** It was listed here for edit-in-place, but nothing
> in Phase 1 edits anything — a finished job does not change, so each outcome is a fresh post. Its
> first real consumer is issue triage in Phase 4, where the parent message *and* its thread id both
> need storing (§3.3.6). Building it now would mean an unused collection with no way to test it short
> of standing up MongoDB. `DiscordClient.EditMessageAsync` exists and is tested, so the client half is
> ready when the consumer arrives.

Not yet verified: anything about the messages themselves. No Discord application exists yet, so no
message has ever been sent. Formatting, colours, embed rendering and channel permissions are all
unexercised.

### Phase 2 — Remaining broadcast notifications ✅ **COMPLETE, VERIFIED AGAINST DISCORD 2026-07-26**
`NotifyConfigUpdateAsync` / `NotifyConfigUpdateFailureAsync`, `SendAgentReportAsync`,
`NotifyDeviceServiceAsync`, `SendDeviceIssueReportAsync`, `SendSessionConflictReportAsync`,
`NotifyTestHealthReportAsync`. All channel-post, no interactivity — mostly formatting work.

Built 2026-07-25. Four things came out of it that were not in the plan:

- **`DiscordRepeatFilter`** — an in-memory, expiring, capacity-bounded map of event id to state digest.
  Not anticipated, but not optional either: `NotifyConfigUpdateAsync` fires on *every* pass of Horde's
  config ticker, including every pass while a file stays broken, so a sink that posts unconditionally
  turns one unclosed brace into a channel full of identical messages. Slack solves it with a digest in
  the Mongo message-state collection; this is the same idea without the persistence, which is only
  needed for the *other* half of what Slack's collection does (editing a message already posted). It
  also drives the recovery messages: "configuration update succeeded" and "test health recovered" are
  sent only to a channel that was told about the problem. Folds into the Phase 4 collection when that
  arrives. Cost of being in memory: a restart re-announces a still-broken config once, which is
  arguably correct.
- **`IUserCollection` is now injected into the processor.** `NotifyTestHealthReportAsync` carries its
  carbon-copied owners as `UserId` strings, and dropping them would lose the only thing on that report
  that says whose test it is. It resolves to `IUser`, which `SendAsync` already knows how to name in
  plain text, so Phase 3 swaps names for mentions at one site rather than reworking this.
- **`NotifyDeviceServiceAsync` departs from Slack.** Slack sends it as a DM and sends *nothing at all*
  when it cannot identify the user — and since both in-tree callers pass a user, the rich attachment
  branch in `SendDeviceServiceMessageAsync` is unreachable. Ours posts to the device channel naming the
  person, matching the interim the job members already take. Phase 3 turns it into a DM with the
  channel post as the fallback for an unmapped user.
- **Test health is keyed on `(StreamId, TestId)`, not `ITestHealthReport.Id`.** Slack keys on the report
  document. Keying on the test is stable if a fresh document is ever written — which is exactly when a
  recovery must still pair with the degradation before it — and it keeps `MongoDB.Bson.ObjectId`, and
  therefore a MongoDB reference, out of the plugin entirely.

Device pool severity uses the same load and problem-rate thresholds as the Slack sink (40/50 and
20/30). Deliberate: both sinks report on the same farm, and a platform that is red in one channel and
orange in the other is worse than either being wrong.

**131 tests**, up from 78. The processor had no test coverage at all before this phase; it now has
end-to-end tests that assert on the JSON that would reach Discord, which is the only level at which the
interesting failures show up — a code fence severed by the 1024-character field limit, an embed over
the combined ceiling, a link built into a field name where Discord renders it as source.

### Phase 3 — Users, mentions, DMs ✅ **COMPLETE, VERIFIED AGAINST DISCORD 2026-07-26**
`IDiscordUserResolver` over the hot-reloadable config map, with `MemoryCache` and warn-once on
unmapped users. DM channel creation (`POST /users/@me/channels`), @-mention rendering, per-user
`NotifyJobCompleteAsync(IUser, …)`, `GetDirectMessageLinkAsync` / `GetChannelLinkAsync`.

Also a `roles` table alongside `channels`: Slack's `TriageAlias`, `EscalateAlias` and
`TriageTypeAliases` are user-group handles, and the Discord equivalent is a role mention
`<@&roleId>`. Same translation-table shape, same reasoning as §3.3.2.

Built 2026-07-25.

**The largest thing in this phase was a correction to Phase 1, not new work.** Reading the Slack sink
member by member for the first time with DMs available showed that four of the job/step members had
been given the wrong shape:

| Member | Slack | Phase 1 shipped | Phase 3 |
|---|---|---|---|
| `NotifyJobCompleteAsync(IJob, …)` | channel | channel ✅ | unchanged |
| `NotifyJobCompleteAsync(IUser, …)` | DM only, skipping `job.AbortedByUserId` | job channel, "For X" | DM, channel fallback, aborter skipped |
| `NotifyJobStepCompleteAsync` | DM per subscriber; channel only on `TimedOut` | job channel | DM per subscriber; channel on `TimedOut` |
| `NotifyJobStepAbortedAsync` | **no-op** | job channel | DM per subscriber, nothing without one |
| `NotifyLabelCompleteAsync(IUser, …)` | DM only | job channel | DM, channel fallback |

These are *subscription* notifications, one per subscriber per step. Broadcasting them to a shared job
channel — which is what Phase 1 did as its "the information still arrives" interim — would have made
the job channel unusable on a busy stream. The interim was right for Phase 1 and wrong to keep.

One deliberate difference from Slack survives: a step that hits its time limit is reported to the job
channel **whether or not anyone subscribed**. Slack checks for subscribers and returns before reaching
its timeout branch, which reads as an ordering accident — that branch does not look at the subscriber
list at all — and a step hitting its time limit is a farm problem rather than a subscriber's.

Other decisions:

- **The user map is not cached, reversing the sketch above.** Caching made sense while this was
  imagined as an API lookup like Slack's `users.lookupByEmail`. Over a dictionary in the hot-reloadable
  config it buys nothing and costs the hot reload the map lives there to get — adding somebody should
  start mentioning them, not start a cache expiry countdown. The *DM channel id* is cached instead,
  because that one is a round trip; opening a DM is idempotent and returns the same channel forever.
- **Deep links default to answering only when nobody else will.**
  `NotificationService.GetDirectMessageLinkAsync` takes the **first non-null answer from any sink and
  ignores the rest**, and sink order is registration order, which a plugin does not control. A sink
  that always answered would decide by luck whether an existing Slack deployment's dashboard buttons
  still opened Slack — precisely the "runs alongside Slack, unchanged" promise in §2. So
  `DiscordServerConfig.EnableDeepLinks` is a `bool?`: unset means provide links only when the Build
  plugin has no `SlackToken`, and setting it overrides in either direction.
- **A DM link is one recipient only.** Slack supports up to eight in a multi-person DM. Discord's group
  DMs need OAuth scopes a bot cannot have, so more than one person gets null rather than a link to the
  wrong conversation.
- **Nothing is ever dropped for want of a mapping.** An unmapped user, a bot sharing no guild with
  them, or DMs turned off all degrade the same way: the notification goes to the fallback channel with
  them mentioned if known and named in plain text if not. The fallback triggers on a failed *send* as
  well as a failed channel open, because Discord will open a DM channel and then refuse the message.
- **The `roles` table is configurable but nothing pings a role yet** — its consumer is issue triage in
  Phase 4. It earns its place now because `DiscordRoutingReport` walks every workflow's `triageAlias`,
  `escalateAlias` and `triageTypeAliases` and names the gaps at startup, so the map can be filled in
  before it is urgent rather than during an outage.

**First real send, 2026-07-26.** A Discord application was created and the bot invited to a guild, and
all fifteen `tools/DiscordSmoke` scenarios were posted to a live channel — twelve channel messages and
five DMs, the mention among them. Three things came out of it, in ascending order of interest:

1. `DiscordSmoke` could not start. It references the engine assemblies with `Private=false` like
   everything else here, so it needs `EngineAssemblyResolver` installed before it touches a Horde type,
   exactly as the test assembly does. It had never been run.
2. `DiscordSmoke` then reported all fifteen scenarios sent while Discord was rejecting every one with a
   403. `DiscordClient` logs a failed request and returns rather than throwing — required of it, since
   a sink that throws inside the server disturbs the other sinks — so returning normally says nothing
   about arrival. The tool now watches the log (`SmokeLog`) and a scenario passes only on silence.
   **A verification tool that cannot fail is worse than no tool**, and this one had been quietly
   incapable of failing since it was written.
3. The shipped bug: `ErrorPrefix` and `WarningPrefix` defaulted to `:red_circle:` and `:warning:`,
   ported across from the Slack sink's settings. **Slack resolves shortcodes server-side; Discord does
   not** — its client expands them as a human types, so anything a bot posts through the API keeps the
   colons. Every error and warning title in the plugin carries one of these. The unit tests blank both
   prefixes to keep expected payloads readable, so nothing in 175 tests could have caught it; the
   regression test in `DiscordServerConfigTests` asserts on the defaults themselves instead.

The third is the one that justifies the phase gate. It was invisible to every assertion in the suite,
survived three phases, and took one glance at a real channel.

**164 tests**, up from 131. The job and step members remain untested end to end: `IJob` has 61 members
and `IJobStep` 38, so a stand-in is a disproportionate amount of boilerplate. The routing they now
depend on — `SendToUsersAsync`, `TrySendDirectAsync` and mention rendering — is public on the processor
and tested directly, which leaves those members as thin wrappers over covered code.

> **Per-stream routing left this phase in Phase 1.** §3.3.2 delivered it as a side effect: Horde
> resolves the stream's, workflow's or template's channel itself, and the plugin translates the result.

> A bot can only DM users who share a guild with it. Unmapped or un-DMable users must fall back to a
> channel mention, never silently drop.

### Phase 4 — Gateway + interactive issue triage ⚠️ **BUILT AND VERIFIED 2026-07-26, one parity gap (role mentions)**
`DiscordGateway` (identify / heartbeat with jitter / resume with session id + sequence / resumable
vs. terminal close codes / backoff). `NotifyIssueUpdatedAsync` and `SendIssueReportAsync` building
the triage thread (§3.3.6) with action-row buttons, plus DM variants carrying their own buttons.

**Gateway built and verified live, 2026-07-26.** Connects, identifies, and holds a session through a
heartbeat interval against the real gateway. 39 tests drive the state machine through an
`IDiscordWebSocket` seam, which is what makes the cases that matter reachable at all — a zombied
connection, an `INVALID_SESSION`, a close code that must not be resumed. Three things settled while
building it:

- **Intents are zero.** Intents subscribe a bot to categories of guild event, and this plugin wants
  none: `INTERACTION_CREATE` is delivered regardless of them. Worth stating because the alternative is
  expensive — a privileged intent such as `GUILD_MEMBERS` has to be enabled in the developer portal
  and, past 100 guilds, forces the application through Discord's verification. Requesting nothing also
  makes `4014 Disallowed intents` structurally impossible.
- **Closing cleanly destroys the session.** A `1000` close tells Discord the session is finished and
  it discards the state a `RESUME` would replay from, so every deliberate hang-up uses `4000` instead.
  This is the easiest way to write a gateway that silently re-identifies on every reconnect and burns
  through the daily session-start limit while appearing to work.
- **`4007` and `4009` are recoverable but the session behind them is not.** Both mean reconnect;
  neither means resume. Resuming from a sequence the server has forgotten just earns another `4007`.

**Interaction handler built and verified live, 2026-07-26.** A message with buttons was posted to a
real channel, a real person pressed one, and the message rewrote itself and lost its buttons — the
whole inbound path, end to end. `tools/DiscordSmoke -- --interact` is that check, and it needs a human
because the failure mode is the client showing *"This interaction failed"*, which appears in no log.

The shape of `DiscordInteractionRouter` is forced by one constraint. **An interaction must be answered
within three seconds**, and Horde's issue operations are database work behind a service call — not a
budget worth betting an operator's triage flow on. So it acknowledges with a *deferred update* before
calling the handler, which then has fifteen minutes to edit the message through the interaction token.
Handlers never see the deadline. Two consequences worth recording:

- **A failed acknowledgement cancels the work.** Without it the token is useless, so the handler would
  do the work and have no way to report it — and the operator, seeing a failed button, presses again.
- **Handlers run off the gateway's receive loop.** That loop also reads heartbeat acknowledgements, so
  a handler blocking it would eventually be diagnosed as a dead connection and provoke a reconnect.

**The last two sink members landed 2026-07-26, taking the plugin to 17/17.**
`NotifyIssueUpdatedAsync` addresses the person who can act — owner, then nominee — and falls back to
the triage channels of every workflow the issue's streams define one for, which is the Phase 3 rule
applied to a request to act rather than an announcement. `SendIssueReportAsync` is a digest and
therefore the opposite: channel only, never a DM, and no buttons on it.

Three decisions worth recording:

- **Repeated states are suppressed via `DiscordRepeatFilter`, not reposted.** Horde raises
  `NotifyIssueUpdatedAsync` on *every* change, including ones a reader would not notice. The digest
  deliberately excludes `LastSeenAt` and `UpdateIndex` — both move whenever the issue is touched at
  all, and including either would defeat the suppression entirely.
- **A resolved issue carries no action buttons**, only the link. There is no state left to move it to,
  and a button that does nothing is worse than no button.
- **Not yet edit-in-place.** A state change posts a new message rather than rewriting the old one,
  because remembering which message belongs to which issue across a restart needs the Mongo collection
  below. The buttons work regardless — a press carries its own interaction token, and the message it
  is on is edited through that, which is why the interactive flow did not have to wait for it.

**The buttons became real on 2026-07-26.** `DiscordIssueTriage` registers for the `issue` scope and
turns each verb into a call on Horde's `IssueService`. Until then the components were posted and
routed but nothing acted on them — a press logged *"Nothing is registered for interaction scope
'issue'"*.

- **`IHordeIssues` is a seam over `IssueService`**, which is a concrete sealed class that reaches
  MongoDB in its constructor. Without it nothing downstream of a press could be tested, and running
  `dotnet test` without MongoDB or Redis is a property worth protecting. The adapter behind it is one
  call per method and is **the only class in the plugin with no coverage**, deliberately.
- **The user map is now read backwards.** A press identifies its author with a Discord snowflake and
  every issue operation is audited against a Horde user, so `IDiscordUserResolver.GetEmail` resolves
  snowflake → email and `IUserCollection.FindUserByEmailAsync` finishes the job. An unmapped presser
  gets an ephemeral reply naming `userMap` rather than silence — they are looking at the button.
- **Acknowledging means different things in a channel and a DM.** In a DM the reader already owns the
  issue, so it is only an acknowledgement; in a channel it is how an unowned issue gets claimed. Slack
  splits this across two handlers and the distinction is worth keeping. Claiming an issue somebody
  *else* owns asks first, with an ephemeral "Take it anyway".
- **`MongoDB.Bson` is now referenced by the plugin** (`Private=false`, so the drop is still one file).
  Not to name anything — several `UpdateIssueAsync` parameters are `List<ObjectId>`, and the compiler
  resolves every parameter type to bind a call even when the arguments are left at their defaults.

**Triage threads landed 2026-07-26**, completing §3.3.6 with no new storage. One message per issue in
the triage channel, rewritten in place as the issue changes, with a thread hanging off it recording
how it got there; the owner still gets their direct message, because the thread is the shared record
rather than a substitute for telling the person who has to act.

**The load-bearing fact was verified live, not taken from the docs:** a thread created from a message
has the *same id* as that message. Posting a message to the smoke channel produced message
`1530953829321937029` and thread `1530953829321937029`, parented to the channel, auto-archiving at
10080 minutes. That is what makes `DiscordMessageLink` — `channels/{guild}/{channel}/{message}` — a
complete record: the channel, the message to edit, and the thread to post into, all in one URL, which
is exactly the shape of the field Horde already keeps.

`DiscordServerConfig.EnableTriageThreads` is the gate agreed above, and behaves like `EnableDeepLinks`:
unset means claim `WorkflowThreadUrl` only when the Build plugin has no `SlackToken`. A value in that
field that is not a `discord.com` link is left strictly alone and the notification falls back to a
flat post — the cost of being wrong there is a studio's Slack triage links being destroyed, which is
worth a branch.

**One parity gap remains in this phase**, and it is not the thread work: nothing mentions a Discord
role. `GetRoleId`, the `roles` map and `DiscordRoutingReport`'s gap warnings were all built in Phase 3
in anticipation, but no code path calls them — so an issue with nobody assigned reaches the triage
channel without pinging the workflow's `triageAlias`, which Slack does. See §3.3.7.

Interaction handler mapping component `custom_id`s to the same `IssueService` calls the Slack sink
makes — `ack` / `accept` / `decline`, in both DM and channel flavours (Slack keeps two handlers,
lines 3841 and 3874; mirror that split) — plus the hybrid *Mark Fixed* flow from §3.3.4: 4-field
modal, then a conditional category dropdown.

Keep Slack's `custom_id` grammar (`issue_{id}_{verb}` and `issue_{id}_{verb}_{userId}`) — it already
encodes everything needed and Discord allows 100 chars, comfortably more than the 24-hex user id
plus verb requires.

**Not ported:** `SlackAdminToken` / `InviteUsersAsync` escalation (§3.3.7), `IAvatarService`,
`IRcaNotifier` (§3.4).

**Effort:** the Slack sink is 4,229 lines plus a ~2,000-line shared client library. Parity is
realistically **4,000–5,500 lines**. This is a multi-week effort for one engineer, with Phase 4
alone roughly a third of it. Phases 0–1 are a few days and deliver most of the day-to-day value.

---

## 6. Ongoing costs & mitigations

| Risk | Mitigation |
|---|---|
| `INotificationSink` changes on engine upgrade → stale DLL fails at load | Rebuild the plugin as part of the engine-upgrade checklist; record the engine identity in the README (see below); run `dotnet test` after any engine change |
| Plugin DLL not redeployed after a server upgrade | Bake the copy into the Horde deploy step rather than doing it by hand |
| Bot token in `server.json` | Use the existing Secrets plugin or environment variables; never commit |
| Discord API drift | Pin the API version in the base URL (`/api/v10`) |
| Duplicate noise while both sinks run | Route Discord to its own channels initially; only widen once formatting is trusted |
| No `ServerSettings.md` entry (docs are generated from in-tree config classes) | Document settings in this repo's README |

**Correction (2026-07-25): "pin the repo to an engine CL" is not achievable as originally written.**
The reference engine is a *source* build — `Engine/Build/Build.version` reports `5.8.0`, `BranchName`
`UE5`, and **`Changelist: 0`**, so there is no changelist to pin to. The assembly versions do not
discriminate either: every `HordeServer.*.dll` is stamped `1.0.0.0` and only `EpicGames.Horde.dll`
carries a real version (`5.8.0.0`). The usable identity is therefore **engine version + binary
timestamp** (5.8.0, binaries rebuilt 2026-07-25), and on a CL-stamped build the changelist as well.
Until a plugin build is verified against a CL-stamped engine, treat "rebuild and re-run `dotnet test`"
as the real mitigation and the recorded version as advisory.

> **The engine bin directory is build output, and can simply not be there.** On 2026-07-25 the whole
> `HordeServer/bin` tree was absent — no `HordeServer.*.dll` anywhere under the Horde source — so
> nothing in this repo could compile until the server was rebuilt (`dotnet build
> HordeServer/HordeServer.csproj -c Development`, 17s warm). A missing `Horde.local.props` looks
> identical from the error message, so check both. The tests report *inconclusive* rather than failing
> in either case.

Recorded in the README under **Engine compatibility**; update it whenever the reference engine moves.

---

## 7. Resolved questions

All blocking design questions are settled — see the decisions table in §2.

| Question | Resolution | Detail |
|---|---|---|
| Repo location | This repo — public MIT git, separate from the Perforce engine depot | §3.1, §3.1a |
| Guilds | One; guild kept out of the posting path so multi-guild stays additive | §4 |
| `markfixed` modal | Hybrid — 4-field text modal, conditional category dropdown | §3.3.4 |
| User targeting | Full parity — triage-channel threads with mentions **and** DMs | Phase 3, Phase 4 |
| User map | Hand-maintained `email → snowflake` in hot-reloadable config, behind a resolver interface | §3.3.1 |

### Deferred by choice, not blocking

- **`/link` slash command** for self-service user mapping — the resolver interface leaves room for it
  as a second provider; the static map stays as the fallback.
- **Multi-guild** — additive if the posting path never takes a guild id.
- **Discord threads vs. edit-in-place** for triage — *resolved 2026-07-26: both.* Real threads, with
  the parent message rewritten in place. Auto-archive is set to the 7-day maximum; see §3.3.6.
- **Triage role mentions (§3.3.7)** — the `roles` map, `IDiscordUserResolver.GetRoleId` and the
  startup gap report are all in place, but **nothing mentions a role yet**. Slack pings a workflow's
  `triageAlias` when an issue has nobody assigned; the Discord equivalent is a `<@&roleId>` in the
  triage thread. This is the last real parity gap in Phase 4.

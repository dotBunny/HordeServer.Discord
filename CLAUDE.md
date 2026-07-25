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
the decisions taken and why, and the phase breakdown. Current status: **Phase 0 complete** (plugin
loads and is wired into the notification pipeline; sends nothing yet). Phase 1 is the REST client and
job/step outcomes.

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

Output is a single `HordeServer.Discord.dll` (~14 KB at Phase 0). If engine assemblies ever appear in
`bin/`, a `<Private>false</Private>` is missing from a `<Reference>`.

### Verifying the plugin still loads

There is no automated test yet (`HordeServer.Discord.Tests` is planned). The manual check is to copy
the DLL into a Horde server app directory and confirm the server logs `Added plugin 'Discord'`.

Booting a real Horde server needs MongoDB and Redis. A lighter check that needs neither: replicate
`ServerApp.CreatePluginCollection` — enumerate the app dir for `HordeServer.*.dll`, `Assembly.LoadFrom`
each, look for `[Plugin]`, then call `PluginCollection.Add`. That last call is the valuable one; it
validates the generic constraints on the config types. This is worth promoting into a real test — see
`PLAN.md` §6.

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
  plugin cannot disturb the Slack sink even if it throws. Keep members in interface order; it makes
  diffing against the interface tractable when Epic changes it.

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

## Gotchas found the hard way

- **`ILogEventData` lives in `HordeServer.Compute`**, not `HordeServer.Build` — it reaches
  `INotificationSink` through the job-step members. Not discoverable by reading the interface; only
  the compiler tells you.
- **The `HordeBinDir` validation target must run `BeforeTargets="ResolveAssemblyReferences"`.** At
  `BeforeCompile` it fires *after* MSBuild has already emitted MSB3245 warnings for every engine
  reference, burying the actual explanation.
- **`PluginName` normalises to lowercase** — the plugin registers as `discord`, not `Discord`.
- **Discord modals accept text inputs only, max 5.** Slack's "Mark Fixed" view uses radio groups and a
  select menu and can present seven inputs. This is a component-type mismatch, not a count problem.
  The agreed resolution is in `PLAN.md` §3.3.4.
- **Discord has no lookup-user-by-email**, so the Horde-user → Discord-user association must be
  supplied by configuration. See `PLAN.md` §3.3.1.

## Documentation split

- **`README.md`** — for people *installing and configuring* the plugin. Keep it task-oriented: install,
  configure, verify, troubleshoot. No internals.
- **`CLAUDE.md`** (this file) — for people and agents *working on* the plugin.
- **`.claude/PLAN.md`** — the design record: investigation, decisions and their rationale, phasing.
  Update it when a decision changes, and note the reversal rather than silently rewriting history.

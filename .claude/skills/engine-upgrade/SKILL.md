---
name: engine-upgrade
description: Re-verify and repair the plugin after the Unreal Engine / Horde tree it compiles against has moved. Use when the engine was upgraded or resynced, when HordeBinDir points somewhere new, or when the plugin fails to load with TypeLoadException or MissingMethodException.
user-invocable: true
allowed-tools:
  - Read
  - Edit
  - Glob
  - Grep
  - Bash
---

# After the engine moves

`INotificationSink` is internal to Horde with **no stability guarantee**. A stale plugin against a
newer server fails hard at plugin load — `TypeLoadException` or `MissingMethodException` — rather than
degrading. This is infrequent enough that the steps are easy to forget and high-stakes enough to be
worth following in order.

## 1. Confirm what changed

```bash
cat "<UE>/Engine/Build/Build.version"
```

Compare against the **Engine compatibility** table in `README.md`. Note the binaries' build date too —
on a source build `"Changelist"` is `0`, and every `HordeServer.*.dll` is stamped `1.0.0.0`, so
version plus build date is the only usable identity.

Make sure `Horde.local.props` still points at a directory that exists and has actually been rebuilt.
Pointing at a stale output is indistinguishable from an engine that did not change — and after a
resync the `bin` tree may not exist at all, in which case rebuild the server first:

```bash
dotnet build "<UE>/Engine/Source/Programs/Horde/HordeServer/HordeServer.csproj" -c Development
```

## 2. Diff the interface

This is the step that matters and the one that gets skipped.

```
<UE>/Engine/Source/Programs/Horde/Plugins/Build/HordeServer.Build/Notifications/INotificationSink.cs
```

Compare its members against `DiscordNotificationSink`. There were 17 — a count the test suite asserts,
so `dotnet test` tells you immediately whether this step has anything to find. Look for:

- **Added members** — the build breaks. Implement as a logging no-op first so the plugin loads again,
  then treat it as a new work item; do not leave it silently unimplemented without saying so.
- **Changed signatures** — parameters added or retyped. Fix the override, then check whether the new
  parameter carries information the message should now show.
- **Removed members** — delete ours; a stale override is a compile error.
- **Types that moved assemblies.** A type can relocate without the interface changing shape. The
  compiler names the missing assembly — add a `<Reference>` with `<Private>false</Private>`.
  Precedent: `ILogEventData` lives in `HordeServer.Compute`, not `HordeServer.Build`.

Keep members in interface order. That ordering exists precisely to make this diff tractable.

## 3. Rebuild and verify

Run the `verify-plugin` skill. Both configurations:

```bash
dotnet build -c Release && dotnet test -c Development
```

Zero warnings, all tests green. The suite covers the parts that used to be checked by eye: one DLL, no
engine assemblies in the output, and every `INotificationSink` member still implemented by the sink
rather than falling through to a default implementation.

## 4. Update the record

- `README.md` → **Engine compatibility**: new version and binary date.
- `PluginLoadTests.ExpectedNotificationSinkMemberCount`, if the interface gained or lost members.
  Change it only after working through step 2 — it is a tripwire, not a knob.
- `.claude/PLAN.md`: the file/line references in §1 are pinned to a specific engine and rot silently.
  Re-verify any you rely on; correct them with a dated note rather than a silent rewrite.
- If `INotificationSink` changed, that is worth recording — it is the concrete evidence for the
  stability risk in §6.

## If it still fails to load

Read the exception's type name: it names the assembly or member that moved. `MissingMethodException`
on a *constructor* usually means a Horde type gained a parameter — check `PluginServerConfig` and the
plugin startup constructor, which may only take `IConfiguration`, `IServerInfo`, and/or the server
config type.

Rebuilding against the new engine is the fix. There is no compatibility shim and none is wanted —
failing loudly at startup is the designed behaviour.

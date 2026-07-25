---
name: verify-plugin
description: Build the plugin and confirm a Horde server would load it, without MongoDB or Redis. Use after changing the plugin, after an engine change, or whenever asked to check the plugin still loads, builds clean, or is discovered.
user-invocable: true
allowed-tools:
  - Bash
  - Read
  - Glob
  - Grep
---

# Verify the plugin loads

The inner loop for this repo. Booting a real Horde server needs MongoDB and Redis; this does not.

## Run it

```bash
dotnet test -c Development
```

That is the whole check. The test project deploys the freshly built plugin into `$(HordeBinDir)`
itself before probing, so there is no separate copy step to forget — which used to be the most common
way to get a misleading result.

`$HORDE_BIN_DIR` is usually not set; the build resolves the path from `Horde.local.props` and bakes it
into the test assembly. If that file is missing, tests report **inconclusive** with instructions
rather than failing.

## What a pass looks like

All tests pass, zero build warnings. `GenerateDocumentationFile` is on for everything except test
projects, so a missing XML doc comment is a warning and therefore a failure.

## Reading a failure

Run the console front end for a readable breakdown of the same probe — it prints every step rather
than just the assertion that tripped:

```bash
dotnet run --project tools/PluginProbe -c Development
```

| Symptom | Cause |
|---|---|
| Tests **inconclusive** | `HordeBinDir` does not resolve, the directory does not exist, or the Horde server was never built there. Build `<UE>/Engine/Source/Programs/Horde/HordeServer/HordeServer.csproj -c Development`. |
| Inconclusive, "a running Horde server holds a lock" | The deploy copy failed. Stop the server and re-run. |
| `PluginDllMatchesTheServerScanPattern` | `AssemblyName` is no longer `HordeServer.Discord`. |
| `PluginIsDiscoveredAlongsideTheEnginePlugins` | The `[Plugin]` attribute was dropped, or the type is not public — the scan reads `GetExportedTypes()`. |
| `PluginCollectionAcceptsTheConfigTypes` | A generic constraint on the config types broke: `ServerConfigType` must derive from `PluginServerConfig`, `GlobalConfigType` must implement `IPluginConfig`. |
| `SinkImplementsEveryNotificationSinkMember` | Epic added a **default** interface method — the plugin still compiles but silently ignores that notification. Use the `engine-upgrade` skill. |
| `NotificationSinkHasNotGrownOrShrunk` | `INotificationSink` changed shape. Use the `engine-upgrade` skill, then update the constant *and* the README's engine compatibility table. |
| `NoEngineAssembliesLeakIntoThePluginOutput` / `DropIsASingleAssembly` | A `<Private>false</Private>` is missing from a `<Reference>`, or something took a package dependency. |
| `TypeLoadException` / `MissingMethodException` | Built against a different engine than the one being scanned. Use the `engine-upgrade` skill. |

`PluginCollection.Add` reporting the name as lowercase **`discord`** is correct, not a bug.

## Also check

Release builds too — the repo's bar is zero warnings in both:

```bash
dotnet build -c Release
```

## What this does not prove

Only that the server would *load* the plugin, bind its configuration, and that the sink type lines up
with the interface. It does not exercise `INotificationSink` — no callback is ever invoked. Do not
report "notifications work" on the strength of a green run; say what was actually verified.

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
dotnet build -c Development
cp HordeServer.Discord/bin/Development/net10.0/HordeServer.Discord.dll "$HORDE_BIN_DIR/"
dotnet run --project tools/PluginProbe -c Development
```

`$HORDE_BIN_DIR` may not be set — the build resolves it from `Horde.local.props` instead. Read that
file for the path rather than guessing, and pass it to the probe explicitly if the env var is empty.
The probe itself falls back to a build-time-baked path, so with no arguments it usually just works.

**The copy step is not optional and is the most common reason for a confusing result.** The probe
scans the *server* directory, not this repo's output. Skip the copy and you are testing the previous
build.

## What a pass looks like

Every line must be `[PASS]`, ending with `Phase 0 verification complete.` and exit code 0. The
plugin should appear as `found plugin 'Discord' (default-off)` alongside Epic's plugins, and
`PluginCollection.Add` should report the name as lowercase **`discord`** — that is correct, not a bug.

## Reading a failure

| Symptom | Cause |
|---|---|
| `HordeServer.Discord.dll was NOT found in the scan set` | The copy did not happen, or went to the wrong directory. The DLL must sit directly beside `HordeServer.dll`. |
| `No [Plugin("Discord")] type was discovered` | `AssemblyName` is no longer `HordeServer.Discord`, the `[Plugin]` attribute was dropped, or the type is not public — the scan reads `GetExportedTypes()`. |
| `PluginCollection.Add threw` | A generic constraint on the config types broke: `ServerConfigType` must derive from `PluginServerConfig`, `GlobalConfigType` must implement `IPluginConfig`. |
| `[WARN] <some Epic dll>: …` | Usually benign — an Epic plugin that fails to reflect. Only worrying if it names our assembly. |
| `TypeLoadException` / `MissingMethodException` | Built against a different engine than the one being scanned. Use the `engine-upgrade` skill. |

## Also check

The build must be **clean with zero warnings** — `GenerateDocumentationFile` is on, so a missing XML
doc comment is a warning and therefore a failure. And confirm no engine assemblies leaked into the
output; the drop is one file:

```bash
find HordeServer.Discord/bin -name "HordeServer.*.dll" -o -name "EpicGames.*.dll" | grep -v "HordeServer.Discord.dll"
```

Anything listed means a `<Private>false</Private>` is missing from a `<Reference>`.

## What this does not prove

Only that the server would *load* the plugin and bind its configuration. It does not exercise
`INotificationSink` at all — no callback is ever invoked. Do not report "notifications work" on the
strength of a green probe; say what was actually verified.

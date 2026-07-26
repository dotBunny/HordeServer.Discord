# HordeServer.Discord

Send [Horde](https://dev.epicgames.com/documentation/en-us/unreal-engine/horde) build notifications to
Discord.

Horde is Epic's build automation server. This plugin delivers its notifications — job and step
outcomes, build health issues, configuration failures and farm reports — to Discord channels.

It runs **alongside** Horde's built-in Slack support rather than replacing it, so you can adopt it
gradually or run both indefinitely.

> [!WARNING]
> **Early development.** Everything Horde broadcasts is delivered: job and step outcomes,
> configuration update failures, agent and device reports, and test health. Build health **issues** and
> the interactive triage that goes with them are not implemented yet, and neither are direct messages
> or @-mentions — a notification aimed at a person is posted to the channel naming them instead.
>
> Nothing has been verified against a real Discord server: no message this plugin produces has ever
> actually been delivered. Treat the formatting as unproven and point it at a test channel first.
>
> Installing it is safe regardless: with no bot token or no channel configured, it loads and does
> nothing, and it cannot disturb the Slack sink either way.

## AI Disclaimer

This project is an attempt to let Claude pretty much run the show when it comes to maintaining parity with both the Horde-side and the Discord-side of the notification sink. It is also serving as a test for how Claude can demonstratibly handle boiler-plate to cut down on human-brain time.

## Requirements

- A Horde server you can copy files into and restart
- .NET SDK 10.0 or later, to build the plugin
- A built Horde server tree to compile against (see below)
- A Discord bot with access to the channels you want to post in

There are no published binaries yet, so installation means building from source.

### Engine compatibility

This plugin compiles against internal Horde interfaces that carry no compatibility guarantee, so the
engine it was built against matters. The current code is built and verified against:

| | |
|---|---|
| Unreal Engine | **5.8.0** (`BranchName` UE5) |
| Horde server binaries | built 2026-07-25 |

Check your own with `Engine/Build/Build.version`. A source build reports `"Changelist": 0`, so there is
usually no changelist to compare against — the version and the build date are the practical identity.
The `HordeServer.*` assemblies are all stamped `1.0.0.0` and cannot be used to tell releases apart.

Other 5.8 engines will very likely work; the plugin uses a small, stable part of the interface. Nothing
is lost by trying, because a mismatch fails loudly at server startup rather than misbehaving quietly —
see [Troubleshooting](#troubleshooting).

## Installation

### 1. Point the build at your Horde server

```
copy Horde.local.props.template Horde.local.props
```

Edit `Horde.local.props` and set `HordeBinDir` to your **built** Horde server directory — the one
containing `HordeServer.dll` and `HordeServer.Build.dll`. For an Unreal Engine source build that is
usually:

```
<UnrealEngine>\Engine\Source\Programs\Horde\HordeServer\bin\Development\net10.0
```

If you have not built Horde yet:

```
dotnet build <UnrealEngine>\Engine\Source\Programs\Horde\Horde.sln -c Development
```

`Horde.local.props` is git-ignored, so your local path is never committed. If you would rather not
have the file, set the `HORDE_BIN_DIR` environment variable instead. Getting either wrong produces a
clear error telling you what to fix.

### 2. Build

```
dotnet build -c Development
```

This produces a single file:

```
HordeServer.Discord\bin\Development\net10.0\HordeServer.Discord.dll
```

The plugin has no dependencies of its own — one file is the entire install.

### 3. Install

Copy that DLL next to your Horde server binaries, in the same directory as `HordeServer.dll`.

Horde finds plugins by scanning that directory, so nothing else needs to change — no configuration
files to register, no changes to Horde itself.

### 4. Enable and configure

Add a `Discord` section to your server's `server.json`, then restart the server:

```jsonc
{
  "Horde": {
    "Plugins": {
      "Discord": {
        "Enabled": true,
        "BotToken": "your-bot-token",
        "ApplicationId": "your-application-id",
        "GuildId": "your-server-id",
        "JobNotificationChannel": "123456789012345678"
      }
    }
  }
}
```

The plugin is **disabled by default** and does nothing until `Enabled` is `true`.

## What gets sent, and where

| Notification | Channel it uses |
|---|---|
| Job completed, step completed or aborted, label completed | The job's or stream's `notificationChannel`, honouring its outcome filter |
| Jobs waiting to be scheduled | `jobNotificationChannel` |
| Configuration update failed, and its recovery | `configNotificationChannel` |
| Stream configuration update failed | `updateStreamsNotificationChannel` |
| Agents stuck conforming or upgrading; session conflicts | `agentNotificationChannel` |
| Device pool health and device problems | The channel on the report (a workflow's `reportChannel`) |
| Device checkout notices | `deviceNotificationChannel` |
| Test health degraded, and its recovery | The workflow's `reportChannel` |
| Build health issues and triage | *Not implemented — Slack only for now* |

Two behaviours are worth knowing before you wonder whether something is broken:

- **Configuration failures are announced once, not every poll.** Horde re-reads its configuration on a
  timer and reports the same failure each time. The plugin posts a failure when it first appears or
  when it changes, and posts "configuration update succeeded" only as the recovery from a failure it
  announced — never as routine chatter. The same applies to test health.
- **Notifications aimed at a person go to a channel, addressed to them by name.** Slack sends some of
  these as direct messages. Discord cannot look a user up by email, so until the user map lands the
  message is posted where everyone can see it rather than not sent at all.

## Configuration

There are two halves. **Channel routing** lives in a hot-reloadable `*.discord.json` config file;
**credentials and infrastructure** live in `server.json` and need a restart.

### Channel routing

Horde already decides which channel every notification belongs in — per workflow, per stream, per
template — and it stores that as a **Slack channel id** like `C0832ESJUR5`. Rather than making you
configure all of that a second time, this plugin translates the last hop: you say where each of those
channels lands in Discord.

```jsonc
{
  "guilds": { "studio": "112233445566778899" },
  "channels": {
    "C0832ESJUR5": { "label": "horde-triage", "guild": "studio", "channel": "998877665544332211" },
    "C085J3A6FHN": { "label": "horde-builds", "channel": "112233445566778899" }
  },
  "fallbackChannel": "555566667777888899"
}
```

- **Keys** are whatever Horde already has for that channel. That's a Slack channel id for workflow
  `reportChannel` / `triageChannel`, the issue and device reports, and per-stream and per-template job
  channels. The two exceptions are `jobNotificationChannel` and `updateStreamsNotificationChannel`,
  where Horde stores a bare channel **name** — key those on the name, without a `#`.
- **`label`** is for humans. Nothing routes on it, but both sides of a mapping are opaque ids, so
  without it the file is unreadable and so are the logs.
- **`guild`** is optional. With exactly one guild configured, that one is the default. It isn't needed
  to post at all — it exists for direct messages, interactions and startup validation.
- **`fallbackChannel`** catches anything unmapped, and the message says which Horde channel it was
  meant for. Without one, unmapped channels are logged once and dropped.

Add a workflow, or re-point one, and Discord follows automatically — as long as its channel is in the
map. **At startup and on every config reload the plugin lists every Horde channel with no mapping**, so
you don't have to discover a gap by noticing a notification that never arrived.

### Server settings

All under `Horde:Plugins:Discord` in `server.json`. Changing any of them requires a server restart.

| Setting | Type | Description |
|---|---|---|
| `Enabled` | bool | Whether to load the plugin at all. Defaults to `false`. |
| `BotToken` | string | Bot token used to authenticate with Discord. Without it the plugin loads but discards notifications. |
| `ApplicationId` | string | Your Discord application (client) id. Needed for slash commands and interactive components. |
| `GuildId` | string | The Discord server the bot operates in. Only used for member lookup and command registration — posting uses channel ids directly. |
| `EnableInteractions` | bool | Whether to connect to Discord's gateway for buttons and modals. Posting works without it. Defaults to `true`. |
| `JobNotificationChannel` | string | **Override.** Discord channel for job and step outcomes, `;`-separated, bypassing the routing map above. Normally unset. |
| `AgentNotificationChannel` | string | Override for agent and session conflict reports. |
| `ConfigNotificationChannel` | string | Override for configuration update failures. |
| `UpdateStreamsNotificationChannel` | string | Override for stream configuration update failures. |
| `DeviceNotificationChannel` | string | Override for device service notices. Device *reports* carry their own channel and are routed through the map above. |
| `ErrorPrefix` | string | Emoji prefixed to error messages. Defaults to `:red_circle:`. |
| `WarningPrefix` | string | Emoji prefixed to warning messages. Defaults to `:warning:`. |

### Finding channel ids

Discord channels are identified by **numeric id**, not by name — there is no `#channel` syntax. In
Discord, enable **Settings → Advanced → Developer Mode**, then right-click a channel and choose **Copy
Channel ID**.

Those ids go on the *right* of a mapping. If you paste a Slack channel id or a `#channel-name` where a
Discord id belongs, the plugin says so by name at startup rather than silently posting nowhere.

### Keeping the bot token out of `server.json`

The token is a credential. Rather than writing it into `server.json`, supply it through Horde's
Secrets plugin or an environment variable:

```
Horde__Plugins__Discord__BotToken=your-bot-token
```

### Creating the bot

1. Create an application at <https://discord.com/developers/applications>.
2. Under **Bot**, create a bot and copy its token.
3. Invite it to your Discord server with permission to view and send messages in the target channels.

No privileged intents are required.

## Verifying the installation

After restarting, the Horde server log should contain two lines:

```
Loading …\HordeServer.Discord.dll
Discord notification sink registered (guild …, interactions enabled)
```

If a bot token is not configured the second line instead reads *"no bot token is configured;
notifications will be discarded"* — which still confirms the plugin installed correctly, so this is a
useful way to verify the install before creating a bot.

Horde also logs `Added plugin 'Discord'`, but only at **debug** level — you will not see it unless you
have lowered the server's log level.

## Troubleshooting

**The server fails to start with `Unable to find plugin(s) enabled in config file: Discord`**
You enabled the plugin in `server.json` but Horde cannot find the assembly. `HordeServer.Discord.dll`
must sit directly beside `HordeServer.dll`, not in a subdirectory.

**No `Loading …HordeServer.Discord.dll` line in the log**
The assembly is missing from the application directory, or it was renamed. Horde only scans for files
named `HordeServer.*.dll` — the filename matters.

**The server throws `TypeLoadException` or `MissingMethodException` on startup**
The plugin was built against a different version of Horde than the one running. Rebuild it against
your current server (see [Upgrading](#upgrading)).

**Notifications are not appearing**
Confirm the bot is a member of the server, can see the target channel, and has permission to send
messages there. Check that channel ids are numeric ids rather than names.

## Upgrading

**Rebuild this plugin whenever you upgrade Horde.** It compiles against internal Horde interfaces that
carry no compatibility guarantee, and a stale plugin fails at server startup rather than degrading
gracefully. Rebuilding is a good step to add to your Horde deployment process.

The version it is currently built against is recorded under
[Engine compatibility](#engine-compatibility). To confirm a rebuild took without booting the full
server — which needs MongoDB and Redis — run the tests:

```
dotnet test -c Development
```

They deploy the plugin into your server directory, replicate Horde's plugin discovery against it, and
report whether the server would load it. For a readable breakdown when something fails, the same check
is available as a console tool:

```
dotnet run --project tools/PluginProbe -c Development
```

See [CLAUDE.md](CLAUDE.md) for details.

## Contributing

Contributions are welcome. See [CLAUDE.md](CLAUDE.md) for repository conventions, architecture notes
and build details, and [.claude/PLAN.md](.claude/PLAN.md) for the design and roadmap.

One rule matters above the rest: **this repository must never contain Epic-owned code**. Build against
a local engine; never commit engine source or compiled engine assemblies.

## License

MIT — see [LICENSE](LICENSE).

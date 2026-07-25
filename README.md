# HordeServer.Discord

Send [Horde](https://dev.epicgames.com/documentation/en-us/unreal-engine/horde) build notifications to
Discord.

Horde is Epic's build automation server. This plugin delivers its notifications — job and step
outcomes, build health issues, configuration failures and farm reports — to Discord channels.

It runs **alongside** Horde's built-in Slack support rather than replacing it, so you can adopt it
gradually or run both indefinitely.

> [!WARNING]
> **Early development.** The plugin installs and is wired into Horde's notification pipeline, but does
> not send messages yet. It is safe to install — it simply does nothing. Follow the repository for
> progress.

## AI Disclaimer

This project is an attempt to let Claude pretty much run the show when it comes to maintaining parity with both the Horde-side and the Discord-side of the notification sink. It is also serving as a test for how Claude can demonstratibly handle boiler-plate to cut down on human-brain time.

## Requirements

- A Horde server you can copy files into and restart
- .NET SDK 10.0 or later, to build the plugin
- A built Horde server tree to compile against (see below)
- A Discord bot with access to the channels you want to post in

There are no published binaries yet, so installation means building from source.

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

## Configuration

All settings live under `Horde:Plugins:Discord` in `server.json`. Changing any of them requires a
server restart.

| Setting | Type | Description |
|---|---|---|
| `Enabled` | bool | Whether to load the plugin at all. Defaults to `false`. |
| `BotToken` | string | Bot token used to authenticate with Discord. Without it the plugin loads but discards notifications. |
| `ApplicationId` | string | Your Discord application (client) id. Needed for slash commands and interactive components. |
| `GuildId` | string | The Discord server the bot operates in. Only used for member lookup and command registration — posting uses channel ids directly. |
| `EnableInteractions` | bool | Whether to connect to Discord's gateway for buttons and modals. Posting works without it. Defaults to `true`. |
| `JobNotificationChannel` | string | Channel for job-related notifications. Multiple channels may be separated by `;`. |
| `AgentNotificationChannel` | string | Channel for agent-related notifications. |
| `ConfigNotificationChannel` | string | Channel for configuration update failures. |
| `UpdateStreamsNotificationChannel` | string | Channel for stream update failures. |
| `ErrorPrefix` | string | Emoji prefixed to error messages. Defaults to `:red_circle:`. |
| `WarningPrefix` | string | Emoji prefixed to warning messages. Defaults to `:warning:`. |

### Finding channel ids

Discord channels are identified by **numeric id**, not by name — there is no `#channel` syntax. In
Discord, enable **Settings → Advanced → Developer Mode**, then right-click a channel and choose **Copy
Channel ID**.

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

## Contributing

Contributions are welcome. See [CLAUDE.md](CLAUDE.md) for repository conventions, architecture notes
and build details, and [.claude/PLAN.md](.claude/PLAN.md) for the design and roadmap.

One rule matters above the rest: **this repository must never contain Epic-owned code**. Build against
a local engine; never commit engine source or compiled engine assemblies.

## License

MIT — see [LICENSE](LICENSE).

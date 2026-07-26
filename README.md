# HordeServer.Discord

Send [Horde](https://dev.epicgames.com/documentation/en-us/unreal-engine/horde) build notifications to
Discord.

Horde is Epic's build automation server. This plugin delivers its notifications — job and step
outcomes, build health issues, configuration failures and farm reports — to Discord channels.

It runs **alongside** Horde's built-in Slack support rather than replacing it, so you can adopt it
gradually or run both indefinitely.

> [!WARNING]
> **Early development.** All of Horde's notifications are delivered — job and step outcomes,
> configuration failures, agent and device reports, test health, and build health issues with
> interactive triage. Every message type has been sent to a real Discord server and looked at, and the
> triage buttons, the Mark Fixed dialog and the issue threads have all been driven end to end by hand.
>
> **What has not been exercised is a running Horde server.** Everything so far has been verified by
> driving the plugin directly with stand-in data. The code that writes back to Horde's issue database
> when somebody presses a button has never executed against a real one. Point this at a test channel
> and a test stream before you trust it on a busy farm.
>
> Installing it is safe regardless: with no bot token or no channel configured, it loads and does
> nothing, and it cannot disturb the Slack sink either way.

## AI Disclaimer

This project is an attempt to let Claude pretty much run the show when it comes to maintaining parity
with both the Horde-side and the Discord-side of the notification sink. It is also serving as a test
for how Claude can demonstrably handle boiler-plate to cut down on human-brain time.

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
| Unreal Engine | **5.8.0** (`release` UE5) |
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

Running Horde in Docker? See [Installing into a container](#installing-into-a-container) — you can
mount the DLL in rather than rebuilding the image.

### 4. Create the Discord bot

1. Create an application at <https://discord.com/developers/applications>. Note its **Application ID**
   from the General Information page.
2. Under **Bot**, click **Reset Token** and copy the token. Treat it like a password.
3. Invite the bot to your Discord server. Substitute your application id:

   ```
   https://discord.com/oauth2/authorize?client_id=YOUR_APPLICATION_ID&scope=bot+applications.commands&permissions=84992
   ```

   `84992` is **View Channel + Send Messages + Embed Links + Read Message History** — everything
   needed to post notifications. For issue triage threads use `permissions=309237730304`, which adds
   **Create Public Threads** and **Send Messages in Threads**; the second is easy to forget and is
   what lets the plugin post updates *into* a thread it created.

**No privileged intents are required** — leave Message Content, Server Members and Presence off. The
plugin requests zero gateway intents, because the only inbound events it wants are its own button
presses, and Discord delivers those regardless. That also means the application never needs Discord's
verification process.

> [!IMPORTANT]
> **Embed Links is not optional.** Every notification this plugin sends is an embed, so without that
> permission Discord rejects all of them. Check it per-channel too: a channel-level override beats a
> role-level grant, and that is the single most common reason nothing arrives.

### 5. Enable the plugin

Add a `Discord` section to your server's `server.json`, then restart:

```jsonc
{
  "Horde": {
    "Plugins": {
      "Discord": {
        "Enabled": true,
        "BotToken": "your-bot-token",
        "ApplicationId": "your-application-id",
        "GuildId": "your-guild-id" // Optional; see the server settings table
      }
    }
  }
}
```

The plugin is **disabled by default** and does nothing until `Enabled` is `true`. At this point it
loads and authenticates but has nowhere to post — that is the next step.

### 6. Tell it where to post

Channel routing lives in Horde's **global** config, not `server.json`, so it reloads without a restart.
Add a `discord` section under `plugins` in `globals.json`:

```jsonc
{
  "plugins": {
    "discord": {
      "guilds": { "studio": "100000000000000001" },
      "channels": {
        "horde-builds": { "label": "build announcements", "channel": "200000000000000001" }
      },
      "fallbackChannel": "200000000000000009"
    }
  }
}
```

Every snowflake in this README is a placeholder, grouped so that a mistake is visible at a glance:
guilds start `1000…`, channels `2000…`, users `3000…`, roles `4000…`. Real ones look nothing like this
— replace all of them. Nothing validates a snowflake beyond its shape, so a guild id pasted where a
channel belongs is accepted and only fails later, as a channel Discord has never heard of.

> [!NOTE]
> **Step 5 has to have worked first.** Horde only knows how to read a `plugins` entry for a plugin it
> actually loaded; a section belonging to anything else is skipped without a word. So if the
> `server.json` block above is missing, misplaced or never took effect, this block is silently ignored
> too — one mistake, two symptoms. Casing does not matter in either file: Horde normalises plugin
> names, so `discord` and `Discord` are the same key.

Restart once more if you like, but you do not have to: global config is picked up on Horde's next
config poll. **On every reload the plugin logs each Horde channel it has no mapping for**, which is
the quickest way to discover what else needs an entry.

See [Channel routing](#channel-routing) below for what the keys mean and where to find them.

## What gets sent, and where

| Notification | Channel it uses |
|---|---|
| A job you subscribed to completed; a step completed or was aborted; a label completed | **A direct message** to each subscriber |
| A job completed (the stream-wide announcement) | The job's or stream's `notificationChannel`, honouring its outcome filter |
| A step timed out | `jobNotificationChannel` |
| Jobs waiting to be scheduled | `jobNotificationChannel` |
| Configuration update failed, and its recovery | `configNotificationChannel` |
| Stream configuration update failed | `updateStreamsNotificationChannel`, **and a direct message** to the commit's author |
| Agents stuck conforming or upgrading; session conflicts | `agentNotificationChannel` |
| Device pool health and device problems | The channel on the report (a workflow's `reportChannel`) |
| Device checkout notices | **A direct message**, falling back to `deviceNotificationChannel` |
| Test health degraded, and its recovery | The workflow's `reportChannel` |
| A build health issue changed | The workflow's `triageChannel`, **and a direct message** to whoever owns it |
| The periodic issue digest | The channel Horde puts on the report |

Two behaviours are worth knowing before you wonder whether something is broken:

- **Configuration failures are announced once, not every poll.** Horde re-reads its configuration on a
  timer and reports the same failure each time. The plugin posts a failure when it first appears or
  when it changes, and posts "configuration update succeeded" only as the recovery from a failure it
  announced — never as routine chatter. The same applies to test health.
- **A person the plugin cannot reach still gets their notification.** Direct messages need the person
  in the user map below, need the bot to share a server with them, and need them to accept messages
  from server members. If any of that is missing the notification goes to the fallback channel
  instead, mentioning them if they are mapped and naming them if not. Nothing is dropped for want of
  a mapping, so the map is safe to fill in gradually.

## Configuration

Settings are split across **two files**, and which one a setting lives in decides how you change it:

| | `server.json` | `globals.json` |
|---|---|---|
| **Holds** | Credentials and infrastructure | Routing — channels, people, roles, guilds |
| **Section** | `Horde` → `Plugins` → `Discord` | `plugins` → `discord` |
| **To change** | Edit and **restart the server** | Edit; picked up on the next config poll |
| **Why** | A bot token should not be in a file the dashboard can serve | Onboarding someone or re-pointing a stream should not need a restart |

The two never overlap except for the channel **overrides** in `server.json`, which exist for a first
run before any routing is configured and are normally left unset.

<details>
<summary>A complete working example of both files</summary>

`server.json` — note that `Plugins` sits **inside** `Horde`, not beside it. Horde reads the section as
`Horde:Plugins:<name>`, and a `Plugins` block at the top level of the file is simply never looked at.
The capitalisation shown follows .NET's configuration convention but is not required; that binding is
case-insensitive:

```jsonc
{
  "Horde": {
    "Plugins": {
      "Discord": {
        "Enabled": true,
        "BotToken": "MTAw…",
        "ApplicationId": "500000000000000001",
        "GuildId": "100000000000000001"
      }
    }
  }
}
```

`globals.json` — everything else:

```jsonc
{
  "plugins": {
    "discord": {
      "guilds": {
        "studio": "100000000000000001"
      },

      "channels": {
        // Keyed by whatever Horde already stores for that channel.
        "C0832ESJUR5": { "label": "horde-triage", "channel": "200000000000000001" },
        "horde-builds": { "label": "build announcements", "channel": "200000000000000002" }
      },

      // Anything unmapped lands here, labelled with the Horde channel it was meant for.
      "fallbackChannel": "200000000000000009",

      "userMap": {
        "ada@example.com": "300000000000000001"
      },

      "roles": {
        "S0123456789": { "label": "build-triage", "role": "400000000000000001" }
      }
    }
  }
}
```

To keep it out of `globals.json`, put the same `plugins` block in its own file and include it — Horde
merges includes into the global config, and this is how Epic's own plugin config is usually organised:

```jsonc
{
  // A path relative to the including file, or a depot path if your config lives in Perforce —
  // "//Horde/plugins/discord.json" — matching whatever ConfigPath in server.json points at.
  "include": [ { "path": "discord.json" } ]
}
```

The included file needs the full `plugins` → `discord` nesting, not just the inner settings — it is
merged as a global config document, not spliced in at the point of the `include`.

</details>

### Channel routing

Horde already decides which channel every notification belongs in — per workflow, per stream, per
template — and it stores that as a **Slack channel id** like `C0832ESJUR5`. Rather than making you
configure all of that a second time, this plugin translates the last hop: you say where each of those
channels lands in Discord.

All of the following goes inside `plugins.discord` in `globals.json`:

```jsonc
{
  "guilds": { "studio": "100000000000000001" },
  "defaultGuild": "studio",
  "channels": {
    "C0832ESJUR5": { "label": "horde-triage", "guild": "studio", "channel": "200000000000000001" },
    "C085J3A6FHN": { "label": "horde-builds", "channel": "200000000000000002" }
  },
  "fallbackChannel": "200000000000000009",
  "fallbackGuild": "studio"
}
```

| Key | Description |
|---|---|
| `guilds` | Short name → guild id, so an id appears once. One bot token serves any number of guilds. |
| `defaultGuild` | Name from `guilds` used by anything that does not say. Inferred only when exactly one guild is configured — **required as soon as there are two**. |
| `channels` | The translation table. Keys are Horde's channel, values say where it lands. |
| `fallbackChannel` | Catch-all for unmapped channels. Omit it and unmapped channels are logged once and dropped. |
| `fallbackGuild` | Guild the fallback is in. Defaults to `defaultGuild`. |

- **Keys** are whatever Horde already has for that channel. That's a Slack channel id for workflow
  `reportChannel` / `triageChannel`, the issue and device reports, and per-stream and per-template job
  channels. The two exceptions are `jobNotificationChannel` and `updateStreamsNotificationChannel`,
  where Horde stores a bare channel **name** — key those on the name, without a `#`.
- **`label`** is for humans. Nothing routes on it, but both sides of a mapping are opaque ids, so
  without it the file is unreadable and so are the logs.
- **`guild`** is optional, but only genuinely optional in a **single-guild** install, where that one
  guild is the default. Posting never needs it — `POST /channels/{id}/messages` takes only the channel,
  and channel ids are globally unique — but three things do: **issue triage threads**, the **channel
  deep links** the dashboard uses, and deciding whether a **role mention** is even valid where it is
  being posted.

  > [!IMPORTANT]
  > **List two guilds and you must set `defaultGuild`.** The default is inferred *only* when exactly
  > one guild is configured — with two or more and no `defaultGuild`, every channel that does not name
  > a `guild` of its own resolves without one. Nothing errors and notifications still post, but those
  > channels quietly get no triage threads (the log says *"no mapped triage channel for issue N, so no
  > thread was started"*) and no deep links. Set `defaultGuild`, or give every channel an explicit
  > `guild`.
- **`fallbackChannel`** catches anything unmapped, and the message says which Horde channel it was
  meant for. Without one, unmapped channels are logged once and dropped.

Add a workflow, or re-point one, and Discord follows automatically — as long as its channel is in the
map. **At startup and on every config reload the plugin lists every Horde channel with no mapping**, so
you don't have to discover a gap by noticing a notification that never arrived.

### People

Discord has no way to look someone up by email address, and an email address is all Horde knows about
a person that Discord might share. So the association has to be written down — again inside
`plugins.discord`:

```jsonc
{
  "userMap": {
    "ada@example.com": "300000000000000001"
  },
  "roles": {
    "S0123456789": { "label": "build-triage", "role": "400000000000000001" },
    "S9876543210": { "label": "render-triage", "guild": "studio-b", "role": "400000000000000002" }
  }
}
```

- **`userMap`** keys are the email address on the person's Horde account, and values are their Discord
  user id. Right-click someone in Discord with Developer Mode on and choose **Copy User ID**.
  Somebody who is not listed still gets their notifications — they are named in plain text in a
  channel rather than mentioned or messaged directly. This lives in the hot-reloadable config on
  purpose: adding a new hire should not need a server restart.
- **`roles`** maps the alias Horde pings — a workflow's `triageAlias`, `escalateAlias` or
  `triageTypeAliases` — to the Discord role that stands in for it. An issue that
  **nobody has been assigned** pings its workflow's triage role; once somebody takes it the pings stop,
  which is what keeps a triage channel from being muted. An alias with no role behind it costs a ping,
  never a notification, and the startup report names the gaps.

  `guild` is optional and only matters with more than one guild: **a role id means nothing outside its
  own guild**, and mentioning one from elsewhere renders as raw text that pings nobody. Naming the
  guild lets the mention be skipped instead. Leave it out in a single-guild install.

  Horde does not require these aliases to be user *groups*. Its Slack sink treats an alias beginning
  with `S` as a user group and **anything else as an individual Slack user**, so a workflow may well
  name a person. There is no per-person equivalent of `roles` — map that alias to a role containing
  just them, or accept the plain-text fallback. Keys are matched however Horde stores them, so a studio
  that writes readable aliases (`build-triage`) rather than Slack ids can use those as keys directly.

### Multiple guilds

One bot token serves any number of guilds it has been invited to. Name each in `guilds`, then point
individual channels and roles at them; `defaultGuild` covers everything that does not say. With exactly
one guild configured, that one is the default and nothing needs to name it.

**Adding a second guild is the moment `defaultGuild` stops being optional.** Up to that point it was
inferred; from that point every channel and role either names a guild or has none, and the ones with
none lose triage threads, deep links and role mentions without any error being raised. If you are
going from one guild to two, set `defaultGuild` in the same edit.

More than one *token* would be a larger change, because Discord's global rate limit is per token.

### Global settings reference

All under `plugins.discord` in `globals.json`. Every one of these reloads without a restart.

| Key | Type | Description |
|---|---|---|
| `guilds` | map | Short name → guild snowflake, so an id is written once. |
| `defaultGuild` | string | Name from `guilds` used by anything that does not name one. Inferred only when exactly one guild is configured; **set it explicitly once there are two**, or unnamed channels lose threads and deep links. |
| `channels` | map | Horde channel → `{ label?, guild?, channel }`. The routing table. |
| `fallbackChannel` | string | Catch-all for unmapped Horde channels. Omit and they are logged once and dropped. |
| `fallbackGuild` | string | Guild the fallback channel is in. Defaults to `defaultGuild`. |
| `userMap` | map | Horde account email → Discord user snowflake. |
| `roles` | map | Horde user-group handle → `{ label?, guild?, role }`. |

### Server settings

All under `Horde:Plugins:Discord` in `server.json`. Changing any of them requires a server restart.

| Setting | Type | Description |
|---|---|---|
| `Enabled` | bool | Whether to load the plugin at all. Defaults to `false`. |
| `BotToken` | string | Bot token used to authenticate with Discord. Without it the plugin loads but discards notifications. |
| `ApplicationId` | string | Your Discord application (client) id. Needed for slash commands and interactive components. |
| `GuildId` | string | The Discord server the bot operates in. Informational — nothing in the posting path reads it, because channels and roles carry their own guild. Set it anyway; it appears in the startup line and is what future guild-scoped features will use. |
| `EnableInteractions` | bool | Whether to connect to Discord's gateway for buttons and modals. Posting works without it. Defaults to `true`. |
| `EnableTriageThreads` | bool? | Whether issue triage keeps one message per issue in a thread, rewritten as the issue changes, instead of posting a new message per change. Three states: unset is *automatic*. **Leave it unset unless you mean it** — see below. |
| `EnableDeepLinks` | bool? | Whether the dashboard's "message these people" buttons should open Discord. Three states: unset is *automatic*. Leave it unset — see below. |
| `JobNotificationChannel` | string | **Override.** Discord channel for job and step outcomes, `;`-separated, bypassing the routing map above. Normally unset. |
| `AgentNotificationChannel` | string | Override for agent and session conflict reports. |
| `ConfigNotificationChannel` | string | Override for configuration update failures. |
| `UpdateStreamsNotificationChannel` | string | Override for stream configuration update failures. |
| `DeviceNotificationChannel` | string | Override for device service notices. Device *reports* carry their own channel and are routed through the map above. |
| `ErrorPrefix` | string | Emoji prefixed to error messages. Defaults to `🔴 `. |
| `WarningPrefix` | string | Emoji prefixed to warning messages. Defaults to `⚠️ `. |

Both prefixes must be a **literal emoji character**, or a custom guild emoji written `<:name:id>`. A
`:red_circle:` shortcode is not expanded by Discord for anything a bot posts, and will appear in the
message as the text you typed.

### Issue triage threads

With `EnableTriageThreads` on, each issue gets **one message in the triage channel with a thread hanging
off it**. The message is rewritten as the issue changes and the thread records how it got there, rather
than the channel collecting a new message per change.

Horde stores where that thread is, in the issue's own `WorkflowThreadUrl` field — and **there is one of
those per issue, which Slack's notification sink also writes**. If both sinks claimed it, a studio's
Slack triage links would be quietly replaced by Discord ones.

So leaving it unset means *automatic*: threads are used only when the Build plugin has no `SlackToken`,
which is exactly when nothing else owns the field. Set it to `true` to take the field even alongside
Slack, or `false` to stay out of it. With threads off, issue notifications post a new message per state
change, and repeats that changed nothing are suppressed.

### Dashboard deep links

Horde's dashboard has buttons that open a chat conversation with a set of people, or a channel. It
asks every notification plugin for a link and uses **the first answer it gets**, in an order nothing
controls — so a Discord plugin that always answered could quietly take those buttons away from Slack.

`EnableDeepLinks` therefore defaults to *automatic*: Discord answers only when the Build plugin has no
`SlackToken` configured, which is exactly when nothing else would. Set it to `true` to point the
dashboard at Discord even alongside Slack, or `false` to stay out of it.

Note that a "message these people" link only works for a single person. Discord's group conversations
are not something a bot can create.

### Finding the ids

A mapping has a Horde channel on the left and a Discord channel on the right, and they are found in
completely different places.

**The Discord id (right side).** Discord channels are identified by **numeric id**, never by name —
there is no `#channel` syntax. Enable **Settings → Advanced → Developer Mode**, then right-click a
channel and choose **Copy Channel ID**. Same for users and roles: **Copy User ID**, **Copy Role ID**.

**The Horde key (left side).** This is whatever your Horde config already stores, which you find by
searching your own `globals.json` and stream files:

| Horde setting | Where it lives | What it holds |
|---|---|---|
| `triageChannel` | A workflow, or a stream | Slack channel id (`C0832ESJUR5`) |
| `reportChannel` | A workflow | Slack channel id |
| `notificationChannel` | A stream or a template | Slack channel id |
| `jobNotificationChannel` | `server.json`, Build plugin | Channel **name**, no `#` |
| `updateStreamsNotificationChannel` | `server.json`, Build plugin | Channel **name**, no `#` |
| `triageAlias`, `escalateAlias` | A workflow | Slack user-group id (`S0123456789`) — or a Slack **user** id, see below |

The two `server.json` ones are the odd pair: Horde stores a bare name there rather than an id, so key
those on the name.

**You do not have to find them all up front.** Start with a `fallbackChannel` and let the plugin tell
you: at startup and on every config reload it lists every Horde channel that has no mapping, by name,
and anything unmapped lands in the fallback labelled with the channel it was meant for.

If you paste a Slack channel id or a `#channel-name` where a Discord id belongs, the plugin names the
offending entry at startup rather than silently posting nowhere.

### Installing into a container

If Horde runs from a container image you do not control, you do not need to rebuild that image to add
the plugin. **Bind-mount the DLL into the server's application directory** — the plugin is one file
with no dependencies, so the mount *is* the install.

The scan directory is **`/app`**, the image's working directory, alongside `HordeServer.dll` and the
plugins Epic ships:

```
/app/HordeServer.dll              ← the server
/app/HordeServer.Build.dll        ← Epic's plugins
/app/HordeServer.Compute.dll
…
/app/HordeServer.Discord.dll      ← yours, mounted in
```

Add one line to the `horde-server` service in your `docker-compose.yml`:

```yaml
services:
  horde-server:
    image: ghcr.io/epicgames/horde-server:latest
    volumes:
      - ./data:/app/Data
      - ./server.json:/app/Data/server.json
      # The plugin. One file, mounted read-only beside the server's own assemblies.
      - ./HordeServer.Discord.dll:/app/HordeServer.Discord.dll:ro
```

Keep a built copy of the DLL beside the compose file, or mount it straight out of the build output so
there is no copy step at all:

```yaml
      - ../../HordeServer.Discord/HordeServer.Discord/bin/Development/net10.0/HordeServer.Discord.dll:/app/HordeServer.Discord.dll:ro
```

Confirm the mount landed where the scan will find it:

```
docker compose exec horde-server ls -l /app/HordeServer.Discord.dll
```

> [!IMPORTANT]
> **After rebuilding the plugin, recreate the container — do not just restart it.**
>
> ```
> dotnet build -c Development
> docker compose up -d --force-recreate horde-server
> ```
>
> A single-file bind mount is bound to the file's *inode*, not its path. `dotnet build` writes a new
> file, so the container keeps serving the old one and `docker compose restart` will not change that —
> the container is the same container, with the same mount. The symptom is a plugin that stubbornly
> behaves like the previous build, which is a genuinely confusing hour. Mounting a *directory* does not
> have this problem, but there is nowhere useful to mount one: Horde only scans its own app directory.

Two things to check in a containerised setup that do not come up otherwise:

- **`ConfigPath` may not be a local file.** It often points into Perforce (`//Horde/globals.json`), in
  which case the `plugins.discord` section belongs in *that* file, not in anything under `./data`.
  Check what your `server.json` sets it to before going looking for a file that is not there.
- **`server.json` is usually mounted too**, so add the `Horde.Plugins.Discord` block to the file on
  the host and recreate the container. Setting it through environment variables works as well and
  keeps the token out of the file — see below.

### Keeping the bot token out of `server.json`

The token is a credential. Rather than writing it into `server.json`, supply it through Horde's
Secrets plugin or an environment variable — Horde reads standard ASP.NET configuration, so every
setting in the table above has an environment-variable form with `__` for each level:

```
Horde__Plugins__Discord__BotToken=your-bot-token
```

In Compose that is how the rest of the connection settings are already passed, so it fits the pattern:

```yaml
services:
  horde-server:
    environment:
      Horde__Plugins__Discord__Enabled: "true"
      Horde__Plugins__Discord__BotToken: ${DISCORD_BOT_TOKEN}
      Horde__Plugins__Discord__ApplicationId: "500000000000000001"
      Horde__Plugins__Discord__GuildId: "100000000000000001"
```

With `${DISCORD_BOT_TOKEN}` taken from a `.env` file beside the compose file, the token stays out of
both `server.json` and version control.

That last part is the real argument for it. A containerised Horde keeps its `server.json` beside a
`docker-compose.yml`, and that pair usually lives in an infrastructure repository — so the file holding
the bot token gets committed as a matter of course. **Discord scans public repositories and revokes
tokens it finds**, which surfaces as the bot working for days and then 401-ing (`40001`) with nothing
having changed. If a token has ever been committed, rotate it on the application's Bot page rather
than deleting the line; the history still has it.

## Verifying the installation

After restarting — `docker compose logs -f horde-server` if it is containerised — the Horde server log
should contain two lines:

```
Loading …\HordeServer.Discord.dll
Discord notification sink registered (guild …, interactions enabled)
```

If a bot token is not configured the second line instead reads *"no bot token is configured;
notifications will be discarded"* — which still confirms the plugin installed correctly, so this is a
useful way to verify the install before creating a bot.

Horde also logs `Added plugin 'Discord'`, but only at **debug** level — you will not see it unless you
have lowered the server's log level.

With `EnableInteractions` on you should also see the gateway connect:

```
Discord gateway ready as YourBot (500000000000000001), session …
Discord interaction router listening (issue)
```

The id on the first line is the bot's **user** id, which for a bot account is the same number as its
application id — so it doubles as a check that you configured `ApplicationId` from the right
application. The second line names the scopes that have handlers behind them; `issue` is currently the
only one, and `listening (no scopes registered yet)` means something started in the wrong order and
buttons will not do anything.

If it instead logs a close code, `4004` means the bot token is wrong and `4014` means a privileged
intent was requested that the application has not been granted — neither is retried, because
reconnecting cannot fix either. This plugin requests no intents at all, so `4014` in practice means
something else is using the same token.

### Sending test messages without a Horde server

You do not have to wait for a real build to fail to find out whether your channel mapping and
permissions work. The repository includes a tool that posts one of every notification to a channel,
driving the real formatting code with stand-in data — **no Horde server, MongoDB or Redis needed**:

```
dotnet run --project tools/DiscordSmoke -c Development                 # all of them
dotnet run --project tools/DiscordSmoke -c Development -- --help       # list them
dotnet run --project tools/DiscordSmoke -c Development -- issue step   # just those
dotnet run --project tools/DiscordSmoke -c Development -- --gateway 50 # connect the gateway
dotnet run --project tools/DiscordSmoke -c Development -- --modal      # the triage dialog, end to end
```

It reads its bot token and target channel from `Horde.local.props`, the same git-ignored file that
points the build at your Horde tree — see `Horde.local.props.template`. Point it at a scratch channel;
the sample data is deliberately awkward, with names containing markdown and errors long enough to be
truncated.

A scenario that reports `REJECTED` prints the Discord error code and what to do about it, which is
usually faster than reading a server log.

## Troubleshooting

**The server fails to start with `Unable to find plugin(s) enabled in config file: Discord`**
You enabled the plugin in `server.json` but Horde cannot find the assembly. `HordeServer.Discord.dll`
must sit directly beside `HordeServer.dll`, not in a subdirectory.

**No `Loading …HordeServer.Discord.dll` line in the log**
The assembly is missing from the application directory, or it was renamed. Horde only scans for files
named `HordeServer.*.dll` — the filename matters. In a container, check the mount actually landed:
`docker compose exec horde-server ls -l /app/HordeServer.Discord.dll`. A bind mount whose source path
does not exist can produce an empty *directory* at the destination rather than an error.

**The plugin loads, but nothing else about it ever appears in the log**
Check that `Plugins` is nested **inside** `Horde` in `server.json`, not beside it. Horde reads
`Horde:Plugins:<name>`, so a top-level `"Plugins"` block is never looked at — and because the plugin is
disabled by default, nothing is enabled, nothing is reported missing, and no error is raised. It is the
one mistake that produces total silence rather than a diagnostic. The shape has to be:

```jsonc
{ "Horde": { "Plugins": { "Discord": { "Enabled": true } } } }
```

`Loading …HordeServer.Discord.dll` still appears, because that is the directory scan; what is missing is
the sink's own registration line. It also takes the `globals.json` section down with it — see below.

**The plugin behaves like an older build after rebuilding it**
A single-file bind mount follows the inode, and `dotnet build` writes a new file. `docker compose
restart` reuses the same container and therefore the same mount. Use `docker compose up -d
--force-recreate horde-server`.

**The server throws `TypeLoadException` or `MissingMethodException` on startup**
The plugin was built against a different version of Horde than the one running. Rebuild it against
your current server (see [Upgrading](#upgrading)).

**Notifications are not appearing**
The plugin logs every rejection with Discord's own error code. Find it in the server log and match it
below — the code says exactly which of these it is, and guessing between them wastes a lot of time:

| Code | Meaning | Fix |
|---|---|---|
| `50001` | Missing Access | The bot cannot see the channel. Invite it, then grant **View Channel** on that channel specifically — a channel override beats a role grant. |
| `50013` | Missing Permissions | It can see the channel but cannot post. Usually **Embed Links**, without which every notification is refused. |
| `10003` | Unknown Channel | The id is wrong, or the channel is in a guild the bot is not in. |
| `50007` | Cannot send to this user | They do not accept direct messages from server members. The notification falls back to a channel. |
| `50278` | No mutual guilds | The `userMap` entry points at somebody who is not in the guild. |
| `40001` | Unauthorized | The bot token is wrong or was regenerated — including revoked by Discord for having been committed to a public repository. |
| `50035` | Invalid Form Body | **The one that is a bug here, not a setup problem.** Discord refused the message itself; the logged body names the field. Please report it. |

If there is no rejection in the log at all, nothing was sent — check the routing map, and look for the
startup line naming unmapped Horde channels.

> [!NOTE]
> **"Sent" is not "arrived".** The client logs a rejection and returns rather than throwing, because a
> notification sink that throws inside Horde is a sink that disturbs the other sinks. Nothing upstream
> can tell a delivered message from a refused one, so the log is the only place delivery is visible —
> a run can report every notification sent while Discord refused all of them.

**Nothing is mapped, and the startup report is empty too**
The `plugins.discord` section did not bind. In order of likelihood:

1. **The plugin is not loaded.** Horde only parses a `plugins` entry for a plugin it actually loaded and
   skips everything else without comment — so a `server.json` problem takes the `globals.json` section
   down with it. Rule out the silent-nesting mistake above first; one cause, two symptoms.
2. **It is in the wrong file.** `ConfigPath` in `server.json` says where the global config is read from,
   and it is often a Perforce path (`//Horde/globals.json`) rather than anything on disk.
3. **The name is misspelled.** An unrecognised key under `plugins` is skipped silently, so `discrod`
   costs you the whole section with no warning. Casing is *not* a cause — Horde normalises plugin
   names, so `discord` and `Discord` bind identically.

**Issue notifications post, but no threads are ever created**
The log says *"no mapped triage channel for issue N, so no thread was started"*. The triage channel
resolved without a guild, and a thread link needs one. Either the channel has no `guild` and there is no
`defaultGuild` while two or more guilds are configured, or the notification landed in `fallbackChannel`
because its Horde channel is unmapped. If threads are not created *anywhere*, check `EnableTriageThreads`
instead — left unset, it turns itself off whenever the Build plugin has a `SlackToken`.

**Emoji show up as `:red_circle:` text**
Something set `ErrorPrefix` or `WarningPrefix` to a Slack-style shortcode. Discord does not expand those
for anything a bot posts — use the literal emoji character, or a custom guild emoji as `<:name:id>`.

**A role mention appears as raw `<@&123…>` text**
The role id belongs to a different guild than the channel it was posted in. Give that `roles` entry a
`guild`, or map a role that exists in the destination guild.

**Buttons say "This interaction failed"**
Discord shows that when nothing answers within three seconds. Usually the gateway is not connected —
check for the `Discord gateway ready` line, and that `EnableInteractions` is not `false`. If the gateway
*is* connected, check the router's `listening (…)` line actually names `issue`; a router with no handler
registered receives the press and has nowhere to send it.

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

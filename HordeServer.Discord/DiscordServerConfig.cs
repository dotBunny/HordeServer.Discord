// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using HordeServer.Plugins;

namespace HordeServer
{
	/// <summary>
	/// Server-level configuration for the Discord plugin, bound from the <c>Horde:Plugins:Discord</c> section of
	/// <c>server.json</c>.
	/// </summary>
	/// <remarks>
	/// Everything here needs a server restart to take effect, so it holds only credentials and infrastructure.
	/// Routing and the user map live in <see cref="DiscordConfig"/> instead, which the config system hot-reloads.
	/// </remarks>
	public class DiscordServerConfig : PluginServerConfig
	{
		/// <summary>
		/// Bot token used to authenticate against the Discord API.
		/// </summary>
		/// <remarks>
		/// Prefer supplying this through the Secrets plugin or an environment variable rather than committing it.
		/// When unset the plugin loads but sends nothing, which is a supported way to run it dark.
		/// </remarks>
		public string? BotToken { get; set; }

		/// <summary>
		/// Application (client) id of the Discord application the bot belongs to. Required to register slash
		/// commands and to respond to interactions.
		/// </summary>
		public string? ApplicationId { get; set; }

		/// <summary>
		/// Id of the guild the bot operates in.
		/// </summary>
		/// <remarks>
		/// Only needed for guild-scoped operations - member lookup and slash command registration. Posting uses
		/// channel ids directly, which are globally unique, so keeping the guild out of the posting path is what
		/// leaves room to support more than one guild later without reworking anything.
		/// </remarks>
		public string? GuildId { get; set; }

		/// <summary>
		/// Whether to open a gateway connection for interactive components. Posting works without it; buttons,
		/// modals and slash commands do not.
		/// </summary>
		public bool EnableInteractions { get; set; } = true;

		/// <summary>
		/// Whether the dashboard's "message these people" and "open this channel" buttons should point at Discord.
		/// </summary>
		/// <remarks>
		/// Leave unset unless you mean to change it. Horde asks every notification sink for a deep link and takes the
		/// **first non-null answer**, ignoring the rest, in an order no plugin controls - so a Discord plugin that
		/// always answered would decide, by luck of registration, whether an existing Slack deployment's dashboard
		/// buttons still opened Slack.
		///
		/// Unset therefore means *automatic*: links are provided only when the Build plugin has no <c>SlackToken</c>,
		/// which is exactly when nothing else would answer. Set it to <c>true</c> to make Discord the dashboard's
		/// chat target even alongside Slack, or <c>false</c> to stay out of it entirely.
		/// </remarks>
		public bool? EnableDeepLinks { get; set; }

		/// <summary>
		/// Channel to send job related notifications to. Multiple channels can be specified, separated by <c>;</c>.
		/// </summary>
		/// <remarks>
		/// An **override**, and normally left unset. By default the plugin translates the Build plugin's own
		/// <c>JobNotificationChannel</c> - a Slack channel id - through the <c>channels</c> map in
		/// <see cref="DiscordConfig"/>, so routing is configured once in Horde rather than twice. Setting this
		/// bypasses that, which is what a deployment running Discord without Slack needs.
		///
		/// Values here are Discord snowflakes, not Slack channel ids and not <c>#channel</c> names. A Slack id put
		/// here is detected and reported rather than silently ignored.
		/// </remarks>
		public string? JobNotificationChannel { get; set; }

		/// <summary>
		/// Channel to send agent related notifications to. Overrides the Build plugin's setting; see
		/// <see cref="JobNotificationChannel"/>.
		/// </summary>
		public string? AgentNotificationChannel { get; set; }

		/// <summary>
		/// Channel to send messages about configuration update failures to. Overrides the Build plugin's setting;
		/// see <see cref="JobNotificationChannel"/>.
		/// </summary>
		public string? ConfigNotificationChannel { get; set; }

		/// <summary>
		/// Channel to send stream update failures to. Overrides the Build plugin's setting; see
		/// <see cref="JobNotificationChannel"/>.
		/// </summary>
		public string? UpdateStreamsNotificationChannel { get; set; }

		/// <summary>
		/// Channel to send device reports to. Overrides the Build plugin's <c>DeviceReportChannel</c>; see
		/// <see cref="JobNotificationChannel"/>.
		/// </summary>
		public string? DeviceNotificationChannel { get; set; }

		/// <summary>
		/// Emoji used to prefix error messages. Accepts a unicode emoji or a custom emoji in <c>&lt;:name:id&gt;</c> form.
		/// </summary>
		/// <remarks>
		/// A literal unicode character, not a <c>:red_circle:</c> shortcode. Slack resolves shortcodes server-side and
		/// Epic's sink relies on that; Discord does not. Its client expands them as a human types, so anything posted
		/// through the API is stored and rendered exactly as sent - a shortcode arrives as the punctuation it is
		/// spelled with. Custom guild emoji are the <c>&lt;:name:id&gt;</c> form, which is a different syntax again.
		/// </remarks>
		public string ErrorPrefix { get; set; } = "🔴 ";

		/// <summary>
		/// Emoji used to prefix warning messages. A unicode emoji, for the reason given on <see cref="ErrorPrefix"/>.
		/// </summary>
		public string WarningPrefix { get; set; } = "⚠️ ";

		/// <summary>
		/// Returns whether enough is configured for the plugin to actually send anything.
		/// </summary>
		public bool IsConfigured => !String.IsNullOrEmpty(BotToken);
	}
}

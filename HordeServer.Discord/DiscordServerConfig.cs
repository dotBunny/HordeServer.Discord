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
		/// Channel to send job related notifications to. Multiple channels can be specified, separated by <c>;</c>.
		/// </summary>
		/// <remarks>
		/// Discord channels are identified by snowflake id, not by name - there is no <c>#channel</c> equivalent.
		/// </remarks>
		public string? JobNotificationChannel { get; set; }

		/// <summary>
		/// Channel to send agent related notifications to.
		/// </summary>
		public string? AgentNotificationChannel { get; set; }

		/// <summary>
		/// Channel to send messages about configuration update failures to.
		/// </summary>
		public string? ConfigNotificationChannel { get; set; }

		/// <summary>
		/// Channel to send stream update failures to.
		/// </summary>
		public string? UpdateStreamsNotificationChannel { get; set; }

		/// <summary>
		/// Emoji used to prefix error messages. Accepts a unicode emoji or a custom emoji in <c>&lt;:name:id&gt;</c> form.
		/// </summary>
		public string ErrorPrefix { get; set; } = ":red_circle: ";

		/// <summary>
		/// Emoji used to prefix warning messages.
		/// </summary>
		public string WarningPrefix { get; set; } = ":warning: ";

		/// <summary>
		/// Returns whether enough is configured for the plugin to actually send anything.
		/// </summary>
		public bool IsConfigured => !String.IsNullOrEmpty(BotToken);
	}
}

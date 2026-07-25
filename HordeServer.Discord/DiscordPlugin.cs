// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using HordeServer.Discord.Notifications;
using HordeServer.Notifications;
using HordeServer.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace HordeServer
{
	/// <summary>
	/// Entry point for the Discord notification plugin.
	/// </summary>
	/// <remarks>
	/// Horde finds this type by scanning its application directory for <c>HordeServer.*.dll</c> and looking for a
	/// <see cref="PluginAttribute"/>, so nothing in the server needs to change to host it. Drop the built assembly
	/// beside the server binaries and set <c>Horde:Plugins:Discord:Enabled</c> to <c>true</c> in <c>server.json</c>.
	/// </remarks>
	[Plugin("Discord", EnabledByDefault = false, ServerConfigType = typeof(DiscordServerConfig), GlobalConfigType = typeof(DiscordConfig))]
	public class DiscordPlugin : IPluginStartup
	{
		readonly DiscordServerConfig _serverConfig;

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="serverConfig">Server configuration bound from the <c>Horde:Plugins:Discord</c> section.</param>
		public DiscordPlugin(DiscordServerConfig serverConfig)
			=> _serverConfig = serverConfig;

		/// <inheritdoc/>
		public void Configure(IApplicationBuilder app)
		{
		}

		/// <inheritdoc/>
		public void ConfigureServices(IServiceCollection serviceCollection)
		{
			// Registering another INotificationSink is purely additive. NotificationService resolves
			// IEnumerable<INotificationSink> and fans out with per-sink exception handling, so a fault here
			// degrades to a logged error and cannot disturb the existing Slack sink.
			//
			// Deliberately unconditional: the sink reports its own configuration state at startup and no-ops
			// when unconfigured, which lets the plugin be verified end to end before any Discord credentials
			// exist. Gating registration on a bot token (the way BuildPlugin gates its Slack sink) is a
			// Phase 1 change, once there is something to gate.
			serviceCollection.AddSingleton<INotificationSink, DiscordNotificationSink>();
		}
	}

	/// <summary>
	/// Helper methods for accessing the Discord plugin's global configuration.
	/// </summary>
	public static class DiscordPluginExtensions
	{
		/// <summary>
		/// Adds the Discord plugin configuration to a plugin config dictionary.
		/// </summary>
		public static void AddDiscordConfig(this IDictionary<PluginName, IPluginConfig> dictionary, DiscordConfig discordConfig)
			=> dictionary[new PluginName("Discord")] = discordConfig;

		/// <summary>
		/// Gets the Discord plugin configuration from a plugin config dictionary.
		/// </summary>
		public static DiscordConfig GetDiscordConfig(this IDictionary<PluginName, IPluginConfig> dictionary)
			=> (DiscordConfig)dictionary[new PluginName("Discord")];
	}
}

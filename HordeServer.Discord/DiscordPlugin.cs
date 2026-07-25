// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using HordeServer.Discord.Client;
using HordeServer.Discord.Notifications;
using HordeServer.Notifications;
using HordeServer.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
			serviceCollection.AddSingleton<DiscordRateLimiter>(sp
				=> new DiscordRateLimiter(sp.GetRequiredService<ILogger<DiscordRateLimiter>>()));

			// Constructed by hand rather than by the container, so that the HttpClient it owns stays private to the
			// plugin. See DiscordClient.Create for why this is not an IHttpClientFactory typed client.
			serviceCollection.AddSingleton<DiscordClient>(sp => DiscordClient.Create(
				sp.GetRequiredService<IOptions<DiscordServerConfig>>(),
				sp.GetRequiredService<DiscordRateLimiter>(),
				sp.GetRequiredService<ILogger<DiscordClient>>()));

			serviceCollection.AddSingleton<DiscordNotificationProcessor>();

			// Registering another INotificationSink is purely additive. NotificationService resolves
			// IEnumerable<INotificationSink> and fans out with per-sink exception handling, so a fault here
			// degrades to a logged error and cannot disturb the existing Slack sink.
			//
			// Deliberately unconditional, which reverses the note left here in Phase 0. BuildPlugin gates its Slack
			// sink on a token being present, but a sink that is registered and says at startup why it will not send
			// is easier to diagnose than one that silently does not exist - and it keeps "run it dark" working as a
			// way to verify the plugin loads before any credentials exist. The real gate is
			// DiscordNotificationProcessor.CanSendJobNotifications.
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

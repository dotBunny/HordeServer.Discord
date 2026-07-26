// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using HordeServer.Discord.Client;
using HordeServer.Discord.Notifications;
using HordeServer.Notifications;
using HordeServer.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

			// The inbound half. Registered whatever the configuration says, because it reports at startup why it is
			// not running - same reasoning as the sink below. It gates itself on DiscordGateway.IsEnabled.
			serviceCollection.AddSingleton<DiscordGateway>();
			serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DiscordGateway>());

			// Also a hosted service, and not only because it subscribes to the gateway on start: nothing else
			// depends on it yet - the issue members are what will - and a singleton nobody resolves is never
			// constructed, so without this the buttons would simply not work.
			serviceCollection.AddSingleton<DiscordInteractionRouter>();
			serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DiscordInteractionRouter>());

			// What makes the triage buttons do something. Behind IHordeIssues because Horde's IssueService is a
			// concrete class that reaches MongoDB in its constructor, and this repo's tests run without one.
			serviceCollection.AddSingleton<IHordeIssues, HordeIssues>();
			serviceCollection.AddSingleton<DiscordIssueTriage>();
			serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DiscordIssueTriage>());

			serviceCollection.AddSingleton<DiscordChannelResolver>();

			// Behind its interface so a /link slash command can be added later as a second provider without
			// touching anything that consumes it. The concrete type is registered too, because the routing report
			// asks it about the role map rather than about a person.
			serviceCollection.AddSingleton<DiscordUserResolver>();
			serviceCollection.AddSingleton<IDiscordUserResolver>(sp => sp.GetRequiredService<DiscordUserResolver>());

			// A singleton on purpose: it exists to remember what has already been said, which a per-request instance
			// could not do. Constructed by hand only so the clock stays an optional constructor parameter, which is
			// what lets the tests drive expiry without waiting a week.
			serviceCollection.AddSingleton<DiscordRepeatFilter>(_ => new DiscordRepeatFilter());
			serviceCollection.AddSingleton<DiscordNotificationProcessor>();

			// Names every Horde channel with no Discord mapping, at startup and on every config reload. Both sides
			// of that map are opaque ids, so a gap in it is otherwise only discovered as a notification that never
			// turned up.
			serviceCollection.AddSingleton<DiscordRoutingReport>();
			serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DiscordRoutingReport>());

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

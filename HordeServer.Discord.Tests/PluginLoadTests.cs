// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using PluginProbe;

namespace HordeServer.Discord.Tests
{
	/// <summary>
	/// Checks that a real Horde server would discover and load the plugin.
	/// </summary>
	/// <remarks>
	/// This replicates <c>ServerApp.CreatePluginCollection</c> against a built server directory rather than booting
	/// the server, which would need MongoDB and Redis. It proves discovery, construction and configuration binding.
	/// It does not exercise <c>INotificationSink</c> - no callback is ever invoked - so a green run here is not
	/// evidence that notifications work.
	/// </remarks>
	[TestClass]
	public sealed class PluginLoadTests
	{
		/// <summary>
		/// Number of members on Horde's <c>INotificationSink</c> as of the engine recorded in the README.
		/// </summary>
		/// <remarks>
		/// A deliberate tripwire rather than a derived value. Epic changes this interface without notice and with no
		/// stability guarantee; when this number moves, the right response is the engine-upgrade skill, not editing
		/// the constant.
		/// </remarks>
		const int ExpectedNotificationSinkMemberCount = 17;

		[AssemblyInitialize]
		public static void DeployPlugin(TestContext context) => TestEnvironment.Deploy(context);

		[TestMethod]
		public void PluginDllMatchesTheServerScanPattern()
		{
			Assert.IsTrue(TestEnvironment.Result.PluginFileFound,
				$"{Probe.PluginFileName} was not in the scan set. The assembly name is load-bearing - the server "
				+ "only looks at files matching HordeServer.*.dll.");
		}

		[TestMethod]
		public void PluginIsDiscoveredAlongsideTheEnginePlugins()
		{
			ProbeResult result = TestEnvironment.Result;

			CollectionAssert.Contains(result.Plugins.Select(x => x.Name).ToList(), Probe.PluginName,
				"No [Plugin(\"Discord\")] type was discovered. Either the attribute is gone or the startup type is "
				+ "not public - the scan reads GetExportedTypes().");

			// Sanity check on the directory itself: finding only our plugin would mean we scanned something that is
			// not a Horde server output, and every other assertion here would be meaningless.
			CollectionAssert.Contains(result.Plugins.Select(x => x.Name).ToList(), "Build",
				$"'{result.AppDir}' does not look like a built Horde server - Epic's own Build plugin is not there.");
		}

		[TestMethod]
		public void PluginIsDisabledByDefault()
		{
			DiscoveredPlugin plugin = RequireDiscoveredPlugin();

			Assert.IsFalse(plugin.EnabledByDefault,
				"The plugin must stay opt-in. Dropping it next to a server should never start sending Discord "
				+ "messages until someone sets Horde:Plugins:Discord:Enabled.");
		}

		[TestMethod]
		public void PluginCollectionAcceptsTheConfigTypes()
		{
			ProbeResult result = TestEnvironment.Result;

			Assert.IsTrue(result.ImplementsPluginStartup, "The startup type does not implement IPluginStartup.");
			Assert.IsTrue(result.AddSucceeded,
				$"Horde's own PluginCollection.Add rejected the plugin: {result.AddError}. That call is what "
				+ "validates the generic constraints - ServerConfigType must derive from PluginServerConfig and "
				+ "GlobalConfigType must implement IPluginConfig.");

			// Not a typo: PluginName normalises to lowercase, so the plugin registers as 'discord'.
			Assert.AreEqual("discord", result.LoadedPluginName);

			// Type names as strings, not nameof: this assembly deliberately has no compile-time reference to the
			// plugin, so that the only copy of it in the process is the one loaded from the server directory.
			Assert.AreEqual("DiscordServerConfig", result.LoadedServerConfigTypeName);
			Assert.AreEqual("DiscordConfig", result.LoadedGlobalConfigTypeName);
		}

		[TestMethod]
		public void ServerConfigBindsFromTheHordePluginsSection()
		{
			IReadOnlyDictionary<string, string> bound = TestEnvironment.Result.BoundServerConfig;

			Assert.AreEqual("test-token", bound["BotToken"]);
			Assert.AreEqual("123456789", bound["GuildId"]);
			Assert.AreEqual("987654321", bound["JobNotificationChannel"]);
			Assert.AreEqual("False", bound["EnableInteractions"], "A non-default bool did not survive binding.");
			Assert.AreEqual("True", bound["IsConfigured"], "IsConfigured should follow from a bound bot token.");
		}

		[TestMethod]
		public void EnabledInServerConfigResolvesToLoad()
		{
			Assert.IsTrue(TestEnvironment.Result.WouldLoad,
				"With Horde:Plugins:Discord:Enabled set, the server's enablement rule should resolve to loading "
				+ "the plugin.");
		}

		[TestMethod]
		public void NoEngineAssemblyFailsToReflect()
		{
			IReadOnlyList<string> ours = TestEnvironment.Result.Warnings
				.Where(x => x.Contains("Discord", StringComparison.OrdinalIgnoreCase))
				.ToList();

			Assert.AreEqual(0, ours.Count, $"The scan could not reflect over our own assembly: {String.Join("; ", ours)}");
		}

		[TestMethod]
		public void SinkImplementsEveryNotificationSinkMember()
		{
			SinkContract sink = RequireSink();

			Assert.AreEqual(0, sink.MethodsLeftToDefaultImplementation.Count,
				"These INotificationSink members fall through to a default implementation on the interface, which "
				+ "means Horde is raising notifications the plugin silently ignores: "
				+ $"{String.Join(", ", sink.MethodsLeftToDefaultImplementation)}. Run the engine-upgrade skill.");
		}

		[TestMethod]
		public void NotificationSinkHasNotGrownOrShrunk()
		{
			SinkContract sink = RequireSink();

			Assert.AreEqual(ExpectedNotificationSinkMemberCount, sink.InterfaceMethodCount,
				"INotificationSink changed shape. It is internal to Horde with no stability guarantee, so this is "
				+ "expected to happen eventually - work through the engine-upgrade skill, then update this count "
				+ "and the README's engine compatibility table.");
		}

		static DiscoveredPlugin RequireDiscoveredPlugin()
		{
			DiscoveredPlugin? plugin = TestEnvironment.Result.Plugins
				.FirstOrDefault(x => x.Name.Equals(Probe.PluginName, StringComparison.OrdinalIgnoreCase));

			Assert.IsNotNull(plugin, $"No [Plugin(\"{Probe.PluginName}\")] type was discovered.");
			return plugin;
		}

		static SinkContract RequireSink()
		{
			SinkContract? sink = TestEnvironment.Result.Sink;

			Assert.IsNotNull(sink, "The probe did not get as far as inspecting the sink; fix the load failures first.");
			Assert.IsNotNull(sink.TypeName, "No INotificationSink implementation was found in the plugin assembly.");
			return sink;
		}
	}
}

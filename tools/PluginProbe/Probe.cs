// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Reflection;
using System.Runtime.CompilerServices;
using HordeServer.Notifications;
using HordeServer.Plugins;
using Microsoft.Extensions.Configuration;

namespace PluginProbe
{
	/// <summary>
	/// Replicates <c>HordeServer/ServerApp.cs</c> <c>CreatePluginCollection</c> against a built server directory, so
	/// the plugin's discoverability can be checked without standing up MongoDB and Redis and booting the real server.
	/// </summary>
	public static class Probe
	{
		/// <summary>
		/// Filename prefix the server's plugin scan matches.
		/// </summary>
		public const string ScanPrefix = "HordeServer.";

		/// <summary>
		/// Filename suffix the server's plugin scan matches.
		/// </summary>
		public const string ScanSuffix = ".dll";

		/// <summary>
		/// File the plugin is deployed as.
		/// </summary>
		public const string PluginFileName = "HordeServer.Discord.dll";

		/// <summary>
		/// Name declared on the plugin attribute.
		/// </summary>
		public const string PluginName = "Discord";

		/// <summary>
		/// Scans a server directory and reports what a real server would make of the plugin sitting in it.
		/// </summary>
		/// <param name="appDir">A built Horde server output directory with the plugin DLL already copied in.</param>
		/// <returns>What was observed. Never throws for an ordinary failure - failures are recorded on the result.</returns>
		/// <remarks>
		/// Must not be inlined into a caller that has not yet installed <see cref="EngineAssemblyResolver"/>: the JIT
		/// resolves a method's referenced types when it compiles the method, so every Horde type mentioned here would
		/// be resolved before the handler was attached, and the load would fail.
		/// </remarks>
		[MethodImpl(MethodImplOptions.NoInlining)]
		public static ProbeResult Run(string appDir)
		{
			ProbeResult result = new ProbeResult(appDir);

			// --- Steps 1+2: enumerate AppDir for HordeServer.*.dll, exactly as ServerApp does -------------------
			foreach (FileInfo fileInfo in new DirectoryInfo(appDir).EnumerateFiles())
			{
				if (fileInfo.Name.StartsWith(ScanPrefix, StringComparison.OrdinalIgnoreCase)
					&& fileInfo.Name.EndsWith(ScanSuffix, StringComparison.OrdinalIgnoreCase)
					&& fileInfo.Name.Length > ScanPrefix.Length + ScanSuffix.Length)
				{
					result.CandidateFiles.Add(fileInfo.Name);
				}
			}

			result.PluginFileFound = result.CandidateFiles.Contains(PluginFileName, StringComparer.OrdinalIgnoreCase);

			// --- Step 3: load each and look for [Plugin] --------------------------------------------------------
			Assembly? pluginAssembly = null;
			Type? startupType = null;

			foreach (string fileName in result.CandidateFiles)
			{
				try
				{
					string path = Path.Combine(appDir, fileName);

					// Our own plugin goes through an isolated context so that this is unambiguously the deployed
					// file and not some other copy the host process already had loaded. See PluginLoadContext.
					Assembly assembly = fileName.Equals(PluginFileName, StringComparison.OrdinalIgnoreCase)
						? new PluginLoadContext(path).LoadPlugin()
						: Assembly.LoadFrom(path);

					foreach (Type type in assembly.GetExportedTypes())
					{
						PluginAttribute? attribute = type.GetCustomAttribute<PluginAttribute>();
						if (attribute == null)
						{
							continue;
						}

						result.Plugins.Add(new DiscoveredPlugin(attribute.Name, attribute.EnabledByDefault, fileName, type.FullName ?? type.Name));

						if (attribute.Name.Equals(PluginName, StringComparison.OrdinalIgnoreCase))
						{
							pluginAssembly = assembly;
							startupType = type;
						}
					}
				}
				catch (Exception ex)
				{
					result.Warnings.Add($"{fileName}: {ex.GetType().Name}: {ex.Message}");
				}
			}

			if (startupType == null || pluginAssembly == null)
			{
				return result;
			}

			PluginAttribute pluginAttribute = startupType.GetCustomAttribute<PluginAttribute>()!;
			result.StartupTypeName = startupType.FullName;
			result.AttributeServerConfigTypeName = pluginAttribute.ServerConfigType?.Name;
			result.AttributeGlobalConfigTypeName = pluginAttribute.GlobalConfigType?.Name;
			result.ImplementsPluginStartup = typeof(IPluginStartup).IsAssignableFrom(startupType);

			// --- Step 4: let Horde's own PluginCollection construct it ------------------------------------------
			// Validates the generic constraints on the config types - this throws if ServerConfigType does not
			// derive from PluginServerConfig, or GlobalConfigType does not implement IPluginConfig.
			try
			{
				ILoadedPlugin loaded = new PluginCollection().Add(startupType);
				result.AddSucceeded = true;
				result.LoadedPluginName = loaded.Name.ToString();
				result.LoadedServerConfigTypeName = loaded.ServerConfigType.Name;
				result.LoadedGlobalConfigTypeName = loaded.GlobalConfigType.Name;
			}
			catch (Exception ex)
			{
				result.AddError = $"{ex.GetType().Name}: {ex.Message}";
				return result;
			}

			// --- Step 5: prove server config binds from the Horde:Plugins:Discord section -----------------------
			IConfiguration configuration = new ConfigurationBuilder()
				.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["Horde:Plugins:Discord:Enabled"] = "true",
					["Horde:Plugins:Discord:BotToken"] = "test-token",
					["Horde:Plugins:Discord:GuildId"] = "123456789",
					["Horde:Plugins:Discord:JobNotificationChannel"] = "987654321",
					["Horde:Plugins:Discord:EnableInteractions"] = "false",
				})
				.Build();

			IConfigurationSection pluginSection = configuration.GetSection("Horde").GetSection("Plugins");

			object serverConfig = Activator.CreateInstance(pluginAttribute.ServerConfigType!)!;
			pluginSection.GetSection(PluginName).Bind(serverConfig);

			foreach (string name in new[] { "Enabled", "BotToken", "GuildId", "JobNotificationChannel", "EnableInteractions", "IsConfigured" })
			{
				PropertyInfo? property = pluginAttribute.ServerConfigType!.GetProperty(name);
				result.BoundServerConfig[name] = property?.GetValue(serverConfig)?.ToString() ?? "<null>";
			}

			// Mirror ServerApp's enablement decision: config wins, the attribute default is the fallback.
			Dictionary<string, PluginServerConfig> pluginConfigs = new Dictionary<string, PluginServerConfig>(StringComparer.OrdinalIgnoreCase);
			pluginSection.Bind(pluginConfigs);

			bool? enabled = pluginConfigs.TryGetValue(PluginName, out PluginServerConfig? pluginConfig) ? pluginConfig.Enabled : null;
			result.WouldLoad = enabled ?? pluginAttribute.EnabledByDefault;

			// --- Step 6: check the sink against the interface it is compiled to satisfy -------------------------
			result.Sink = InspectSink(pluginAssembly);

			return result;
		}

		static SinkContract InspectSink(Assembly pluginAssembly)
		{
			Type sinkInterface = typeof(INotificationSink);

			SinkContract contract = new SinkContract
			{
				InterfaceMethodCount = sinkInterface.GetMethods().Length,
			};

			Type? sinkType = pluginAssembly
				.GetExportedTypes()
				.FirstOrDefault(x => !x.IsInterface && !x.IsAbstract && sinkInterface.IsAssignableFrom(x));

			if (sinkType == null)
			{
				return contract;
			}

			contract.TypeName = sinkType.FullName;

			// A method whose target is still declared on the interface resolved to a default implementation, which
			// means Epic added a member and we are quietly not handling it.
			InterfaceMapping mapping = sinkType.GetInterfaceMap(sinkInterface);
			for (int idx = 0; idx < mapping.InterfaceMethods.Length; idx++)
			{
				if (mapping.TargetMethods[idx].DeclaringType == sinkInterface)
				{
					contract.MethodsLeftToDefaultImplementation.Add(mapping.InterfaceMethods[idx].Name);
				}
			}

			return contract;
		}
	}
}

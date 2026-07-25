// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

namespace PluginProbe
{
	/// <summary>
	/// A plugin found by the assembly scan.
	/// </summary>
	/// <param name="Name">Name declared on the plugin attribute.</param>
	/// <param name="EnabledByDefault">Whether it loads without being named in server config.</param>
	/// <param name="AssemblyFileName">File it was found in.</param>
	/// <param name="TypeName">Full name of the startup type.</param>
	public sealed record DiscoveredPlugin(string Name, bool EnabledByDefault, string AssemblyFileName, string TypeName);

	/// <summary>
	/// Everything the probe observed about a server directory.
	/// </summary>
	/// <remarks>
	/// Deliberately inert data. The probe fills it, the console tool renders it and the tests assert against it -
	/// which is the whole reason it exists rather than the probe printing as it goes.
	/// </remarks>
	public sealed class ProbeResult
	{
		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="appDir">Directory that was scanned.</param>
		public ProbeResult(string appDir) => AppDir = appDir;

		/// <summary>
		/// Directory that was scanned.
		/// </summary>
		public string AppDir { get; }

		/// <summary>
		/// File names matching the server's <c>HordeServer.*.dll</c> plugin scan pattern.
		/// </summary>
		public List<string> CandidateFiles { get; } = new List<string>();

		/// <summary>
		/// Every plugin discovered, ours included.
		/// </summary>
		public List<DiscoveredPlugin> Plugins { get; } = new List<DiscoveredPlugin>();

		/// <summary>
		/// Assemblies that matched the scan but could not be reflected over.
		/// </summary>
		/// <remarks>
		/// Usually benign - an Epic plugin that fails to reflect does not affect ours. Only interesting if one
		/// names our assembly.
		/// </remarks>
		public List<string> Warnings { get; } = new List<string>();

		/// <summary>
		/// Whether the plugin DLL is present in the scanned directory under a name the scan matches.
		/// </summary>
		public bool PluginFileFound { get; set; }

		/// <summary>
		/// Full name of our startup type, or null if the scan did not find it.
		/// </summary>
		public string? StartupTypeName { get; set; }

		/// <summary>
		/// Server config type named on the plugin attribute.
		/// </summary>
		public string? AttributeServerConfigTypeName { get; set; }

		/// <summary>
		/// Global config type named on the plugin attribute.
		/// </summary>
		public string? AttributeGlobalConfigTypeName { get; set; }

		/// <summary>
		/// Whether the startup type implements <c>IPluginStartup</c>.
		/// </summary>
		public bool ImplementsPluginStartup { get; set; }

		/// <summary>
		/// Whether Horde's own <c>PluginCollection.Add</c> accepted the type.
		/// </summary>
		/// <remarks>
		/// The single most valuable signal in the probe: it is what validates the generic constraints on the two
		/// config types, which nothing else exercises outside a running server.
		/// </remarks>
		public bool AddSucceeded { get; set; }

		/// <summary>
		/// Why <c>PluginCollection.Add</c> rejected the type, when it did.
		/// </summary>
		public string? AddError { get; set; }

		/// <summary>
		/// Name the plugin registered under. Normalises to lowercase - <c>discord</c>, not <c>Discord</c>.
		/// </summary>
		public string? LoadedPluginName { get; set; }

		/// <summary>
		/// Server config type as resolved by the plugin collection.
		/// </summary>
		public string? LoadedServerConfigTypeName { get; set; }

		/// <summary>
		/// Global config type as resolved by the plugin collection.
		/// </summary>
		public string? LoadedGlobalConfigTypeName { get; set; }

		/// <summary>
		/// Values read back off the server config type after binding a representative configuration section.
		/// </summary>
		public Dictionary<string, string> BoundServerConfig { get; } = new Dictionary<string, string>();

		/// <summary>
		/// Whether Horde's enablement rule resolves to loading the plugin, given it is enabled in server config.
		/// </summary>
		public bool WouldLoad { get; set; }

		/// <summary>
		/// What the notification sink in the loaded assembly looks like against the engine's interface.
		/// </summary>
		public SinkContract? Sink { get; set; }

		/// <summary>
		/// Whether every check the probe can make passed.
		/// </summary>
		public bool Success =>
			PluginFileFound
			&& StartupTypeName != null
			&& ImplementsPluginStartup
			&& AddSucceeded
			&& WouldLoad
			&& Sink is { Matches: true };
	}

	/// <summary>
	/// How the plugin's notification sink lines up with the engine's <c>INotificationSink</c>.
	/// </summary>
	/// <remarks>
	/// This is the engine-drift alarm. A member added to or removed from the interface breaks the plugin build, so
	/// it is caught before this ever runs - but a *default* interface method added by Epic would not, and would
	/// silently leave a notification unhandled. That is the case these numbers exist to catch.
	/// </remarks>
	public sealed class SinkContract
	{
		/// <summary>
		/// Full name of the sink type found in the loaded plugin assembly, or null if there is none.
		/// </summary>
		public string? TypeName { get; set; }

		/// <summary>
		/// Number of methods declared on the engine's interface.
		/// </summary>
		public int InterfaceMethodCount { get; set; }

		/// <summary>
		/// Interface methods the sink does not itself implement, meaning they resolve to a default implementation
		/// on the interface.
		/// </summary>
		public List<string> MethodsLeftToDefaultImplementation { get; } = new List<string>();

		/// <summary>
		/// Whether a sink was found and it implements every member itself.
		/// </summary>
		public bool Matches => TypeName != null && MethodsLeftToDefaultImplementation.Count == 0;
	}
}

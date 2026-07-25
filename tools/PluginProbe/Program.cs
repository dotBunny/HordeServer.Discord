// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

// Phase 0 verification harness. Replicates HordeServer/ServerApp.cs CreatePluginCollection so the
// plugin's discoverability can be checked without standing up Mongo/Redis and booting the real server.
//
// Run it against a built Horde server output that already has HordeServer.Discord.dll copied in:
//
//     dotnet run --project tools/PluginProbe -- "<horde-bin-dir>"
//
// With no argument it falls back to the HordeBinDir baked in at build time, then to HORDE_BIN_DIR -
// the same resolution order Directory.Build.props uses, so on a configured machine it just works.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;

string? appDir = args.Length > 0 ? args[0] : (BuildTimeHordeBinDir() ?? Environment.GetEnvironmentVariable("HORDE_BIN_DIR"));

if (String.IsNullOrWhiteSpace(appDir))
{
	Console.Error.WriteLine("No Horde server directory to scan.");
	Console.Error.WriteLine("Pass one as the first argument, set HORDE_BIN_DIR, or create Horde.local.props (see Horde.local.props.template).");
	return 2;
}

if (!Directory.Exists(appDir))
{
	Console.Error.WriteLine($"Horde server directory does not exist: {appDir}");
	return 2;
}

// Resolve engine assemblies out of the app dir. The real server gets this for free because it *is* the
// app; here we have to opt in before any Horde type is touched - hence the NoInlining split below.
AssemblyLoadContext.Default.Resolving += (ctx, name) =>
{
	string candidate = Path.Combine(appDir, name.Name + ".dll");
	return File.Exists(candidate) ? ctx.LoadFromAssemblyPath(candidate) : null;
};

return Probe.Run(appDir);

// The path baked in by the csproj from $(HordeBinDir), so a developer with Horde.local.props configured
// can run the probe with no arguments. Reading our own metadata touches no Horde type, so it is safe to
// do before the resolver above is installed.
static string? BuildTimeHordeBinDir()
{
	string? value = typeof(Probe).Assembly
		.GetCustomAttributes<AssemblyMetadataAttribute>()
		.FirstOrDefault(x => x.Key == "HordeBinDir")?.Value;

	return String.IsNullOrWhiteSpace(value) ? null : value;
}

static class Probe
{
	// Must not be inlined into the top-level statements: the JIT resolves a method's referenced types
	// when it compiles the method, so any Horde type mentioned here would be resolved before the
	// AssemblyLoadContext handler above is attached, and the load would fail.
	[MethodImpl(MethodImplOptions.NoInlining)]
	public static int Run(string appDir)
	{
		Console.WriteLine($"Scanning: {appDir}");
		Console.WriteLine();

		// --- Step 1+2: enumerate AppDir for HordeServer.*.dll, exactly as ServerApp does ---------------
		const string Prefix = "HordeServer.";
		const string Suffix = ".dll";

		List<FileInfo> candidates = new();
		foreach (FileInfo fileInfo in new DirectoryInfo(appDir).EnumerateFiles())
		{
			if (fileInfo.Name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
				&& fileInfo.Name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)
				&& fileInfo.Name.Length > Prefix.Length + Suffix.Length)
			{
				candidates.Add(fileInfo);
			}
		}

		Console.WriteLine($"Matched {candidates.Count} candidate assemblies by filename pattern.");

		bool discordFileFound = candidates.Any(x => x.Name.Equals("HordeServer.Discord.dll", StringComparison.OrdinalIgnoreCase));
		Console.WriteLine(discordFileFound
			? "  [PASS] HordeServer.Discord.dll matches the scan pattern."
			: "  [FAIL] HordeServer.Discord.dll was NOT found in the scan set.");
		Console.WriteLine();

		// --- Step 3: load each and look for [Plugin] ---------------------------------------------------
		HordeServer.Plugins.PluginCollection collection = new();
		Type? discordStartupType = null;
		int discovered = 0;

		foreach (FileInfo file in candidates)
		{
			try
			{
				Assembly assembly = Assembly.LoadFrom(file.FullName);
				foreach (Type type in assembly.GetExportedTypes())
				{
					HordeServer.Plugins.PluginAttribute? attr = type.GetCustomAttribute<HordeServer.Plugins.PluginAttribute>();
					if (attr != null)
					{
						discovered++;
						string flag = attr.EnabledByDefault ? "default-on" : "default-off";
						Console.WriteLine($"  found plugin '{attr.Name}' ({flag}) in {file.Name}");
						if (attr.Name.Equals("Discord", StringComparison.OrdinalIgnoreCase))
						{
							discordStartupType = type;
						}
					}
				}
			}
			catch (Exception ex)
			{
				Console.WriteLine($"  [WARN] {file.Name}: {ex.GetType().Name}: {ex.Message}");
			}
		}

		Console.WriteLine();
		Console.WriteLine($"Discovered {discovered} plugins total.");
		Console.WriteLine();

		if (discordStartupType == null)
		{
			Console.WriteLine("  [FAIL] No [Plugin(\"Discord\")] type was discovered.");
			return 1;
		}

		Console.WriteLine($"  [PASS] Discord plugin type: {discordStartupType.FullName}");

		HordeServer.Plugins.PluginAttribute discordAttr = discordStartupType.GetCustomAttribute<HordeServer.Plugins.PluginAttribute>()!;
		Console.WriteLine($"         ServerConfigType = {discordAttr.ServerConfigType?.Name ?? "<null>"}");
		Console.WriteLine($"         GlobalConfigType = {discordAttr.GlobalConfigType?.Name ?? "<null>"}");
		Console.WriteLine($"         implements IPluginStartup = {typeof(HordeServer.Plugins.IPluginStartup).IsAssignableFrom(discordStartupType)}");
		Console.WriteLine();

		// --- Step 4: let Horde's own PluginCollection construct it -------------------------------------
		// Validates the generic constraints on the config types - this throws if ServerConfigType does not
		// derive from PluginServerConfig, or GlobalConfigType does not implement IPluginConfig.
		try
		{
			HordeServer.Plugins.ILoadedPlugin loaded = collection.Add(discordStartupType);
			Console.WriteLine($"  [PASS] PluginCollection.Add succeeded - name '{loaded.Name}'");
			Console.WriteLine($"         ServerConfigType = {loaded.ServerConfigType.Name}");
			Console.WriteLine($"         GlobalConfigType = {loaded.GlobalConfigType.Name}");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"  [FAIL] PluginCollection.Add threw: {ex.GetType().Name}: {ex.Message}");
			return 1;
		}
		Console.WriteLine();

		// --- Step 5: prove server config binds from the Horde:Plugins:Discord section ------------------
		IConfiguration config = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["Horde:Plugins:Discord:Enabled"] = "true",
				["Horde:Plugins:Discord:BotToken"] = "test-token",
				["Horde:Plugins:Discord:GuildId"] = "123456789",
				["Horde:Plugins:Discord:JobNotificationChannel"] = "987654321",
				["Horde:Plugins:Discord:EnableInteractions"] = "false",
			})
			.Build();

		object serverConfig = Activator.CreateInstance(discordAttr.ServerConfigType!)!;
		config.GetSection("Horde").GetSection("Plugins").GetSection("Discord").Bind(serverConfig);

		Console.WriteLine("  Bound server config:");
		foreach (string name in new[] { "Enabled", "BotToken", "GuildId", "JobNotificationChannel", "EnableInteractions", "IsConfigured" })
		{
			PropertyInfo? prop = discordAttr.ServerConfigType!.GetProperty(name);
			Console.WriteLine($"         {name,-24} = {prop?.GetValue(serverConfig) ?? "<null>"}");
		}

		// Mirror ServerApp's enablement decision: config wins, attribute default is the fallback.
		Dictionary<string, HordeServer.Plugins.PluginServerConfig> pluginConfigs = new(StringComparer.OrdinalIgnoreCase);
		config.GetSection("Horde").GetSection("Plugins").Bind(pluginConfigs);
		bool? enabled = pluginConfigs.TryGetValue("Discord", out HordeServer.Plugins.PluginServerConfig? pc) ? pc.Enabled : null;
		bool wouldLoad = enabled ?? discordAttr.EnabledByDefault;

		Console.WriteLine();
		Console.WriteLine(wouldLoad
			? "  [PASS] With Enabled=true in server.json, Horde would load this plugin."
			: "  [FAIL] Horde would NOT load this plugin.");

		Console.WriteLine();
		Console.WriteLine("Phase 0 verification complete.");
		return 0;
	}
}

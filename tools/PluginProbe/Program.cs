// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

// Human-facing front end for the plugin load probe. HordeServer.Discord.Tests runs the same Probe.Run over the
// same server directory and asserts on the result; this prints it, which is what you want when an engine upgrade
// has broken something and a red test is not enough to tell you what.
//
//     dotnet run --project tools/PluginProbe -- "<horde-bin-dir>"
//
// With no argument it falls back to the HordeBinDir baked in at build time, then to HORDE_BIN_DIR - the same
// resolution order Directory.Build.props uses, so on a configured machine it just works.

using System.Reflection;
using PluginProbe;

string? appDir = HordeBinDirLocator.Resolve(typeof(Probe).Assembly, args.Length > 0 ? args[0] : null);

if (appDir == null)
{
	Console.Error.WriteLine(HordeBinDirLocator.NotFoundMessage);
	return 2;
}

if (!Directory.Exists(appDir))
{
	Console.Error.WriteLine($"Horde server directory does not exist: {appDir}");
	return 2;
}

// Resolve engine assemblies out of the app dir before anything touches a Horde type. See EngineAssemblyResolver
// for why this cannot be folded into the call below.
EngineAssemblyResolver.Install(appDir);

return Report.Render(Probe.Run(appDir));

static class Report
{
	public static int Render(ProbeResult result)
	{
		Console.WriteLine($"Scanning: {result.AppDir}");
		Console.WriteLine();

		Console.WriteLine($"Matched {result.CandidateFiles.Count} candidate assemblies by filename pattern.");
		Console.WriteLine(result.PluginFileFound
			? $"  [PASS] {Probe.PluginFileName} matches the scan pattern."
			: $"  [FAIL] {Probe.PluginFileName} was NOT found in the scan set.");
		Console.WriteLine();

		foreach (DiscoveredPlugin plugin in result.Plugins)
		{
			string flag = plugin.EnabledByDefault ? "default-on" : "default-off";
			Console.WriteLine($"  found plugin '{plugin.Name}' ({flag}) in {plugin.AssemblyFileName}");
		}

		foreach (string warning in result.Warnings)
		{
			Console.WriteLine($"  [WARN] {warning}");
		}

		Console.WriteLine();
		Console.WriteLine($"Discovered {result.Plugins.Count} plugins total.");
		Console.WriteLine();

		if (result.StartupTypeName == null)
		{
			Console.WriteLine($"  [FAIL] No [Plugin(\"{Probe.PluginName}\")] type was discovered.");
			return 1;
		}

		Console.WriteLine($"  [PASS] Discord plugin type: {result.StartupTypeName}");
		Console.WriteLine($"         ServerConfigType = {result.AttributeServerConfigTypeName ?? "<null>"}");
		Console.WriteLine($"         GlobalConfigType = {result.AttributeGlobalConfigTypeName ?? "<null>"}");
		Console.WriteLine($"         implements IPluginStartup = {result.ImplementsPluginStartup}");
		Console.WriteLine();

		if (!result.AddSucceeded)
		{
			Console.WriteLine($"  [FAIL] PluginCollection.Add threw: {result.AddError}");
			return 1;
		}

		Console.WriteLine($"  [PASS] PluginCollection.Add succeeded - name '{result.LoadedPluginName}'");
		Console.WriteLine($"         ServerConfigType = {result.LoadedServerConfigTypeName}");
		Console.WriteLine($"         GlobalConfigType = {result.LoadedGlobalConfigTypeName}");
		Console.WriteLine();

		Console.WriteLine("  Bound server config:");
		foreach ((string name, string value) in result.BoundServerConfig)
		{
			Console.WriteLine($"         {name,-24} = {value}");
		}

		Console.WriteLine();
		Console.WriteLine(result.WouldLoad
			? "  [PASS] With Enabled=true in server.json, Horde would load this plugin."
			: "  [FAIL] Horde would NOT load this plugin.");
		Console.WriteLine();

		SinkContract? sink = result.Sink;
		if (sink?.TypeName == null)
		{
			Console.WriteLine("  [FAIL] No INotificationSink implementation was found in the plugin assembly.");
		}
		else if (sink.MethodsLeftToDefaultImplementation.Count > 0)
		{
			Console.WriteLine($"  [FAIL] {sink.TypeName} leaves {sink.MethodsLeftToDefaultImplementation.Count} of "
				+ $"{sink.InterfaceMethodCount} INotificationSink members to a default implementation:");
			foreach (string method in sink.MethodsLeftToDefaultImplementation)
			{
				Console.WriteLine($"         {method}");
			}
			Console.WriteLine("         Epic added interface members. Run the engine-upgrade skill.");
		}
		else
		{
			Console.WriteLine($"  [PASS] {sink.TypeName} implements all {sink.InterfaceMethodCount} INotificationSink members.");
		}

		Console.WriteLine();
		Console.WriteLine(result.Success ? "Plugin load verification complete." : "Plugin load verification FAILED.");
		return result.Success ? 0 : 1;
	}
}

// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Reflection;
using System.Runtime.CompilerServices;
using PluginProbe;

namespace HordeServer.Discord.Tests
{
	/// <summary>
	/// Locates the built Horde server, deploys the plugin into it, and runs the load probe once for the whole test
	/// assembly.
	/// </summary>
	static class TestEnvironment
	{
		static readonly object s_lock = new object();
		static ProbeResult? s_result;

		/// <summary>
		/// Built Horde server output directory, resolved the same way the build resolves it.
		/// </summary>
		public static string? HordeBinDir { get; } = HordeBinDirLocator.Resolve(typeof(TestEnvironment).Assembly);

		/// <summary>
		/// The plugin DLL this test run is checking, in the plugin project's own output.
		/// </summary>
		public static string? PluginOutputPath { get; } = GetMetadata("PluginOutputPath");

		/// <summary>
		/// Installs the engine assembly resolver.
		/// </summary>
		/// <remarks>
		/// A module initializer, not <c>[AssemblyInitialize]</c>: the resolver has to be attached before anything
		/// in this assembly can cause a Horde type to be resolved, and the test framework reflects over test
		/// classes long before it runs an assembly initializer. See <see cref="EngineAssemblyResolver"/>.
		///
		/// This mentions no Horde type itself, which is what makes it safe to run this early.
		/// </remarks>
		[ModuleInitializer]
		internal static void InstallAssemblyResolver()
		{
			string? hordeBinDir = HordeBinDir;
			if (!String.IsNullOrEmpty(hordeBinDir) && Directory.Exists(hordeBinDir))
			{
				EngineAssemblyResolver.Install(hordeBinDir);
			}
		}

		/// <summary>
		/// Copies the freshly built plugin into the server directory.
		/// </summary>
		/// <remarks>
		/// The tests do the deploy themselves on purpose. Doing it by hand is the single most common way to get a
		/// misleading result out of the probe - you end up verifying whatever DLL was left in the server directory
		/// by the last run rather than the one you just built.
		/// </remarks>
		public static void Deploy(TestContext context)
		{
			string hordeBinDir = RequireHordeBinDir();
			string pluginOutputPath = RequirePluginOutputPath();
			string destination = Path.Combine(hordeBinDir, Probe.PluginFileName);

			try
			{
				File.Copy(pluginOutputPath, destination, true);
			}
			catch (IOException ex)
			{
				Assert.Inconclusive($"Could not deploy the plugin to '{destination}': {ex.Message}. "
					+ "A running Horde server holds a lock on it; stop the server and re-run.");
			}

			context.WriteLine($"Deployed {pluginOutputPath}");
			context.WriteLine($"      -> {destination}");
		}

		/// <summary>
		/// Result of probing the server directory, computed once and shared by every test.
		/// </summary>
		public static ProbeResult Result
		{
			get
			{
				lock (s_lock)
				{
					return s_result ??= Probe.Run(RequireHordeBinDir());
				}
			}
		}

		/// <summary>
		/// Directory the plugin builds into, for checks against the shape of the drop itself.
		/// </summary>
		public static string PluginOutputDirectory => Path.GetDirectoryName(RequirePluginOutputPath())!;

		static string RequireHordeBinDir()
		{
			string? hordeBinDir = HordeBinDir;

			if (String.IsNullOrEmpty(hordeBinDir))
			{
				Assert.Inconclusive(HordeBinDirLocator.NotFoundMessage);
			}

			if (!Directory.Exists(hordeBinDir))
			{
				Assert.Inconclusive($"HordeBinDir is set to '{hordeBinDir}' but that directory does not exist. "
					+ "Build the Horde solution, or repoint Horde.local.props.");
			}

			return hordeBinDir!;
		}

		static string RequirePluginOutputPath()
		{
			string? pluginOutputPath = PluginOutputPath;

			if (String.IsNullOrEmpty(pluginOutputPath))
			{
				Assert.Inconclusive("No plugin output path was baked into the test assembly.");
			}

			pluginOutputPath = Path.GetFullPath(pluginOutputPath!);

			if (!File.Exists(pluginOutputPath))
			{
				Assert.Inconclusive($"The plugin has not been built for this configuration: '{pluginOutputPath}' does not exist.");
			}

			return pluginOutputPath;
		}

		static string? GetMetadata(string key)
			=> typeof(TestEnvironment).Assembly
				.GetCustomAttributes<AssemblyMetadataAttribute>()
				.FirstOrDefault(x => x.Key == key)?.Value;
	}
}

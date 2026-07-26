// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using PluginProbe;

namespace HordeServer.Discord.Tests
{
	/// <summary>
	/// Checks the shape of what gets deployed, rather than what it does.
	/// </summary>
	/// <remarks>
	/// The plugin's whole deployment story is "copy one file". That holds only while every engine reference carries
	/// <c>&lt;Private&gt;false&lt;/Private&gt;</c> and nothing takes a package dependency, both of which are easy to
	/// break by adding a reference without thinking about it. Committing a stray engine assembly would also breach
	/// the UE EULA - see <c>.claude/PLAN.md</c> section 3.1a.
	/// </remarks>
	[TestClass]
	public sealed class PluginDropTests
	{
		[TestMethod]
		public void NoEngineAssembliesLeakIntoThePluginOutput()
		{
			IReadOnlyList<string> leaked = Directory
				.EnumerateFiles(TestEnvironment.PluginOutputDirectory, "*.dll")
				.Select(Path.GetFileName)
				.OfType<string>()
				.Where(x => x.StartsWith("HordeServer.", StringComparison.OrdinalIgnoreCase)
					|| x.StartsWith("EpicGames.", StringComparison.OrdinalIgnoreCase))
				.Where(x => !x.Equals(Probe.PluginFileName, StringComparison.OrdinalIgnoreCase))
				.ToList();

			Assert.AreEqual(0, leaked.Count,
				$"Engine assemblies were copied into the plugin output: {String.Join(", ", leaked)}. A <Reference> "
				+ "is missing its <Private>false</Private>.");
		}

		[TestMethod]
		public void DropIsASingleAssembly()
		{
			IReadOnlyList<string> assemblies = Directory
				.EnumerateFiles(TestEnvironment.PluginOutputDirectory, "*.dll")
				.Select(Path.GetFileName)
				.OfType<string>()
				.ToList();

			Assert.AreEqual(1, assemblies.Count,
				$"The drop is meant to be one file with no transitive dependencies, but the output contains: "
				+ $"{String.Join(", ", assemblies)}. A new package reference would have to be deployed alongside "
				+ "the plugin, and risks colliding with a different version already loaded by the server.");
		}

		[TestMethod]
		public void NothingButThePluginIsDeployedFromThisRepo()
		{
			// The server's plugin scan is a filename match - any top-level HordeServer.*.dll in its app directory is
			// loaded and reflected over (ServerApp.CreatePluginCollection). This assembly matches that pattern, as
			// Epic's own test assemblies do, and is harmless only because it is never deployed. Copying a build
			// output folder rather than the single file is the way that stops being true: the server catches the
			// resulting load failure and continues, but it logs an error at every startup and nobody enjoys tracing
			// that back.
			string[] expected = [Probe.PluginFileName];

			IReadOnlyList<string> ours = Directory
				.EnumerateFiles(TestEnvironment.HordeBinDir!, "HordeServer.*.dll")
				.Select(Path.GetFileName)
				.OfType<string>()
				.Where(x => x.StartsWith("HordeServer.Discord", StringComparison.OrdinalIgnoreCase))
				.Where(x => !expected.Contains(x, StringComparer.OrdinalIgnoreCase))
				.ToList();

			Assert.AreEqual(0, ours.Count,
				$"These were found beside the server and should not have been: {String.Join(", ", ours)}. Deploy "
				+ $"{Probe.PluginFileName} on its own, not the contents of a bin directory.");
		}
	}
}

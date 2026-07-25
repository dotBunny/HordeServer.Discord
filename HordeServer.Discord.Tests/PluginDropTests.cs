// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

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
	}
}

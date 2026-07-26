// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Reflection;
using System.Runtime.Loader;

namespace PluginProbe
{
	/// <summary>
	/// Loads one plugin assembly from an exact path, in isolation from whatever else the host process has loaded.
	/// </summary>
	/// <remarks>
	/// The probe has to be certain it is inspecting the DLL sitting in the server directory. <c>Assembly.LoadFrom</c>
	/// is not: the default context resolves by assembly identity, so if a copy of <c>HordeServer.Discord.dll</c> has
	/// already been loaded from somewhere else - which is exactly what happens when the test assembly references the
	/// plugin project so it can unit test its internals - it hands that one back and the probe silently verifies a
	/// build that was never deployed.
	///
	/// Only the plugin is isolated. <see cref="Load"/> returns null for everything else, which sends the runtime to
	/// the default context for Horde's assemblies. That matters: types like <c>PluginAttribute</c> and
	/// <c>INotificationSink</c> have to be the *same* types the probe itself compiled against, or every
	/// <c>IsAssignableFrom</c> check would quietly return false.
	/// </remarks>
	public sealed class PluginLoadContext : AssemblyLoadContext
	{
		readonly string _assemblyPath;
		readonly string _assemblySimpleName;

		/// <summary>
		/// Constructor.
		/// </summary>
		/// <param name="assemblyPath">Full path of the assembly to load in isolation.</param>
		public PluginLoadContext(string assemblyPath)
			: base(name: $"PluginProbe:{Path.GetFileName(assemblyPath)}", isCollectible: false)
		{
			_assemblyPath = assemblyPath;
			_assemblySimpleName = Path.GetFileNameWithoutExtension(assemblyPath);
		}

		/// <summary>
		/// Loads the isolated assembly.
		/// </summary>
		/// <returns>The assembly, loaded from the exact path this context was constructed with.</returns>
		public Assembly LoadPlugin() => LoadFromAssemblyPath(_assemblyPath);

		/// <inheritdoc/>
		protected override Assembly? Load(AssemblyName assemblyName)
			=> String.Equals(assemblyName.Name, _assemblySimpleName, StringComparison.OrdinalIgnoreCase)
				? LoadFromAssemblyPath(_assemblyPath)
				: null;
	}
}

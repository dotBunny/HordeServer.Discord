// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Reflection;
using System.Runtime.Loader;

namespace PluginProbe
{
	/// <summary>
	/// Teaches the default <see cref="AssemblyLoadContext"/> to find Horde's assemblies in a built server output
	/// directory.
	/// </summary>
	/// <remarks>
	/// The real server never needs this - it *is* the application that owns those assemblies, so its own deps.json
	/// resolves them. Anything else that loads Horde types, meaning this probe and the test host that runs it, has
	/// to opt in.
	///
	/// Install it before any Horde type is touched. The JIT resolves the types a method references when it compiles
	/// that method, so a caller that mentions a Horde type in the same method as the <see cref="Install"/> call will
	/// fail before the handler is ever attached. Callers keep the two apart: the console tool with a
	/// <see cref="System.Runtime.CompilerServices.MethodImplOptions.NoInlining"/> split, the test assembly with a
	/// module initializer.
	/// </remarks>
	public static class EngineAssemblyResolver
	{
		static readonly List<string> s_searchDirectories = new List<string>();
		static bool s_installed;

		/// <summary>
		/// Adds a directory to the search path, attaching the resolve handler on first use.
		/// </summary>
		/// <param name="appDir">A built Horde server output directory.</param>
		public static void Install(string appDir)
		{
			lock (s_searchDirectories)
			{
				if (!s_searchDirectories.Contains(appDir, StringComparer.OrdinalIgnoreCase))
				{
					s_searchDirectories.Add(appDir);
				}

				if (!s_installed)
				{
					AssemblyLoadContext.Default.Resolving += OnResolving;
					s_installed = true;
				}
			}
		}

		static Assembly? OnResolving(AssemblyLoadContext context, AssemblyName name)
		{
			if (name.Name == null)
			{
				return null;
			}

			string[] searchDirectories;
			lock (s_searchDirectories)
			{
				searchDirectories = s_searchDirectories.ToArray();
			}

			foreach (string searchDirectory in searchDirectories)
			{
				string candidate = Path.Combine(searchDirectory, name.Name + ".dll");
				if (File.Exists(candidate))
				{
					return context.LoadFromAssemblyPath(candidate);
				}
			}

			return null;
		}
	}
}

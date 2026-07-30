// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Reflection;
using System.Runtime.InteropServices;
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
	///
	/// Two handlers are needed, because some of those assemblies are only a managed wrapper. <c>EpicGames.IoHash</c>
	/// p/invokes <c>blake3_dotnet</c>, which the server ships under <c>runtimes/{rid}/native</c> rather than beside
	/// the managed DLLs - a layout only its deps.json knows how to read. Resolving the wrapper without the native
	/// library it calls fails later and further away, as a <see cref="DllNotFoundException"/> from whichever engine
	/// method happened to hash something.
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
					AssemblyLoadContext.Default.ResolvingUnmanagedDll += OnResolvingUnmanagedDll;
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

		static IntPtr OnResolvingUnmanagedDll(Assembly requesting, string name)
		{
			string[] searchDirectories;
			lock (s_searchDirectories)
			{
				searchDirectories = s_searchDirectories.ToArray();
			}

			foreach (string searchDirectory in searchDirectories)
			{
				foreach (string candidate in GetNativeCandidates(searchDirectory, name))
				{
					if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr handle))
					{
						return handle;
					}
				}
			}

			return IntPtr.Zero;
		}

		/// <summary>
		/// Paths a native library may occupy in a built server output, most specific first.
		/// </summary>
		/// <remarks>
		/// The runtime identifier is matched exactly rather than by scanning <c>runtimes</c> for anything of the right
		/// name. A win-arm64 binary sits beside the win-x64 one and loading the wrong architecture is a worse failure
		/// than not loading at all, because it surfaces as a BadImageFormatException from deep inside the engine.
		/// </remarks>
		static IEnumerable<string> GetNativeCandidates(string searchDirectory, string name)
		{
			string fileName = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : name + ".dll";
			string rid = RuntimeInformation.RuntimeIdentifier;

			yield return Path.Combine(searchDirectory, "runtimes", rid, "native", fileName);
			yield return Path.Combine(searchDirectory, fileName);
		}
	}
}

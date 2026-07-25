// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Reflection;

namespace PluginProbe
{
	/// <summary>
	/// Finds the built Horde server output directory to probe.
	/// </summary>
	/// <remarks>
	/// Mirrors the resolution order in <c>Directory.Build.props</c> so a machine that can build the plugin can
	/// also run the probe with no arguments. The build-time value is baked into the calling assembly rather than
	/// hardcoded in source: the path is machine-specific and publishing one would be wrong.
	/// </remarks>
	public static class HordeBinDirLocator
	{
		/// <summary>
		/// Key of the <see cref="AssemblyMetadataAttribute"/> the csproj bakes the build-time path into.
		/// </summary>
		public const string MetadataKey = "HordeBinDir";

		/// <summary>
		/// Environment variable consulted when nothing was baked in.
		/// </summary>
		public const string EnvironmentVariable = "HORDE_BIN_DIR";

		/// <summary>
		/// Explains how to supply the path, for callers reporting a failure to resolve one.
		/// </summary>
		public const string NotFoundMessage =
			"No Horde server directory to scan. Pass one explicitly, set HORDE_BIN_DIR, or create Horde.local.props "
			+ "(see Horde.local.props.template).";

		/// <summary>
		/// Resolves the directory to probe, from an explicit path, then the calling assembly's build-time value,
		/// then the environment.
		/// </summary>
		/// <param name="assembly">Assembly to read the baked-in build-time path from.</param>
		/// <param name="explicitPath">Path supplied by the caller, which wins if set.</param>
		/// <returns>The directory, or null if none of the sources supplied one.</returns>
		public static string? Resolve(Assembly assembly, string? explicitPath = null)
		{
			if (!String.IsNullOrWhiteSpace(explicitPath))
			{
				return explicitPath;
			}

			string? buildTimePath = assembly
				.GetCustomAttributes<AssemblyMetadataAttribute>()
				.FirstOrDefault(x => x.Key == MetadataKey)?.Value;

			if (!String.IsNullOrWhiteSpace(buildTimePath))
			{
				return buildTimePath;
			}

			string? environmentPath = Environment.GetEnvironmentVariable(EnvironmentVariable);
			return String.IsNullOrWhiteSpace(environmentPath) ? null : environmentPath;
		}
	}
}

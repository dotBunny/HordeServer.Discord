// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using EpicGames.Horde.Acls;
using EpicGames.Horde.Issues;
using EpicGames.Horde.Jobs.Templates;
using EpicGames.Horde.Projects;
using EpicGames.Horde.Streams;
using HordeServer;
using HordeServer.Acls;
using HordeServer.Configuration;
using HordeServer.Issues;
using HordeServer.Plugins;
using HordeServer.Projects;
using HordeServer.Streams;

namespace HordeTestDoubles
{
	/// <summary>
	/// Builds the <c>BuildConfig</c> that issue routing reads.
	/// </summary>
	/// <remarks>
	/// Issue triage is decided entirely by stream, workflow and template configuration, so nothing about it can be
	/// tested against the empty <c>BuildConfig</c> the tests used to pass in - which is exactly why the plugin
	/// diverged from Epic's routing unnoticed.
	///
	/// The awkward part is <c>BuildConfig.TryGetStream</c>, which reads a private lookup that only
	/// <c>PostLoad</c> fills. So this builds real <c>ProjectConfig</c>/<c>StreamConfig</c> objects and calls
	/// <c>PostLoad</c> for real. <c>StreamConfig.TryGetWorkflow</c> and <c>TryGetTemplate</c> need no such help -
	/// both are a <c>FirstOrDefault</c> over a public list.
	/// </remarks>
	public static class BuildConfigFakes
	{
		/// <summary>
		/// A build configuration containing the given streams.
		/// </summary>
		/// <param name="streams">Streams to publish, normally from <see cref="Stream"/>.</param>
		/// <returns>A configuration whose stream lookup is populated.</returns>
		public static BuildConfig With(params StreamConfig[] streams)
		{
			ProjectConfig project = new ProjectConfig { Id = new ProjectId("dethol"), Name = "DETHOL" };
			project.Streams.AddRange(streams);

			BuildConfig config = new BuildConfig();
			config.Projects.Add(project);

			// The ComputeConfig is not optional despite the comment in BuildConfig.UpdateWorkspacesForPools saying it
			// skips when absent - the code is a First(), not a FirstOrDefault(), so leaving it out throws
			// "Sequence contains no elements" from deep inside PostLoad.
			config.PostLoad(new PluginConfigOptions(ConfigVersion.Latest, [new ComputeConfig()], new AclConfig()));

			return config;
		}

		/// <summary>
		/// One stream, with an optional triage channel of its own.
		/// </summary>
		/// <param name="id">Stream id.</param>
		/// <param name="triageChannel">Channel the stream triages to, if any.</param>
		/// <returns>The stream, ready for <see cref="Workflow"/> and <see cref="Template"/>.</returns>
		public static StreamConfig Stream(string id, string? triageChannel = null)
			=> new StreamConfig { Id = new StreamId(id), Name = id, TriageChannel = triageChannel };

		/// <summary>
		/// Adds a workflow to a stream.
		/// </summary>
		/// <param name="stream">Stream to add it to.</param>
		/// <param name="id">Workflow id, matching what a step's annotations name.</param>
		/// <param name="triageChannel">Channel this workflow triages to.</param>
		/// <param name="triageAlias">Alias pinged for an unassigned issue.</param>
		/// <param name="triageErrors">Whether errors are triaged. Horde defaults this to true.</param>
		/// <param name="triageWarnings">Whether warnings are triaged. Horde defaults this to true.</param>
		/// <returns>The same stream, for chaining.</returns>
		public static StreamConfig Workflow(this StreamConfig stream, string id, string? triageChannel = null, string? triageAlias = null, bool triageErrors = true, bool triageWarnings = true)
		{
			stream.Workflows.Add(new WorkflowConfig
			{
				Id = new WorkflowId(id),
				TriageChannel = triageChannel,
				TriageAlias = triageAlias,
				TriageErrors = triageErrors,
				TriageWarnings = triageWarnings,
			});

			return stream;
		}

		/// <summary>
		/// Adds a template to a stream.
		/// </summary>
		/// <param name="stream">Stream to add it to.</param>
		/// <param name="id">Template id, matching a span's <c>TemplateRefId</c>.</param>
		/// <param name="triageChannel">Channel this template triages to, overriding the stream's.</param>
		/// <returns>The same stream, for chaining.</returns>
		public static StreamConfig Template(this StreamConfig stream, string id, string? triageChannel = null)
		{
			stream.Templates.Add(new TemplateRefConfig { Id = new TemplateId(id), Name = id, TriageChannel = triageChannel });
			return stream;
		}
	}
}

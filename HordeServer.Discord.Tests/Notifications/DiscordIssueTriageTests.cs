// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Text.Json;
using EpicGames.Horde.Users;
using HordeServer.Configuration;
using HordeServer.Discord.Client;
using HordeServer.Discord.Notifications;
using HordeServer.Discord.Tests.Client;
using HordeServer.Users;
using HordeTestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HordeServer.Discord.Tests.Notifications
{
	/// <summary>
	/// Tests for what a triage button actually changes.
	/// </summary>
	/// <remarks>
	/// Asserted against <see cref="FakeHordeIssues"/> rather than Horde's own <c>IssueService</c>, which reaches
	/// MongoDB in its constructor. What matters here is that the right operation is called, attributed to the right
	/// person, carrying the values the operator typed - the adapter that turns those into service calls is four
	/// lines per method and is the one thing in the plugin with no coverage.
	/// </remarks>
	[TestClass]
	public sealed class DiscordIssueTriageTests
	{
		const string AdaEmail = "ada@example.com";
		const string AdaDiscordId = "200000000000000001";
		const string GuildId = "600000000000000001";
		const string ApplicationId = "300000000000000001";

		#region Attribution

		[TestMethod]
		public async Task AcknowledgingInAChannelClaimsTheIssue()
		{
			Harness harness = new Harness();
			harness.Issues.Add(IssueFakes.Issue(42, "Compile error"));

			await harness.PressAsync("ack");

			Assert.AreEqual("acknowledge", harness.Issues.Last.Verb);
			Assert.AreEqual(42, harness.Issues.Last.IssueId);
			Assert.AreEqual(harness.Ada.Id, harness.Issues.Last.UserId);
			Assert.IsTrue(harness.Issues.Last.TakeOwnership,
				"Pressing Acknowledge in a channel is how an unowned issue gets claimed.");
		}

		[TestMethod]
		public async Task AcknowledgingADirectMessageDoesNotReassignIt()
		{
			Harness harness = new Harness();
			harness.Issues.Add(IssueFakes.Issue(42, "Compile error"));

			await harness.PressAsync("ack", inChannel: false);

			Assert.IsFalse(harness.Issues.Last.TakeOwnership,
				"A DM went to the person it is already about, so acknowledging it means 'yes, mine' rather than a "
				+ "reassignment. Slack keeps two handlers for the same distinction.");
		}

		[TestMethod]
		public async Task AnUnmappedPresserIsToldRatherThanIgnored()
		{
			Harness harness = new Harness(mapUser: false);
			harness.Issues.Add(IssueFakes.Issue(42, "Compile error"));

			await harness.PressAsync("ack");

			Assert.AreEqual(0, harness.Issues.Updates.Count, "Nothing can be recorded against an unknown user.");

			StringAssert.Contains(harness.LastFollowUpContent(), "userMap",
				"They are looking at the button waiting for it to do something.");
			Assert.AreEqual(DiscordMessageFlags.Ephemeral, harness.LastFollowUpFlags(),
				"And the rest of the channel does not need to hear about it.");
		}

		[TestMethod]
		public async Task DecliningIsRecordedAgainstThePresser()
		{
			Harness harness = new Harness();
			harness.Issues.Add(IssueFakes.Issue(42, "Compile error"));

			await harness.PressAsync("decline");

			Assert.AreEqual("decline", harness.Issues.Last.Verb);
			Assert.AreEqual(harness.Ada.Id, harness.Issues.Last.UserId);
		}

		#endregion

		#region Taking an owned issue

		[TestMethod]
		public async Task ClaimingSomebodyElsesIssueAsksFirst()
		{
			Harness harness = new Harness();

			FakeIssue issue = harness.Issues.Add(IssueFakes.Issue(42, "Compile error"));
			issue.OwnerId = HordeFakes.User("Grace Hopper", "grace@example.com").Id;

			await harness.PressAsync("ack");

			Assert.AreEqual(0, harness.Issues.Updates.Count, "Taking an issue off somebody silently would be wrong.");
			StringAssert.Contains(harness.LastFollowUpContent(), "currently assigned to");
		}

		[TestMethod]
		public async Task AndTakesItWhenConfirmed()
		{
			Harness harness = new Harness();

			FakeIssue issue = harness.Issues.Add(IssueFakes.Issue(42, "Compile error"));
			issue.OwnerId = HordeFakes.User("Grace Hopper", "grace@example.com").Id;

			await harness.PressAsync("claim");

			Assert.AreEqual("acknowledge", harness.Issues.Last.Verb);
			Assert.IsTrue(harness.Issues.Last.TakeOwnership);
		}

		[TestMethod]
		public async Task AnIssueAlreadyOwnedByThePresserNeedsNoConfirmation()
		{
			Harness harness = new Harness();

			FakeIssue issue = harness.Issues.Add(IssueFakes.Issue(42, "Compile error"));
			issue.OwnerId = harness.Ada.Id;

			await harness.PressAsync("ack");

			Assert.AreEqual("acknowledge", harness.Issues.Last.Verb);
		}

		#endregion

		#region Mark Fixed

		[TestMethod]
		public async Task MarkFixedOpensAModalAndNothingElse()
		{
			Harness harness = new Harness();
			harness.Issues.Add(IssueFakes.Issue(42, "Compile error"));

			await harness.PressAsync("markfixed");

			Assert.AreEqual(0, harness.Issues.Updates.Count, "Nothing is fixed until the modal comes back.");
			Assert.AreEqual(1, harness.Handler.Requests.Count,
				"A modal has to be the only response - Discord will not attach one to an interaction already "
				+ "answered, which is why this verb answers for itself.");
			Assert.AreEqual(DiscordInteractionCallbackType.Modal, harness.Handler.Message(0).GetProperty("type").GetInt32());
		}

		[TestMethod]
		public void OnlyMarkFixedGivesUpItsDeferral()
		{
			Assert.IsTrue(DiscordIssueTriage.AnswersForItself(new DiscordCustomId(DiscordCustomId.IssueScope, "42", "markfixed")));
			Assert.IsFalse(DiscordIssueTriage.AnswersForItself(new DiscordCustomId(DiscordCustomId.IssueScope, "42", "ack")));
			Assert.IsFalse(DiscordIssueTriage.AnswersForItself(new DiscordCustomId(DiscordCustomId.IssueScope, "42", "fixsubmit")));
		}

		[TestMethod]
		public async Task SubmittingTheModalResolvesTheIssueWithWhatWasTyped()
		{
			Harness harness = new Harness();
			harness.Issues.Add(IssueFakes.Issue(42, "Compile error"));

			await harness.SubmitAsync(new Dictionary<string, string>
			{
				[DiscordIssueTriage.FixCommitField] = "12345",
				[DiscordIssueTriage.RootCauseSummaryField] = "Bad merge",
				[DiscordIssueTriage.RootCauseCommitField] = "12000",
				[DiscordIssueTriage.DuplicateIssueField] = "41",
			});

			HordeIssueUpdate update = harness.Issues.Last;

			Assert.AreEqual("resolve", update.Verb);
			Assert.AreEqual("12345", update.FixCommitId?.ToString());
			Assert.AreEqual("Bad merge", update.RootCauseSummary);
			Assert.AreEqual("12000", update.RootCauseCommitId?.ToString());
			Assert.AreEqual(41, update.DuplicateIssueId);
		}

		[TestMethod]
		public async Task BlankOptionalFieldsBecomeNothingRatherThanEmptyStrings()
		{
			Harness harness = new Harness();
			harness.Issues.Add(IssueFakes.Issue(42, "Compile error"));

			await harness.SubmitAsync(new Dictionary<string, string>
			{
				[DiscordIssueTriage.FixCommitField] = "12345",
				[DiscordIssueTriage.RootCauseSummaryField] = "   ",
				[DiscordIssueTriage.RootCauseCommitField] = "",
				[DiscordIssueTriage.DuplicateIssueField] = "not a number",
			});

			HordeIssueUpdate update = harness.Issues.Last;

			Assert.IsNull(update.RootCauseSummary);
			Assert.IsNull(update.RootCauseCommitId);
			Assert.IsNull(update.DuplicateIssueId,
				"Rejecting the whole submission because somebody typed prose into an optional box would lose the "
				+ "fix along with it.");
			Assert.AreEqual("12345", update.FixCommitId?.ToString());
		}

		[TestMethod]
		public async Task ARootCauseSummaryEarnsTheCategoryDropdown()
		{
			Harness harness = new Harness();
			harness.Issues.Add(IssueFakes.Issue(42, "Compile error"));

			await harness.SubmitAsync(new Dictionary<string, string>
			{
				[DiscordIssueTriage.FixCommitField] = "12345",
				[DiscordIssueTriage.RootCauseSummaryField] = "Bad merge",
			});

			JsonElement followUp = harness.LastFollowUp();

			Assert.AreEqual(DiscordMessageFlags.Ephemeral, followUp.GetProperty("flags").GetInt32());

			JsonElement select = followUp.GetProperty("components")[0].GetProperty("components")[0];

			Assert.AreEqual(DiscordComponentType.StringSelect, select.GetProperty("type").GetInt32());
			Assert.AreEqual("issue_42_category", select.GetProperty("custom_id").GetString());
			Assert.AreEqual(DiscordIssueTriage.RootCauseCategories.Count, select.GetProperty("options").GetArrayLength());
		}

		[TestMethod]
		public async Task NoSummaryMeansNoFurtherQuestions()
		{
			Harness harness = new Harness();
			harness.Issues.Add(IssueFakes.Issue(42, "Compile error"));

			await harness.SubmitAsync(new Dictionary<string, string>
			{
				[DiscordIssueTriage.FixCommitField] = "12345",
			});

			Assert.IsFalse(harness.Handler.Requests.Any(x => x.Uri.EndsWith($"webhooks/{ApplicationId}/interaction-token", StringComparison.Ordinal)),
				"Closing out a fix stays a single interaction on the common path. That is the whole argument for "
				+ "the hybrid flow.");
		}

		[TestMethod]
		public async Task ChoosingACategoryRecordsIt()
		{
			Harness harness = new Harness();
			harness.Issues.Add(IssueFakes.Issue(42, "Compile error"));

			await harness.SelectAsync("Content");

			Assert.AreEqual("category", harness.Issues.Last.Verb);
			Assert.AreEqual("Content", harness.Issues.Last.Category);
		}

		#endregion

		#region Failure

		[TestMethod]
		public async Task AnUpdateHordeRefusesIsReportedBack()
		{
			Harness harness = new Harness();
			harness.Issues.Add(IssueFakes.Issue(42, "Compile error"));
			harness.Issues.Succeeds = false;

			await harness.PressAsync("decline");

			StringAssert.Contains(harness.LastFollowUpContent(), "would not accept");
		}

		[TestMethod]
		public async Task AVerbNobodyRecognisesChangesNothing()
		{
			Harness harness = new Harness();
			harness.Issues.Add(IssueFakes.Issue(42, "Compile error"));

			await harness.PressAsync("frobnicate");

			Assert.AreEqual(0, harness.Issues.Updates.Count);
		}

		[TestMethod]
		public async Task AComponentThatDoesNotNameAnIssueChangesNothing()
		{
			Harness harness = new Harness();

			await harness.HandleAsync(new DiscordCustomId(DiscordCustomId.IssueScope, "notanumber", "ack"), true, null, null);

			Assert.AreEqual(0, harness.Issues.Updates.Count);
		}

		#endregion

		sealed class Harness
		{
			public Harness(bool mapUser = true)
			{
				Ada = HordeFakes.User("Ada Lovelace", AdaEmail);

				FakeUserCollection users = new FakeUserCollection();
				users.Add(Ada);

				DiscordServerConfig serverConfig = new DiscordServerConfig
				{
					BotToken = "token",
					ApplicationId = ApplicationId,
					GuildId = GuildId,
					JobNotificationChannel = "100000000000000006",
					ErrorPrefix = String.Empty,
					WarningPrefix = String.Empty,
				};

				DiscordConfig pluginConfig = new DiscordConfig();

				if (mapUser)
				{
					pluginConfig.UserMap[AdaEmail] = AdaDiscordId;
				}

				pluginConfig.PostLoad(new Plugins.PluginConfigOptions(
					ConfigVersion.Latest,
					Array.Empty<Plugins.IPluginConfig>(),
					new Acls.AclConfig(),
					NullLogger<DiscordConfig>.Instance));

				IOptions<DiscordServerConfig> options = Options.Create(serverConfig);
				IOptions<BuildServerConfig> buildServerConfig = Options.Create(new BuildServerConfig());
				StaticOptionsMonitor<DiscordConfig> config = new StaticOptionsMonitor<DiscordConfig>(pluginConfig);

				Handler = new RecordingHttpHandler();

				DiscordClient client = new DiscordClient(
					new HttpClient(Handler) { BaseAddress = new Uri(DiscordClient.ApiBaseUrl) },
					options,
					new DiscordRateLimiter(NullLogger.Instance, new FakeDiscordClock()),
					NullLogger<DiscordClient>.Instance);

				DiscordUserResolver discordUsers = new DiscordUserResolver(config, NullLogger<DiscordUserResolver>.Instance);

				DiscordNotificationProcessor processor = new DiscordNotificationProcessor(
					client,
					new DiscordChannelResolver(config, options, buildServerConfig, NullLogger<DiscordChannelResolver>.Instance),
					discordUsers,
					new DiscordRepeatFilter(new FakeDiscordClock()),
					options,
					buildServerConfig,
					new StaticOptionsMonitor<BuildConfig>(new BuildConfig()),
					users,
					new FakeServerInfo(),
					NullLogger<DiscordNotificationProcessor>.Instance);

				DiscordGateway gateway = new DiscordGateway(options, client, NullLogger<DiscordGateway>.Instance);
				Router = new DiscordInteractionRouter(gateway, client, options, NullLogger<DiscordInteractionRouter>.Instance);

				Triage = new DiscordIssueTriage(
					Router, Issues, discordUsers, users, processor, NullLogger<DiscordIssueTriage>.Instance);
			}

			public IUser Ada { get; }

			public FakeHordeIssues Issues { get; } = new FakeHordeIssues();

			public RecordingHttpHandler Handler { get; }

			public DiscordInteractionRouter Router { get; }

			public DiscordIssueTriage Triage { get; }

			/// <summary>
			/// Presses a button on an issue message.
			/// </summary>
			public Task PressAsync(string verb, bool inChannel = true)
				=> HandleAsync(new DiscordCustomId(DiscordCustomId.IssueScope, "42", verb), inChannel, null, null);

			/// <summary>
			/// Submits the Mark Fixed modal.
			/// </summary>
			public Task SubmitAsync(IReadOnlyDictionary<string, string> values)
				=> HandleAsync(new DiscordCustomId(DiscordCustomId.IssueScope, "42", "fixsubmit"), true, values, null);

			/// <summary>
			/// Chooses from the category dropdown.
			/// </summary>
			public Task SelectAsync(string category)
				=> HandleAsync(new DiscordCustomId(DiscordCustomId.IssueScope, "42", "category"), true, null, category);

			public Task HandleAsync(DiscordCustomId customId, bool inChannel, IReadOnlyDictionary<string, string>? modalValues, string? selected)
			{
				DiscordInteraction interaction = new DiscordInteraction
				{
					Id = "400000000000000001",
					ApplicationId = ApplicationId,
					Type = modalValues == null ? DiscordInteractionType.MessageComponent : DiscordInteractionType.ModalSubmit,
					Token = "interaction-token",
					ChannelId = "500000000000000001",
					GuildId = inChannel ? GuildId : null,
					Data = new DiscordInteractionData
					{
						CustomId = customId.ToString(),
						Values = selected == null ? null : [selected],
						Components = modalValues == null ? default : ModalRows(modalValues),
					},
					Member = inChannel ? new DiscordInteractionMember { User = new DiscordInteractionUser { Id = AdaDiscordId } } : null,
					User = inChannel ? null : new DiscordInteractionUser { Id = AdaDiscordId },
					Message = new DiscordInteractionMessage { Id = "700000000000000001", ChannelId = "500000000000000001" },
				};

				return Triage.HandleAsync(new DiscordInteractionContext(interaction, customId, AdaDiscordId), default);
			}

			/// <summary>
			/// The body of the most recent followup, which is how triage speaks to one person.
			/// </summary>
			public JsonElement LastFollowUp()
			{
				for (int index = Handler.Requests.Count - 1; index >= 0; index--)
				{
					if (Handler.Requests[index].Uri.EndsWith($"webhooks/{ApplicationId}/interaction-token", StringComparison.Ordinal))
					{
						return Handler.Message(index);
					}
				}

				throw new AssertFailedException("No followup was posted.");
			}

			public string? LastFollowUpContent() => LastFollowUp().GetProperty("content").GetString();

			public int LastFollowUpFlags() => LastFollowUp().GetProperty("flags").GetInt32();

			static JsonElement ModalRows(IReadOnlyDictionary<string, string> values)
			{
				IEnumerable<string> rows = values.Select(x
					=> $$"""{"type":1,"components":[{"type":4,"custom_id":"{{x.Key}}","value":{{JsonSerializer.Serialize(x.Value)}} }]}""");

				return JsonDocument.Parse($"[{String.Join(',', rows)}]").RootElement.Clone();
			}
		}
	}
}

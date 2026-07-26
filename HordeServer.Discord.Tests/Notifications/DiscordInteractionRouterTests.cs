// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json;
using HordeServer.Discord.Client;
using HordeServer.Discord.Notifications;
using HordeServer.Discord.Tests.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HordeServer.Discord.Tests.Notifications
{
	/// <summary>
	/// Tests for what happens between a button being pressed and the work starting.
	/// </summary>
	/// <remarks>
	/// Almost all of this is about ordering. Discord gives three seconds to answer an interaction, and the whole
	/// design is that the answer goes out before anything that could take longer than that - so the assertions are
	/// mostly about what happened *first*.
	/// </remarks>
	[TestClass]
	public sealed class DiscordInteractionRouterTests
	{
		const string ApplicationId = "300000000000000001";
		const string PressedBy = "200000000000000001";

		[TestMethod]
		public async Task AnInteractionIsAcknowledgedBeforeTheHandlerRuns()
		{
			Harness harness = new Harness();

			string? acknowledgedFirst = null;

			harness.Router.Register(DiscordCustomId.IssueScope, (context, cancellationToken) =>
			{
				// Whatever the handler is going to do, the acknowledgement has already gone out by the time it runs.
				acknowledgedFirst = harness.Handler.Requests.Count > 0 ? harness.Handler.Requests[0].Uri : null;
				return Task.CompletedTask;
			});

			await harness.Router.HandleAsync(Interaction("issue_abc_ack"), default);

			StringAssert.Contains(acknowledgedFirst, "/callback",
				"The handler must never be what Discord is waiting on.");
		}

		[TestMethod]
		public async Task TheAcknowledgementDefersRatherThanReplying()
		{
			Harness harness = new Harness();
			harness.Router.Register(DiscordCustomId.IssueScope, (_, _) => Task.CompletedTask);

			await harness.Router.HandleAsync(Interaction("issue_abc_ack"), default);

			JsonElement body = harness.Handler.Message(0);

			Assert.AreEqual(DiscordInteractionCallbackType.DeferredUpdateMessage, body.GetProperty("type").GetInt32(),
				"A deferred *update* stops the button spinning and changes nothing. Anything else either posts a "
				+ "message nobody asked for or blanks the one the button is on.");
		}

		[TestMethod]
		public async Task TheHandlerIsToldWhoPressedIt()
		{
			Harness harness = new Harness();

			DiscordInteractionContext? seen = null;
			harness.Router.Register(DiscordCustomId.IssueScope, (context, _) =>
			{
				seen = context;
				return Task.CompletedTask;
			});

			await harness.Router.HandleAsync(Interaction("issue_abc_accept"), default);

			Assert.IsNotNull(seen);
			Assert.AreEqual(PressedBy, seen!.DiscordUserId);
			Assert.AreEqual("abc", seen.CustomId.Id);
			Assert.AreEqual("accept", seen.CustomId.Verb);
		}

		[TestMethod]
		public async Task AUserInADirectMessageIsIdentifiedToo()
		{
			Harness harness = new Harness();

			string? userId = null;
			harness.Router.Register(DiscordCustomId.IssueScope, (context, _) =>
			{
				userId = context.DiscordUserId;
				return Task.CompletedTask;
			});

			// A guild interaction carries member.user; a DM carries user. The DM copy of a notification has its own
			// buttons, so both paths matter.
			await harness.Router.HandleAsync(DirectMessageInteraction("issue_abc_ack"), default);

			Assert.AreEqual(PressedBy, userId);
		}

		[TestMethod]
		public async Task AHandlerThatThrowsDoesNotEscape()
		{
			Harness harness = new Harness();
			harness.Router.Register(DiscordCustomId.IssueScope, (_, _) => throw new InvalidOperationException("issue service is down"));

			// Nothing awaits the task this normally runs on, so an escaping exception would be unobserved rather
			// than reported.
			await harness.Router.HandleAsync(Interaction("issue_abc_ack"), default);
		}

		[TestMethod]
		public async Task AFailedAcknowledgementStopsTheWork()
		{
			Harness harness = new Harness(RecordingHttpHandler.Json(HttpStatusCode.NotFound, """{"message":"Unknown interaction","code":10062}"""));

			bool ran = false;
			harness.Router.Register(DiscordCustomId.IssueScope, (_, _) =>
			{
				ran = true;
				return Task.CompletedTask;
			});

			await harness.Router.HandleAsync(Interaction("issue_abc_ack"), default);

			Assert.IsFalse(ran,
				"Without an acknowledgement the token is useless, so the work would happen with no way to report it "
				+ "and the operator would press the button again.");
		}

		[TestMethod]
		public async Task AComponentFromAnotherBotIsIgnored()
		{
			Harness harness = new Harness();

			bool ran = false;
			harness.Router.Register(DiscordCustomId.IssueScope, (_, _) =>
			{
				ran = true;
				return Task.CompletedTask;
			});

			await harness.Router.HandleAsync(Interaction("someone-elses-button"), default);

			Assert.IsFalse(ran);
			Assert.AreEqual(0, harness.Handler.Requests.Count,
				"Not ours, so it must not be answered either - the bot that owns it is trying to.");
		}

		[TestMethod]
		public async Task AnUnregisteredScopeIsNotAcknowledged()
		{
			Harness harness = new Harness();

			await harness.Router.HandleAsync(Interaction("device_abc_release"), default);

			Assert.AreEqual(0, harness.Handler.Requests.Count,
				"Acknowledging something nothing will act on leaves the operator with a button that reports success "
				+ "and does nothing.");
		}

		[TestMethod]
		public async Task ASlashCommandIsLeftAloneForNow()
		{
			Harness harness = new Harness();
			harness.Router.Register(DiscordCustomId.IssueScope, (_, _) => Task.CompletedTask);

			DiscordInteraction interaction = Interaction("issue_abc_ack");
			interaction.Type = DiscordInteractionType.ApplicationCommand;

			await harness.Router.HandleAsync(interaction, default);

			Assert.AreEqual(0, harness.Handler.Requests.Count);
		}

		[TestMethod]
		public async Task TheMessageIsEditedThroughTheInteractionToken()
		{
			Harness harness = new Harness();

			harness.Router.Register(DiscordCustomId.IssueScope, async (context, cancellationToken) =>
				await harness.Router.UpdateMessageAsync(
					context,
					new DiscordMessageBuilder().WithContent("done").WithoutComponents().Build(),
					cancellationToken));

			await harness.Router.HandleAsync(Interaction("issue_abc_ack"), default);

			Assert.AreEqual(2, harness.Handler.Requests.Count);
			StringAssert.Contains(harness.Handler.Requests[1].Uri, $"webhooks/{ApplicationId}/interaction-token/messages/@original",
				"Editing through the token rather than the channel is what makes this work on a message the bot "
				+ "has no permission to edit.");
			Assert.AreEqual("PATCH", harness.Handler.Requests[1].Method);
		}

		#region Modals

		[TestMethod]
		public async Task AModalOpeningVerbIsNotPreAcknowledged()
		{
			Harness harness = new Harness();

			harness.Router.Register(
				DiscordCustomId.IssueScope,
				async (context, cancellationToken) => await harness.Router.RespondAsync(
					context,
					DiscordInteractionResponse.OpenModal(new DiscordModalBuilder(context.CustomId.ToString(), "Mark Fixed")
						.AddTextInput("fix_cl", "Fix CL", required: true)
						.Build()),
					cancellationToken),
				customId => customId.Verb == "markfixed");

			await harness.Router.HandleAsync(Interaction("issue_abc_markfixed"), default);

			Assert.AreEqual(1, harness.Handler.Requests.Count,
				"Exactly one response. A deferral followed by the modal would be two, and Discord refuses to attach "
				+ "a dialog to an interaction that has already been answered.");

			Assert.AreEqual(DiscordInteractionCallbackType.Modal, harness.Handler.Message(0).GetProperty("type").GetInt32());
		}

		[TestMethod]
		public async Task OtherVerbsInTheSameScopeAreStillPreAcknowledged()
		{
			Harness harness = new Harness();

			harness.Router.Register(
				DiscordCustomId.IssueScope,
				(_, _) => Task.CompletedTask,
				customId => customId.Verb == "markfixed");

			await harness.Router.HandleAsync(Interaction("issue_abc_ack"), default);

			Assert.AreEqual(DiscordInteractionCallbackType.DeferredUpdateMessage,
				harness.Handler.Message(0).GetProperty("type").GetInt32(),
				"The exemption is per verb, not per scope - only the one that opens a dialog gives up its deferral.");
		}

		[TestMethod]
		public async Task ASubmittedModalIsRoutedLikeAButton()
		{
			Harness harness = new Harness();

			IReadOnlyDictionary<string, string>? values = null;

			harness.Router.Register(DiscordCustomId.IssueScope, (context, _) =>
			{
				values = context.Interaction.GetModalValues();
				return Task.CompletedTask;
			});

			DiscordInteraction submission = Interaction("issue_abc_markfixed");
			submission.Type = DiscordInteractionType.ModalSubmit;
			submission.Data!.Components = JsonDocument.Parse("""
				[{"type":1,"components":[{"type":4,"custom_id":"fix_cl","value":"12345"}]}]
				""").RootElement.Clone();

			await harness.Router.HandleAsync(submission, default);

			Assert.IsNotNull(values);
			Assert.AreEqual("12345", values!["fix_cl"]);
		}

		[TestMethod]
		public async Task ASubmittedModalIsAcknowledgedBeforeTheFixIsApplied()
		{
			Harness harness = new Harness();

			int requestsWhenHandlerRan = -1;

			harness.Router.Register(DiscordCustomId.IssueScope, (_, _) =>
			{
				requestsWhenHandlerRan = harness.Handler.Requests.Count;
				return Task.CompletedTask;
			});

			DiscordInteraction submission = Interaction("issue_abc_markfixed");
			submission.Type = DiscordInteractionType.ModalSubmit;

			await harness.Router.HandleAsync(submission, default);

			Assert.AreEqual(1, requestsWhenHandlerRan,
				"Applying the fix is database work behind a service call, and the submission has the same three "
				+ "seconds a button press does.");
		}

		#endregion

		static DiscordInteraction Interaction(string customId)
			=> new DiscordInteraction
			{
				Id = "400000000000000001",
				ApplicationId = ApplicationId,
				Type = DiscordInteractionType.MessageComponent,
				Token = "interaction-token",
				ChannelId = "500000000000000001",
				GuildId = "600000000000000001",
				Data = new DiscordInteractionData { CustomId = customId, ComponentType = DiscordComponentType.Button },
				Member = new DiscordInteractionMember { User = new DiscordInteractionUser { Id = PressedBy, Username = "ada" } },
				Message = new DiscordInteractionMessage { Id = "700000000000000001", ChannelId = "500000000000000001" },
			};

		static DiscordInteraction DirectMessageInteraction(string customId)
		{
			DiscordInteraction interaction = Interaction(customId);

			interaction.GuildId = null;
			interaction.Member = null;
			interaction.User = new DiscordInteractionUser { Id = PressedBy, Username = "ada" };

			return interaction;
		}

		sealed class Harness
		{
			public Harness(params HttpResponseMessage[] responses)
			{
				DiscordServerConfig serverConfig = new DiscordServerConfig
				{
					BotToken = "bot-token",
					ApplicationId = ApplicationId,
					GuildId = "600000000000000001",
				};

				Handler = new RecordingHttpHandler(responses);

				DiscordClient client = new DiscordClient(
					new HttpClient(Handler) { BaseAddress = new Uri(DiscordClient.ApiBaseUrl) },
					Options.Create(serverConfig),
					new DiscordRateLimiter(NullLogger.Instance, new FakeDiscordClock()),
					NullLogger<DiscordClient>.Instance);

				DiscordGateway gateway = new DiscordGateway(
					Options.Create(serverConfig),
					client,
					NullLogger<DiscordGateway>.Instance);

				Router = new DiscordInteractionRouter(
					gateway, client, Options.Create(serverConfig), NullLogger<DiscordInteractionRouter>.Instance);
			}

			public RecordingHttpHandler Handler { get; }

			public DiscordInteractionRouter Router { get; }
		}
	}
}

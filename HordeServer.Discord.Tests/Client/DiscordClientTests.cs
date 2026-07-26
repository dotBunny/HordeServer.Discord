// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Net;
using System.Text.Json;
using HordeServer.Discord.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HordeServer.Discord.Tests.Client
{
	/// <summary>
	/// Tests for what the client actually puts on the wire, and what it does with what comes back.
	/// </summary>
	[TestClass]
	public sealed class DiscordClientTests
	{
		[TestMethod]
		public async Task ApiVersionIsPinnedInThePath()
		{
			RecordingHttpHandler handler = new RecordingHttpHandler(Json(HttpStatusCode.OK, """{"id":"9","channel_id":"5"}"""));
			DiscordClient client = Create(handler);

			await client.CreateMessageAsync("5", new DiscordMessageBuilder().WithContent("hi").Build(), CancellationToken.None);

			Assert.AreEqual("https://discord.com/api/v10/channels/5/messages", handler.Requests[0].Uri,
				"An unversioned request does not get the current API - it routes to v6, which is deprecated.");
		}

		[TestMethod]
		public async Task BotTokenIsSentWithTheBotPrefix()
		{
			RecordingHttpHandler handler = new RecordingHttpHandler(Json(HttpStatusCode.OK, """{"id":"9"}"""));
			DiscordClient client = Create(handler, botToken: "abc123");

			await client.CreateMessageAsync("5", new DiscordMessageBuilder().WithContent("hi").Build(), CancellationToken.None);

			Assert.AreEqual("Bot abc123", handler.Requests[0].Authorization,
				"A bare token is read as a user token and rejected.");
		}

		[TestMethod]
		public async Task CreateMessageReturnsSomethingEditable()
		{
			RecordingHttpHandler handler = new RecordingHttpHandler(Json(HttpStatusCode.OK, """{"id":"999","channel_id":"5"}"""));
			DiscordClient client = Create(handler);

			DiscordMessageReference? reference = await client.CreateMessageAsync("5", new DiscordMessageBuilder().Build(), CancellationToken.None);

			Assert.IsNotNull(reference);
			Assert.AreEqual("999", reference.MessageId);
			Assert.AreEqual("5", reference.ChannelId);
		}

		[TestMethod]
		public async Task PayloadCarriesContentEmbedsAndMentionPolicy()
		{
			RecordingHttpHandler handler = new RecordingHttpHandler(Json(HttpStatusCode.OK, """{"id":"9"}"""));
			DiscordClient client = Create(handler);

			DiscordMessage message = new DiscordMessageBuilder()
				.WithContent("Build failed")
				.AddEmbed(new DiscordEmbedBuilder().WithTitle("Compile").AddField("Stream", "dethol-main", true))
				.Build();

			await client.CreateMessageAsync("5", message, CancellationToken.None);

			using JsonDocument document = JsonDocument.Parse(handler.Requests[0].Body!);
			JsonElement root = document.RootElement;

			Assert.AreEqual("Build failed", root.GetProperty("content").GetString());
			Assert.AreEqual("Compile", root.GetProperty("embeds")[0].GetProperty("title").GetString());
			Assert.AreEqual("Stream", root.GetProperty("embeds")[0].GetProperty("fields")[0].GetProperty("name").GetString());
			Assert.AreEqual(0, root.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
		}

		[TestMethod]
		public async Task NullPropertiesAreLeftOutOfThePayload()
		{
			RecordingHttpHandler handler = new RecordingHttpHandler(Json(HttpStatusCode.OK, """{"id":"9"}"""));
			DiscordClient client = Create(handler);

			await client.CreateMessageAsync("5", new DiscordMessageBuilder().WithContent("text only").Build(), CancellationToken.None);

			using JsonDocument document = JsonDocument.Parse(handler.Requests[0].Body!);

			Assert.IsFalse(document.RootElement.TryGetProperty("embeds", out _),
				"Sending embeds:null is not the same as sending no embeds, and Discord rejects the former.");
		}

		[TestMethod]
		public async Task EditUsesPatchAgainstTheMessagePath()
		{
			RecordingHttpHandler handler = new RecordingHttpHandler(new HttpResponseMessage(HttpStatusCode.OK));
			DiscordClient client = Create(handler);

			bool edited = await client.EditMessageAsync(new DiscordMessageReference("5", "999"), new DiscordMessageBuilder().Build(), CancellationToken.None);

			Assert.IsTrue(edited);
			Assert.AreEqual("PATCH", handler.Requests[0].Method);
			Assert.AreEqual("https://discord.com/api/v10/channels/5/messages/999", handler.Requests[0].Uri);
		}

		[TestMethod]
		public async Task FailureIsReportedRatherThanThrown()
		{
			RecordingHttpHandler handler = new RecordingHttpHandler(Json(HttpStatusCode.Forbidden, """{"message":"Missing Permissions","code":50013}"""));
			DiscordClient client = Create(handler);

			DiscordMessageReference? reference = await client.CreateMessageAsync("5", new DiscordMessageBuilder().Build(), CancellationToken.None);

			Assert.IsNull(reference,
				"An exception escaping the sink would still be swallowed by the notification service, just without "
				+ "anything useful in the log.");
		}

		[TestMethod]
		public async Task ThrottledRequestIsRetriedByTheLimiter()
		{
			RecordingHttpHandler handler = new RecordingHttpHandler(
				Throttled("0.5"),
				Json(HttpStatusCode.OK, """{"id":"9","channel_id":"5"}"""));

			FakeDiscordClock clock = new FakeDiscordClock();
			DiscordClient client = Create(handler, clock: clock);

			DiscordMessageReference? reference = await client.CreateMessageAsync("5", new DiscordMessageBuilder().Build(), CancellationToken.None);

			Assert.IsNotNull(reference);
			Assert.AreEqual(2, handler.Requests.Count, "Each attempt has to build a fresh request message.");
			CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(0.5) }, clock.Delays);
		}

		[TestMethod]
		public async Task MalformedResponseDoesNotThrow()
		{
			RecordingHttpHandler handler = new RecordingHttpHandler(Json(HttpStatusCode.OK, "not json at all"));
			DiscordClient client = Create(handler);

			DiscordMessageReference? reference = await client.CreateMessageAsync("5", new DiscordMessageBuilder().Build(), CancellationToken.None);

			Assert.IsNull(reference);
		}

		static DiscordClient Create(RecordingHttpHandler handler, string botToken = "token", IDiscordClock? clock = null)
		{
			DiscordServerConfig serverConfig = new DiscordServerConfig { BotToken = botToken };
			DiscordRateLimiter rateLimiter = new DiscordRateLimiter(NullLogger.Instance, clock ?? new FakeDiscordClock());

			return new DiscordClient(new HttpClient(handler), Options.Create(serverConfig), rateLimiter, NullLogger<DiscordClient>.Instance);
		}

		static HttpResponseMessage Json(HttpStatusCode statusCode, string body)
			=> RecordingHttpHandler.Json(statusCode, body);

		static HttpResponseMessage Throttled(string resetAfter)
		{
			HttpResponseMessage response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
			response.Headers.TryAddWithoutValidation("X-RateLimit-Reset-After", resetAfter);
			return response;
		}
	}
}

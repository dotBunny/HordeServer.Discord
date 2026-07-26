// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json;

namespace HordeServer.Discord.Tests.Client
{
	/// <summary>
	/// Records what was sent and hands back canned responses in order.
	/// </summary>
	/// <remarks>
	/// The seam the whole plugin is testable through. Everything the plugin says to Discord ends up here as a JSON
	/// body, so a test can assert on the message that would actually have been posted rather than on an intermediate
	/// object that might not survive serialisation. Requests past the end of the canned list get a bare 200, which is
	/// what makes "send this report and tell me what came out" a one-line setup.
	/// </remarks>
	sealed class RecordingHttpHandler : HttpMessageHandler
	{
		readonly Queue<HttpResponseMessage> _responses;

		public RecordingHttpHandler(params HttpResponseMessage[] responses)
			=> _responses = new Queue<HttpResponseMessage>(responses);

		public List<RecordedRequest> Requests { get; } = new List<RecordedRequest>();

		/// <summary>
		/// Parses the body of a recorded request as the Discord message it was.
		/// </summary>
		public JsonElement Message(int index)
			=> JsonDocument.Parse(Requests[index].Body ?? "{}").RootElement.Clone();

		/// <summary>
		/// The single embed of a recorded message.
		/// </summary>
		public JsonElement Embed(int index) => Message(index).GetProperty("embeds")[0];

		/// <summary>
		/// The value of a named field on the single embed of a recorded message.
		/// </summary>
		/// <returns>The field value, or null if the embed has no such field.</returns>
		public string? Field(int index, string name)
		{
			JsonElement embed = Embed(index);

			if (!embed.TryGetProperty("fields", out JsonElement fields))
			{
				return null;
			}

			foreach (JsonElement candidate in fields.EnumerateArray())
			{
				if (candidate.GetProperty("name").GetString() == name)
				{
					return candidate.GetProperty("value").GetString();
				}
			}

			return null;
		}

		/// <summary>
		/// Names of the fields on the single embed of a recorded message, in order.
		/// </summary>
		public IReadOnlyList<string> FieldNames(int index)
		{
			JsonElement embed = Embed(index);

			return embed.TryGetProperty("fields", out JsonElement fields)
				? [.. fields.EnumerateArray().Select(x => x.GetProperty("name").GetString() ?? String.Empty)]
				: [];
		}

		/// <summary>
		/// A response carrying a JSON body.
		/// </summary>
		public static HttpResponseMessage Json(HttpStatusCode statusCode, string body)
			=> new HttpResponseMessage(statusCode)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json"),
			};

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests.Add(new RecordedRequest(
				request.Method.Method,
				request.RequestUri?.ToString() ?? String.Empty,
				request.Headers.Authorization?.ToString(),
				request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

			return _responses.Count > 0 ? _responses.Dequeue() : Json(HttpStatusCode.OK, """{"id":"1","channel_id":"1"}""");
		}
	}

	sealed record RecordedRequest(string Method, string Uri, string? Authorization, string? Body);
}

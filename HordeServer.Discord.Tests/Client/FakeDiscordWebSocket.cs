// Copyright (c) 2026 dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Text.Json;
using System.Threading.Channels;
using HordeServer.Discord.Client;

namespace HordeServer.Discord.Tests.Client
{
	/// <summary>
	/// A gateway socket the test writes both sides of.
	/// </summary>
	/// <remarks>
	/// Frames are scripted up front and read in order; a receive with nothing left parks until the test enqueues
	/// something or the session is cancelled, which is what a real idle socket does. That is what makes the
	/// interesting states reachable - a zombie connection, an <c>INVALID_SESSION</c>, a close code that must not be
	/// resumed - none of which a live gateway can be asked to produce.
	/// </remarks>
	sealed class FakeDiscordWebSocket : IDiscordWebSocket
	{
		readonly Channel<DiscordWebSocketFrame> _incoming = Channel.CreateUnbounded<DiscordWebSocketFrame>();
		readonly List<string> _sent = new List<string>();

		/// <summary>
		/// Whether a heartbeat should be answered, as a healthy gateway would. Turn it off to zombie the connection.
		/// </summary>
		public bool AcknowledgeHeartbeats { get; set; } = true;

		/// <summary>
		/// The URL the gateway connected to.
		/// </summary>
		public Uri? ConnectedTo { get; private set; }

		/// <summary>
		/// Close code the gateway hung up with, or null if it never did.
		/// </summary>
		public int? ClosedWith { get; private set; }

		/// <summary>
		/// Every frame the gateway sent, in order.
		/// </summary>
		public IReadOnlyList<string> Sent
		{
			get
			{
				lock (_sent)
				{
					return _sent.ToArray();
				}
			}
		}

		public Task ConnectAsync(Uri uri, CancellationToken cancellationToken)
		{
			ConnectedTo = uri;
			return Task.CompletedTask;
		}

		public Task SendAsync(string text, CancellationToken cancellationToken)
		{
			lock (_sent)
			{
				_sent.Add(text);
			}

			if (AcknowledgeHeartbeats && OpcodeOf(text) == DiscordGatewayOpcode.Heartbeat)
			{
				EnqueueJson($$"""{"op":{{DiscordGatewayOpcode.HeartbeatAck}},"d":null}""");
			}

			return Task.CompletedTask;
		}

		public async Task<DiscordWebSocketFrame> ReceiveAsync(CancellationToken cancellationToken)
			=> await _incoming.Reader.ReadAsync(cancellationToken);

		public Task CloseAsync(int closeStatus, string? description, CancellationToken cancellationToken)
		{
			ClosedWith = closeStatus;
			return Task.CompletedTask;
		}

		public void Dispose()
		{
		}

		/// <summary>
		/// Queues a frame for the gateway to receive.
		/// </summary>
		public void EnqueueJson(string json) => _incoming.Writer.TryWrite(DiscordWebSocketFrame.Message(json));

		/// <summary>
		/// Queues the socket closing.
		/// </summary>
		/// <param name="closeStatus">Close code, or null for a socket that dropped without one.</param>
		public void EnqueueClose(int? closeStatus) => _incoming.Writer.TryWrite(DiscordWebSocketFrame.Closed(closeStatus));

		/// <summary>
		/// Queues the <c>HELLO</c> every connection opens with.
		/// </summary>
		public void EnqueueHello(int heartbeatIntervalMs = 45000)
			=> EnqueueJson($$$"""{"op":{{{DiscordGatewayOpcode.Hello}}},"d":{"heartbeat_interval":{{{heartbeatIntervalMs}}} }}""");

		/// <summary>
		/// Queues a <c>READY</c>, which is what establishes a session.
		/// </summary>
		public void EnqueueReady(string sessionId = "session-1", string resumeUrl = "wss://resume.example.com", int sequence = 1)
			=> EnqueueJson($$$"""
				{"op":0,"s":{{{sequence}}},"t":"READY","d":{"session_id":"{{{sessionId}}}",
				"resume_gateway_url":"{{{resumeUrl}}}","user":{"id":"999","username":"Horde"} } }
				""");

		/// <summary>
		/// The frames the gateway sent, parsed.
		/// </summary>
		public IReadOnlyList<JsonElement> SentFrames
			=> [.. Sent.Select(x => JsonDocument.Parse(x).RootElement.Clone())];

		/// <summary>
		/// The first frame the gateway sent with the given opcode, or null.
		/// </summary>
		public JsonElement? SentWithOpcode(int opcode)
		{
			foreach (JsonElement frame in SentFrames)
			{
				if (frame.GetProperty("op").GetInt32() == opcode)
				{
					return frame;
				}
			}

			return null;
		}

		/// <summary>
		/// How many frames with the given opcode were sent.
		/// </summary>
		public int CountWithOpcode(int opcode) => SentFrames.Count(x => x.GetProperty("op").GetInt32() == opcode);

		static int OpcodeOf(string json)
		{
			try
			{
				return JsonDocument.Parse(json).RootElement.GetProperty("op").GetInt32();
			}
			catch (JsonException)
			{
				return -1;
			}
		}
	}
}

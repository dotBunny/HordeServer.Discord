// Copyright (c) dotBunny Inc. See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace HordeServer.Discord.Client
{
	/// <summary>
	/// One frame received from the gateway, or the news that it closed.
	/// </summary>
	/// <param name="Text">The frame, or null if the socket closed instead.</param>
	/// <param name="CloseStatus">Close code, when the socket closed. Null when it closed without one.</param>
	/// <param name="CloseDescription">Close reason, when the socket closed.</param>
	public sealed record DiscordWebSocketFrame(string? Text, int? CloseStatus, string? CloseDescription)
	{
		/// <summary>
		/// Whether this is the end of the connection rather than a frame.
		/// </summary>
		public bool IsClose => Text == null;

		/// <summary>
		/// A received frame.
		/// </summary>
		/// <param name="text">Frame body.</param>
		public static DiscordWebSocketFrame Message(string text) => new DiscordWebSocketFrame(text, null, null);

		/// <summary>
		/// The socket closing.
		/// </summary>
		/// <param name="closeStatus">Close code, if there was one.</param>
		/// <param name="description">Close reason, if there was one.</param>
		public static DiscordWebSocketFrame Closed(int? closeStatus, string? description = null)
			=> new DiscordWebSocketFrame(null, closeStatus, description);
	}

	/// <summary>
	/// The websocket the gateway talks over.
	/// </summary>
	/// <remarks>
	/// A seam for the same reason as <see cref="IDiscordClock"/>. The gateway is a state machine whose interesting
	/// states are the ones a real connection will not produce on request - a zombie connection that stops
	/// acknowledging heartbeats, an <c>INVALID_SESSION</c>, a close code that must not be resumed. Against a real
	/// socket those are untestable; against this they are three lines of setup.
	///
	/// Deliberately message-oriented rather than exposing <see cref="ClientWebSocket"/>. Reassembling the continuation
	/// frames a large payload arrives in is the implementation's problem, not the state machine's.
	/// </remarks>
	public interface IDiscordWebSocket : IDisposable
	{
		/// <summary>
		/// Opens the connection.
		/// </summary>
		/// <param name="uri">Gateway URL, already carrying the version and encoding.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		Task ConnectAsync(Uri uri, CancellationToken cancellationToken);

		/// <summary>
		/// Sends one frame.
		/// </summary>
		/// <param name="text">Frame body, which is always JSON here.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		Task SendAsync(string text, CancellationToken cancellationToken);

		/// <summary>
		/// Waits for the next frame, or for the socket to close.
		/// </summary>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		/// <returns>The frame, or a close.</returns>
		Task<DiscordWebSocketFrame> ReceiveAsync(CancellationToken cancellationToken);

		/// <summary>
		/// Closes the connection.
		/// </summary>
		/// <remarks>
		/// The close code decides whether the session survives, which is why it is a parameter rather than always
		/// <c>1000</c>. Closing cleanly with 1000 or 1001 tells Discord to *discard* the session, so a client that
		/// intends to resume must close with something else - 4000 is the conventional choice.
		/// </remarks>
		/// <param name="closeStatus">Close code to send.</param>
		/// <param name="description">Close reason to send.</param>
		/// <param name="cancellationToken">Cancellation token for the operation.</param>
		Task CloseAsync(int closeStatus, string? description, CancellationToken cancellationToken);
	}

	/// <summary>
	/// The real websocket, over <see cref="ClientWebSocket"/>.
	/// </summary>
	public sealed class DiscordClientWebSocket : IDiscordWebSocket
	{
		// Discord's frames are small - a heartbeat ack is 20 bytes - but an INTERACTION_CREATE carrying a populated
		// guild member is not, and READY on a large guild is much bigger again. Rented per receive, grown by
		// continuation rather than by size.
		const int ReceiveBufferSize = 8192;

		readonly ClientWebSocket _socket = new ClientWebSocket();

		/// <inheritdoc/>
		public Task ConnectAsync(Uri uri, CancellationToken cancellationToken) => _socket.ConnectAsync(uri, cancellationToken);

		/// <inheritdoc/>
		public Task SendAsync(string text, CancellationToken cancellationToken)
			=> _socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)), WebSocketMessageType.Text, true, cancellationToken);

		/// <inheritdoc/>
		public async Task<DiscordWebSocketFrame> ReceiveAsync(CancellationToken cancellationToken)
		{
			byte[] buffer = ArrayPool<byte>.Shared.Rent(ReceiveBufferSize);

			try
			{
				using MemoryStream assembled = new MemoryStream();

				while (true)
				{
					ValueWebSocketReceiveResult result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);

					if (result.MessageType == WebSocketMessageType.Close)
					{
						return DiscordWebSocketFrame.Closed((int?)_socket.CloseStatus, _socket.CloseStatusDescription);
					}

					assembled.Write(buffer, 0, result.Count);

					if (result.EndOfMessage)
					{
						return DiscordWebSocketFrame.Message(Encoding.UTF8.GetString(assembled.GetBuffer(), 0, (int)assembled.Length));
					}
				}
			}
			finally
			{
				ArrayPool<byte>.Shared.Return(buffer);
			}
		}

		/// <inheritdoc/>
		public async Task CloseAsync(int closeStatus, string? description, CancellationToken cancellationToken)
		{
			// CloseOutputAsync rather than CloseAsync: the latter waits for the server's close frame, and a socket
			// being abandoned because it stopped responding is precisely one that will not send it.
			if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
			{
				await _socket.CloseOutputAsync((WebSocketCloseStatus)closeStatus, description, cancellationToken);
			}
		}

		/// <inheritdoc/>
		public void Dispose() => _socket.Dispose();
	}
}
